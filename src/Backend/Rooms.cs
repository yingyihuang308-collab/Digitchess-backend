using System.Security.Cryptography;

namespace Backend;
public sealed record CreateRoomInput(string? Name, bool IsPrivate = false, int MaxPlayers = 2);
public sealed record JoinInput(string? Code, string? RoomId);
public sealed record ReadyInput(bool Ready);
public sealed record StartInput(string? Bootstrap);
public sealed record MetadataInput(string? Metadata);
public sealed record KickInput(Guid UserId);
public sealed class Member
{
    public Guid UserId { get; init; }
    public string Nickname { get; set; } = "";
    public int Seat { get; init; }
    public bool Ready { get; set; }
    public bool Returned { get; set; }
    public DateTimeOffset? OfflineSince { get; set; }
}
public sealed class Room
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public Guid HostUserId { get; set; }
    public bool IsPrivate { get; init; }
    public int MaxPlayers { get; init; }
    public string State { get; set; } = "waiting";
    public long Version { get; set; } = 1;
    public long Sequence { get; set; }
    public string? Bootstrap { get; set; }
    public string? Metadata { get; set; }
    public List<Member> Members { get; } = [];
    public Dictionary<string, DateTimeOffset> Requests { get; } = [];
}
public sealed class Rooms
{
    public readonly object Gate = new();
    private readonly Dictionary<string, Room> rooms = [];
    private readonly Dictionary<Guid, string> membership = [];
    public event Action<Guid[]?, object>? Emit;
    public static object Snapshot(Room r) => new { r.Id, r.Code, r.Name, r.HostUserId, r.IsPrivate, r.MaxPlayers, r.State, r.Version, r.Bootstrap, r.Metadata,
        members = r.Members.Select(m => new { m.UserId, m.Nickname, m.Seat, m.Ready, m.Returned, online = m.OfflineSince == null }).ToArray() };
    static object Public(Room r) => new { r.Id, r.Name, r.MaxPlayers, memberCount = r.Members.Count, r.State, r.Version };
    void Send(Room r, string type, Guid[]? recipients = null) => Emit?.Invoke(recipients ?? r.Members.Select(m => m.UserId).ToArray(), new { protocolVersion = 1, type, roomId = r.Id, payload = Snapshot(r) });
    void Changed(Room r, string type)
    {
        r.Version++; Send(r, type); Send(r, "room.snapshot");
        if (!r.IsPrivate) Emit?.Invoke(null, new { protocolVersion = 1, type = "lobby.roomUpdated", roomId = r.Id, payload = Public(r) });
    }
    public object List(int offset, int limit, string? search)
    {
        lock (Gate) {
            Check.That(offset >= 0 && offset <= 100000 && limit is >= 1 and <= 50 && (search?.Length ?? 0) <= 64, 400, "invalid_page", "分页参数无效");
            var all = rooms.Values.Where(r => !r.IsPrivate && (search == null || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))).OrderBy(r => r.Id).ToArray();
            return new { rooms = all.Skip(offset).Take(limit).Select(Public).ToArray(), nextOffset = offset + limit < all.Length ? (int?)(offset + limit) : null };
        }
    }
    public object Create(Guid user, string nickname, CreateRoomInput input, bool online)
    {
        lock (Gate) {
            Check.That(!membership.ContainsKey(user), 409, "already_in_room", "请先离开原房间");
            Check.That(input.MaxPlayers is >= 2 and <= 8 && rooms.Count < 1000, 400, "room_limit", "房间人数或服务器房间数量超限");
            string code;
            do { code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)); } while (rooms.Values.Any(r => r.Code == code));
            var r = new Room { Code = code, Name = Check.Text(input.Name, 1, 48, "房间名"), HostUserId = user, IsPrivate = input.IsPrivate, MaxPlayers = input.MaxPlayers };
            r.Members.Add(new Member { UserId = user, Nickname = nickname, Seat = 0, OfflineSince = online ? null : DateTimeOffset.UtcNow });
            rooms.Add(r.Id, r); membership.Add(user, r.Id);
            if (!r.IsPrivate) Emit?.Invoke(null, new { protocolVersion = 1, type = "lobby.roomAdded", roomId = r.Id, payload = Public(r) });
            return Snapshot(r);
        }
    }
    public object Join(Guid user, string nickname, JoinInput input, bool online)
    {
        lock (Gate) {
            Check.That((input.Code?.Length ?? 0) <= 32 && (input.RoomId?.Length ?? 0) <= 64, 400, "invalid_input", "房间参数无效");
            var r = input.RoomId != null ? rooms.GetValueOrDefault(input.RoomId) : rooms.Values.FirstOrDefault(x => x.Code == input.Code?.Trim().ToUpperInvariant());
            Check.That(r != null, 404, "room_not_found", "房间不存在");
            Check.That(!r!.IsPrivate || input.Code?.Trim().ToUpperInvariant() == r.Code || membership.GetValueOrDefault(user) == r.Id, 403, "invite_required", "私密房间需要房间码");
            if (membership.TryGetValue(user, out var old)) { Check.That(old == r!.Id, 409, "already_in_room", "请先离开原房间"); return Snapshot(r!); }
            Check.That(r!.State == "waiting" && r.Members.Count < r.MaxPlayers, 409, "room_unavailable", "房间不可加入");
            var seat = Enumerable.Range(0, r.MaxPlayers).First(s => r.Members.All(m => m.Seat != s));
            r.Members.Add(new Member { UserId = user, Nickname = nickname, Seat = seat, OfflineSince = online ? null : DateTimeOffset.UtcNow }); membership[user] = r.Id;
            foreach (var m in r.Members) m.Ready = false;
            Changed(r, "room.memberJoined"); return Snapshot(r);
        }
    }
    Room Require(Guid user, string id, bool host = false)
    {
        Check.That(rooms.TryGetValue(id, out var r), 404, "room_not_found", "房间不存在");
        Check.That(r!.Members.Any(m => m.UserId == user), 403, "not_member", "你不是房间成员");
        Check.That(!host || r.HostUserId == user, 403, "host_only", "只有房主可以操作"); return r;
    }
    public object Get(Guid user, string id) { lock (Gate) return Snapshot(Require(user, id)); }
    public object? Current(Guid user) { lock (Gate) return membership.TryGetValue(user, out var id) ? Snapshot(rooms[id]) : null; }
    public object Ready(Guid user, string id, bool ready)
    { lock (Gate) { var r = Require(user,id); Check.That(r.State == "waiting",409,"not_waiting","房间不在等待状态"); r.Members.Single(m=>m.UserId==user).Ready=ready; Changed(r,"room.readyChanged"); return Snapshot(r); } }
    public object Metadata(Guid user, string id, string? data)
    { lock(Gate) { var r=Require(user,id,true); Check.That(r.State=="waiting" && r.Members.All(m=>!m.Ready),409,"not_editable","请先取消准备"); Check.That((data?.Length??0)<=8192,400,"metadata_too_large","配置过大"); r.Metadata=data; Changed(r,"room.snapshot"); return Snapshot(r); } }
    public object Start(Guid user, string id, string? bootstrap)
    {
        lock(Gate) { var r=Require(user,id,true);
            Check.That(r.State=="waiting" && r.Members.Count==r.MaxPlayers && r.Members.All(m=>m.Ready && m.OfflineSince==null),409,"not_ready","成员尚未全部上线并准备");
            Check.That(bootstrap is { Length: >0 and <=32768 },400,"invalid_bootstrap","启动消息无效");
            r.Bootstrap=bootstrap; r.State="playing"; foreach(var m in r.Members) m.Returned=false; r.Requests.Clear();
            Changed(r,"room.started"); return Snapshot(r);
        }
    }
    public object Return(Guid user, string id)
    { lock(Gate) { var r=Require(user,id); r.Members.Single(m=>m.UserId==user).Returned=true; if(r.Members.All(m=>m.Returned)) { r.State="waiting"; foreach(var m in r.Members) m.Ready=false; } Changed(r,"room.snapshot"); return Snapshot(r); } }
    public void Leave(Guid user, string id)
    {
        lock(Gate) { var r=Require(user,id); Remove(r,user); }
    }
    public void Kick(Guid user,string id,Guid target)
    { lock(Gate) {var r=Require(user,id,true); Check.That(target!=user && r.Members.Any(m=>m.UserId==target),400,"invalid_target","成员无效"); Remove(r,target); } }
    public void Close(Guid user,string id) {lock(Gate) CloseRoom(Require(user,id,true));}
    void Remove(Room r,Guid user)
    {
        Emit?.Invoke([user],new {protocolVersion=1,type="room.closed",roomId=r.Id,payload=new {reason="left_or_kicked"}}); r.Members.RemoveAll(m=>m.UserId==user); membership.Remove(user);
        if(r.Members.Count==0 || r.State!="waiting") { CloseRoom(r); return; }
        if(r.HostUserId==user) { var next=r.Members.FirstOrDefault(m=>m.OfflineSince==null); if(next==null) { CloseRoom(r);return;} r.HostUserId=next.UserId; }
        foreach(var m in r.Members) m.Ready=false;
        Changed(r,"room.memberLeft");
    }
    void CloseRoom(Room r)
    {
        r.State="closed"; r.Version++; Send(r,"room.closed");
        foreach(var m in r.Members) membership.Remove(m.UserId);
        rooms.Remove(r.Id);
        if(!r.IsPrivate) Emit?.Invoke(null,new {protocolVersion=1,type="lobby.roomRemoved",roomId=r.Id,payload=new {r.Id}});
    }
    public void Presence(Guid user,bool online)
    {
        lock(Gate) {
            if(!membership.TryGetValue(user,out var id)) return;
            var r=rooms[id]; var m=r.Members.Single(m=>m.UserId==user); m.OfflineSince=online?null:DateTimeOffset.UtcNow;
            if(user==r.HostUserId && r.State=="playing" && !online) r.State="paused";
            if(user==r.HostUserId && r.State=="paused" && online) r.State="playing";
            Changed(r,r.State=="paused"?"room.paused":"room.snapshot");
        }
    }
    public void Sweep(TimeSpan grace)
    { lock(Gate) foreach(var r in rooms.Values.ToArray()) { if(!rooms.ContainsKey(r.Id))continue; foreach(var m in r.Members.Where(m=>m.OfflineSince!=null && DateTimeOffset.UtcNow-m.OfflineSince>grace).ToArray()) { if(!rooms.ContainsKey(r.Id))break; Remove(r,m.UserId); } } }
    public void Relay(Guid user, WireInput input)
    {
        lock(Gate) {
            var r=Require(user,input.RoomId??"");
            Check.That(r.State=="playing",409,"not_playing","对局未进行或已暂停");
            Check.That(input.RequestId is {Length:>0 and <=64},400,"invalid_request_id","请求编号无效");
            Guid[] recipients;
            if(user==r.HostUserId) {
                if(input.TargetUserId!=null) { Check.That(Guid.TryParse(input.TargetUserId,out var target) && r.Members.Any(m=>m.UserId==target) && target!=user,400,"invalid_target","接收人无效"); recipients=[Guid.Parse(input.TargetUserId)]; }
                else recipients=r.Members.Where(m=>m.UserId!=user).Select(m=>m.UserId).ToArray();
            } else { Check.That(input.TargetUserId==null || input.TargetUserId==r.HostUserId.ToString(),403,"host_route_only","只能发送给房主"); recipients=[r.HostUserId]; }
            foreach(var key in r.Requests.Where(x=>DateTimeOffset.UtcNow-x.Value>TimeSpan.FromMinutes(2)).Select(x=>x.Key).ToArray())r.Requests.Remove(key);
            var keyId=user+":"+input.RequestId;
            if(r.Requests.ContainsKey(keyId)) { Emit?.Invoke([user],new {protocolVersion=1,type="message.ack",requestId=input.RequestId,duplicate=true});return; }
            Check.That(r.Requests.Count<4096,429,"dedup_full","请求过多，请稍后重试");
            r.Requests[keyId]=DateTimeOffset.UtcNow;
            Emit?.Invoke(recipients,new {protocolVersion=1,type="game.message",roomId=r.Id,requestId=input.RequestId,senderUserId=user,sequence=++r.Sequence,receivedAt=DateTimeOffset.UtcNow,payload=input.Payload});
            Emit?.Invoke([user],new {protocolVersion=1,type="message.ack",requestId=input.RequestId,duplicate=false});
        }
    }
}
public sealed class RoomCleanup(Rooms rooms,IConfiguration config):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    { using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2)); while(await timer.WaitForNextTickAsync(token)) rooms.Sweep(TimeSpan.FromSeconds(config.GetValue("Rooms:ReconnectSeconds",45))); }
}
