using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

var url=args.FirstOrDefault()??"http://127.0.0.1:5080";
var run=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
var http=new HttpClient{BaseAddress=new Uri(url)};
var password="LocalTest_"+Guid.NewGuid().ToString("N");
var results=new List<string>();
void Assert(bool ok,string label){if(!ok)throw new Exception("FAIL: "+label);results.Add(label);Console.WriteLine("PASS "+label);}
async Task<(int Status,JsonElement Json)> Call(string method,string path,object? data=null,string? token=null)
{
    using var req=new HttpRequestMessage(new HttpMethod(method),path);
    if(data!=null)req.Content=JsonContent.Create(data);
    if(token!=null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
    using var res=await http.SendAsync(req);var text=await res.Content.ReadAsStringAsync();
    return ((int)res.StatusCode,string.IsNullOrWhiteSpace(text)?default:JsonDocument.Parse(text).RootElement.Clone());
}
async Task<(string Id,string Token,string Refresh)> User(string suffix)
{
    var name="t"+run+suffix;
    var register=await Call("POST","/api/auth/register",new {loginName=name,password,nickname=suffix});
    Assert(register.Status==201,"register "+suffix);
    var login=await Call("POST","/api/auth/login",new {loginName=name,password});
    Assert(login.Status==200,"login "+suffix);
    return (login.Json.GetProperty("user").GetProperty("id").GetString()!,login.Json.GetProperty("accessToken").GetString()!,login.Json.GetProperty("refreshToken").GetString()!);
}
async Task<ClientWebSocket> Socket(string token)
{
    var ticket=await Call("POST","/api/ws-ticket",new {},token);
    if(ticket.Status!=200)throw new Exception("ticket failed "+ticket.Status);
    var ws=new ClientWebSocket();using var timeout=new CancellationTokenSource(5000);
    await ws.ConnectAsync(new Uri(url.Replace("http:","ws:").Replace("https:","wss:")+"/ws?ticket="+ticket.Json.GetProperty("ticket").GetString()),timeout.Token);
    await Until(ws,"connection.ready");return ws;
}
async Task<JsonElement> Until(ClientWebSocket ws,string type,int milliseconds=5000)
{
    using var timeout=new CancellationTokenSource(milliseconds);
    var bytes=new byte[16384];
    while(true) {
        using var stream=new MemoryStream();WebSocketReceiveResult part;
        do {part=await ws.ReceiveAsync(bytes,timeout.Token);if(part.MessageType==WebSocketMessageType.Close)throw new Exception("Socket closed");stream.Write(bytes,0,part.Count);}while(!part.EndOfMessage);
        var json=JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        if(json.GetProperty("type").GetString()==type)return json;
    }
}
async Task Send(ClientWebSocket ws,object value)=>await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),WebSocketMessageType.Text,true,CancellationToken.None);
Assert((await Call("GET","/health/ready")).Status==200,"database readiness");
var a=await User("host");var b=await User("guest");var c=await User("outsider");
Assert((await Call("POST","/api/auth/login",new{loginName="t"+run+"host",password="bad"})).Status==401,"wrong password rejected");
var dup=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Call("POST","/api/auth/register",new{loginName="race"+run,password,nickname="race"})));
Assert(dup.Count(x=>x.Status==201)==1&&dup.Count(x=>x.Status==409)==1,"concurrent unique registration");
Assert((await Call("GET","/api/me",token:"forged")).Status==401,"forged token rejected");
if (File.Exists(".local/jwt-private.pem")) {
    string B64(byte[] data)=>Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_');
    using var rsa=RSA.Create();rsa.ImportFromPem(File.ReadAllText(".local/jwt-private.pem"));
    var header=B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
    var payload=B64(JsonSerializer.SerializeToUtf8Bytes(new{sub=a.Id,iss="digitchess",aud="digitchess-client",exp=DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds()}));
    var signature=B64(rsa.SignData(Encoding.UTF8.GetBytes(header+"."+payload),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
    Assert((await Call("GET","/api/me",token:header+"."+payload+"."+signature)).Status==401,"correctly signed expired token rejected");
}

Assert((await Call("PATCH","/api/me",new{nickname="本地玩家",userId=b.Id},a.Token)).Json.GetProperty("id").GetString()==a.Id,"nickname update uses authenticated identity");
Assert((await Call("GET","/api/me",token:b.Token)).Json.GetProperty("nickname").GetString()=="guest","other user unchanged");
using var wa=await Socket(a.Token);using var wb=await Socket(b.Token);using var wc=await Socket(c.Token);
var room=await Call("POST","/api/rooms",new{name="integration",isPrivate=true,maxPlayers=2},a.Token);
Assert(room.Status==200,"create private room");var id=room.Json.GetProperty("id").GetString();var code=room.Json.GetProperty("code").GetString();
Assert(!(await Call("GET","/api/rooms",token:c.Token)).Json.GetProperty("rooms").EnumerateArray().Any(x=>x.GetProperty("id").GetString()==id),"private room omitted from lobby");
Assert((await Call("POST","/api/rooms/join",new{roomId=id},c.Token)).Status==403,"private ID cannot bypass invitation");
var joining=await Task.WhenAll(Call("POST","/api/rooms/join",new{code},b.Token),Call("POST","/api/rooms/join",new{code},c.Token));
Assert(joining.Count(x=>x.Status==200)==1&&joining.Count(x=>x.Status==409)==1,"concurrent join respects capacity");
var guest=joining[0].Status==200?b:c;var other=joining[0].Status==200?c:b;var wg=joining[0].Status==200?wb:wc;var wo=joining[0].Status==200?wc:wb;
var snapshot=await Call("GET","/api/rooms/"+id,token:a.Token);
Assert(snapshot.Json.GetProperty("members").EnumerateArray().Select(m=>m.GetProperty("seat").GetInt32()).Distinct().Count()==2,"unique seats");
Assert((await Call("GET","/api/rooms/"+id,token:other.Token)).Status==403,"nonmember snapshot denied");
Assert((await Call("POST","/api/rooms/"+id+"/start",new{bootstrap="opaque"},guest.Token)).Status==403,"guest cannot start");
await Call("POST","/api/rooms/"+id+"/ready",new{ready=true},a.Token);await Call("POST","/api/rooms/"+id+"/ready",new{ready=true},guest.Token);
Assert((await Call("POST","/api/rooms/"+id+"/start",new{bootstrap="opaque-protocol-test"},a.Token)).Status==200,"ready room starts");
await Send(wo,new{protocolVersion=1,type="game.message",requestId="outsider",roomId=id,payload=new{test=1}});
Assert((await Until(wo,"error")).GetProperty("code").GetString()=="not_member","nonmember relay denied");
await Send(wg,new{protocolVersion=1,type="game.message",requestId="g1",roomId=id,senderUserId=a.Id,payload=new{test="hello"}});
var forwarded=await Until(wa,"game.message");
Assert(forwarded.GetProperty("senderUserId").GetString()==guest.Id,"sender identity cannot be forged");
await Until(wg,"message.ack");
await Send(wg,new{protocolVersion=1,type="game.message",requestId="g1",roomId=id,payload=new{test="hello"}});
Assert((await Until(wg,"message.ack")).GetProperty("duplicate").GetBoolean(),"duplicate request acknowledged without retransmission");
await Send(wg,new{protocolVersion=1,type="game.message",requestId="bad-route",roomId=id,targetUserId=other.Id,payload=new{test=1}});
Assert((await Until(wg,"error")).GetProperty("code").GetString()=="host_route_only","guest cannot broadcast to arbitrary targets");
await Send(wa,new{protocolVersion=1,type="game.message",requestId="h1",roomId=id,targetUserId=guest.Id,payload=new{test="result"}});
Assert((await Until(wg,"game.message")).GetProperty("senderUserId").GetString()==a.Id,"host result routed to member");
using var replacement=await Socket(guest.Token);await Task.Delay(200);
Assert((await Call("GET","/api/rooms/"+id,token:a.Token)).Json.GetProperty("members").EnumerateArray().Single(m=>m.GetProperty("userId").GetString()==guest.Id).GetProperty("online").GetBoolean(),"old connection cleanup preserves replacement");
wa.Abort();await Task.Delay(300);
Assert((await Call("GET","/api/rooms/"+id,token:guest.Token)).Json.GetProperty("state").GetString()=="paused","host disconnect pauses room");
using var hostBack=await Socket(a.Token);
Assert((await Call("GET","/api/rooms/"+id,token:a.Token)).Json.GetProperty("state").GetString()=="playing","host reconnect resumes room envelope");
// Expiry case: test server must run with Rooms__ReconnectSeconds=5.
hostBack.Abort();await Task.Delay(8000);
Assert((await Call("GET","/api/rooms/"+id,token:guest.Token)).Status==404,"host timeout closes room (5-second test configuration)");
var refreshed=await Call("POST","/api/auth/refresh",new{refreshToken=a.Refresh});
Assert(refreshed.Status==200,"refresh rotates token");
Assert((await Call("POST","/api/auth/refresh",new{refreshToken=a.Refresh})).Status==401,"old refresh token rejected");
Assert((await Call("POST","/api/auth/refresh",new{refreshToken=refreshed.Json.GetProperty("refreshToken").GetString()})).Status==401,"refresh reuse revokes descendant");
await Call("POST","/api/auth/logout",new{refreshToken=b.Refresh});
Assert((await Call("POST","/api/auth/refresh",new{refreshToken=b.Refresh})).Status==401,"logout prevents refresh");
var oneTime=await Call("POST","/api/ws-ticket",new{},c.Token);
var address=new Uri(url.Replace("http:","ws:").Replace("https:","wss:")+"/ws?ticket="+oneTime.Json.GetProperty("ticket").GetString());
using var once=new ClientWebSocket();await once.ConnectAsync(address,CancellationToken.None);await Until(once,"connection.ready");
using var twice=new ClientWebSocket();bool rejected=false;try{await twice.ConnectAsync(address,CancellationToken.None);}catch(WebSocketException){rejected=true;}Assert(rejected,"websocket ticket is single-use");
// Test rate limiting before closing this connection.
for(int i=0;i<35;i++)await Send(once,new{protocolVersion=1,type="ping",requestId="rate"+i});
Assert((await Until(once,"error")).GetProperty("code").GetString()=="message_rate","websocket message rate enforced");
using var oversized=await Socket(c.Token);
await Send(oversized,new{protocolVersion=1,type="game.message",requestId="big",roomId=id,payload=new{data=new string('x',100*1024)}});
bool bigClosed=false;try{await Until(oversized,"message.ack",1500);}catch(Exception e)when(e is WebSocketException or OperationCanceledException || e.Message=="Socket closed"){bigClosed=true;}
Assert(bigClosed,"oversized websocket message disconnects");
Directory.CreateDirectory(".local");
await File.WriteAllTextAsync(".local/persistence-probe.json",JsonSerializer.Serialize(new{loginName="t"+run+"host",password,nickname="本地玩家",id=a.Id}));
Console.WriteLine($"Completed {results.Count} checks. Real HTTP + PostgreSQL + WebSocket; no Unity/Relay integration claimed.");
