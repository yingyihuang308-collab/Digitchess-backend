using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Backend;
public sealed record WireInput(int ProtocolVersion,string? Type,string? RequestId,string? RoomId,JsonElement Payload,string? TargetUserId);
public sealed class Realtime(Rooms rooms)
{
    public const int MaxBytes=96*1024;
    private readonly Dictionary<Guid,Peer> peers=[];
    private readonly Dictionary<string,(Guid User,DateTimeOffset Expiry,DateTimeOffset SessionExpiry)> tickets=[];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public void Initialize() => rooms.Emit+=Publish;
    public object Ticket(Guid user,DateTimeOffset sessionExpiry)
    {
        lock(rooms.Gate) {
            foreach(var key in tickets.Where(x=>x.Value.Expiry<DateTimeOffset.UtcNow).Select(x=>x.Key).ToArray())tickets.Remove(key);
            Check.That(tickets.Count<4096,429,"ticket_limit","连接请求过多");
            var ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            tickets[Accounts.Hash(ticket)]=(user,DateTimeOffset.UtcNow.AddSeconds(30),sessionExpiry);
            return new {ticket,expiresIn=30};
        }
    }
    public bool Online(Guid user) {lock(rooms.Gate)return peers.ContainsKey(user);}
    void Publish(Guid[]? targets,object value)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Json);
        foreach(var p in peers.Values.Where(p=>targets==null || targets.Contains(p.User)).ToArray())p.Enqueue(bytes);
    }
    public async Task Handle(HttpContext context)
    {
        if(!context.WebSockets.IsWebSocketRequest)throw new ApiError(400,"websocket_required","需要 WebSocket 连接");
        var ticket=context.Request.Query["ticket"].ToString();
        Check.That(ticket.Length==64,401,"invalid_ticket","连接凭证无效");
        (Guid User,DateTimeOffset Expiry,DateTimeOffset SessionExpiry) entry;
        lock(rooms.Gate) {Check.That(tickets.Remove(Accounts.Hash(ticket),out entry) && entry.Expiry>DateTimeOffset.UtcNow,401,"invalid_ticket","连接凭证无效或已使用");}
        using var socket=await context.WebSockets.AcceptWebSocketAsync();
        using var peer=new Peer(entry.User,socket,context.RequestAborted,entry.SessionExpiry);
        lock(rooms.Gate) {
            if(peers.TryGetValue(peer.User,out var old))old.Stop();
            peers[peer.User]=peer;
            peer.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new {protocolVersion=1,type="connection.ready",userId=peer.User},Json));
            rooms.Presence(peer.User,true);
            var current=rooms.Current(peer.User);
            if(current!=null)peer.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new {protocolVersion=1,type="room.snapshot",payload=current},Json));
        }
        var sender=peer.SendLoop();
        try {
            var buffer=new byte[8192];
            while(!peer.Token.IsCancellationRequested && socket.State==WebSocketState.Open) {
                using var message=new MemoryStream();
                WebSocketReceiveResult chunk;
                do {
                    chunk=await socket.ReceiveAsync(buffer,peer.Token);
                    if(chunk.MessageType==WebSocketMessageType.Close)return;
                    if(chunk.MessageType!=WebSocketMessageType.Text || message.Length+chunk.Count>MaxBytes) { peer.Stop(); return; }
                    message.Write(buffer,0,chunk.Count);
                }while(!chunk.EndOfMessage);
                peer.LastSeen=DateTimeOffset.UtcNow;
                try {
                    peer.CountMessage();
                    var input=JsonSerializer.Deserialize<WireInput>(message.ToArray(),Json) ?? throw new JsonException();
                    Check.That(input.ProtocolVersion==1,400,"protocol_mismatch","协议版本不兼容");
                    lock(rooms.Gate) {
                        Check.That(peers.GetValueOrDefault(peer.User)==peer,409,"connection_replaced","连接已替换");
                        if(input.Type=="ping")peer.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new {protocolVersion=1,type="pong",requestId=input.RequestId},Json));
                        else {Check.That(input.Type=="game.message",400,"unknown_message","消息类型无效");rooms.Relay(peer.User,input);}
                    }
                }catch(ApiError e) {peer.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new {protocolVersion=1,type="error",code=e.Code,message=e.Message},Json));}
                catch(JsonException){peer.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new {protocolVersion=1,type="error",code="invalid_json",message="消息格式无效"},Json));}
            }
        }catch(Exception e)when(e is OperationCanceledException or WebSocketException) { }
        finally {
            lock(rooms.Gate) if(peers.GetValueOrDefault(peer.User)==peer) {peers.Remove(peer.User);rooms.Presence(peer.User,false);}
            peer.Stop(); await sender;
        }
    }
    public void Sweep()
    {lock(rooms.Gate)foreach(var p in peers.Values) if(DateTimeOffset.UtcNow-p.LastSeen>TimeSpan.FromSeconds(60))p.Stop();}
    sealed class Peer:IDisposable
    {
        public Guid User {get;}
        readonly WebSocket socket;
        readonly CancellationTokenSource cancel;
        readonly Channel<byte[]> outgoing=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        public CancellationToken Token=>cancel.Token;
        public DateTimeOffset LastSeen=DateTimeOffset.UtcNow;
        DateTimeOffset window=DateTimeOffset.UtcNow;
        int count;
        public Peer(Guid user,WebSocket socket,CancellationToken token,DateTimeOffset expiry)
        {User=user;this.socket=socket;cancel=CancellationTokenSource.CreateLinkedTokenSource(token);cancel.CancelAfter(expiry>DateTimeOffset.UtcNow?expiry-DateTimeOffset.UtcNow:TimeSpan.Zero);}
        public void Enqueue(byte[] bytes) {if(!outgoing.Writer.TryWrite(bytes))Stop();}
        public void CountMessage(){if(DateTimeOffset.UtcNow-window>TimeSpan.FromSeconds(1)){window=DateTimeOffset.UtcNow;count=0;}++count;if(count>60)Stop();Check.That(count<=30,429,"message_rate","消息发送过快");}
        public async Task SendLoop()
        {try {await foreach(var b in outgoing.Reader.ReadAllAsync(Token))await socket.SendAsync(b,WebSocketMessageType.Text,true,Token);}catch(Exception e)when(e is OperationCanceledException or WebSocketException){} }
        public void Stop(){cancel.Cancel();socket.Abort();outgoing.Writer.TryComplete();}
        public void Dispose(){cancel.Dispose();}
    }
}
public sealed class RealtimeCleanup(Realtime rt):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));while(await timer.WaitForNextTickAsync(token))rt.Sweep();}
}
