# HTTP 与实时协议 v1

此仓库只定义账号、房间和传输，不包含游戏规则。原生客户端和 WebGL 使用相同 API。HTTPS/WSS 为正式环境唯一入口。

## 认证

请求与响应采用 camelCase JSON。失败返回 `{code,message}` 与 HTTP 状态码。

| 方法 / 路径 | 请求 | 成功 |
|---|---|---|
| POST /api/auth/register | loginName,password,nickname | 201 玩家资料 |
| POST /api/auth/login | loginName,password | accessToken,expiresIn,refreshToken,user |
| POST /api/auth/refresh | refreshToken | 同登录，替换旧刷新令牌 |
| POST /api/auth/logout | refreshToken | 204 |
| GET /api/me | Bearer accessToken | 玩家资料 |
| PATCH /api/me | nickname | 更新后的本人资料 |
| POST /api/ws-ticket | Bearer，无正文 | ticket,expiresIn=30 |

登录名为 3–32 个 ASCII 字母、数字、下划线，去首尾空格、不区分大小写。昵称 1–32 字符，允许重名，不允许控制字符和尖括号；密码 12–128 字符，不做 trim。密码使用 ASP.NET Core PasswordHasher，账号 ID 为固定 UUID。

access token 为 RS256，有效 10 分钟，允许 10 秒时钟偏差。每个 HTTP 请求核查账号未禁用。refresh token 为随机 32 字节、十六进制编码，数据库只存 SHA256；有效 14 天，轮换。使用 PostgreSQL 按令牌家族的事务 advisory lock 与行锁串行化刷新、注销。复用已撤销刷新令牌会撤销其整个家族，包括刚生成的后代；客户端必须串行刷新，不应盲目重试超时刷新。

注销不撤销已签发的 JWT，最长约 10 分钟仍可用；已建立的 WS 到该 JWT 到期时断开。第一版不提供实时 access token 黑名单。Unity 客户端只在内存中保存凭证，不将密码或令牌写进 PlayerPrefs。

## 房间（均需 Bearer）

| 方法 / 路径 | 请求 / 查询 |
|---|---|
| GET /api/rooms | offset>=0,limit=1..50,search，可选 |
| POST /api/rooms | name,isPrivate,maxPlayers（2..8，Unity 游戏固定2） |
| POST /api/rooms/join | code 或 roomId；私密房需 code |
| GET /api/rooms/current | 当前本人房间或 null |
| GET /api/rooms/{id} | 仅成员 |
| POST /api/rooms/{id}/leave | 无正文 |
| POST /api/rooms/{id}/ready | ready |
| POST /api/rooms/{id}/metadata | metadata：房主提交的不透明字符串，最多8192字符 |
| POST /api/rooms/{id}/start | bootstrap：房主提交的不透明字符串，最多32768字符 |
| POST /api/rooms/{id}/return | 标记本人返回，全部返回后重置等待状态 |
| POST /api/rooms/{id}/kick | userId，房主专用 |
| POST /api/rooms/{id}/close | 房主专用 |

大厅响应 `{rooms:[{id,name,maxPlayers,memberCount,state,version}],nextOffset}`，没有 code、成员、私有配置。房间详情包含 id,code,name,hostUserId,isPrivate,maxPlayers,state,version,bootstrap,metadata,members。成员包含 userId,nickname,seat,ready,returned,online。

房间存内存，单进程同步锁保护容量、成员关系、座位和状态修改。每人最多一个活动房间；加入新成员会清除准备。开始需满员、在线、全部准备。metadata 仅在等待且无人准备时可改。bootstrap、metadata 由私有 Unity 客户端生成，后端不解释、不验证棋局语义。return 只管理房间生命周期，不能证明对局已合法结束。

默认重连宽限45秒（Rooms__ReconnectSeconds）；未建立 WS 的房间成员也会开始离线计时。等待阶段离开/超时会转交给最早加入的在线成员；无可用成员则关闭。对局阶段任何成员主动离开或超时会关闭房间；房主断线立即 paused，宽限内恢复到 playing。空房立即清理。事件版本单调增加，服务器重启后所有房间失效。

## WebSocket

先申请 ticket，再连 `/ws?ticket=...`，30秒、单次兑换。不要将 access token 放到 URL。服务端不输出请求 URL；代理也不启用 access log。部署时若外部基础设施记录 URL，必须脱敏 ticket。

服务端建立后发送 `{protocolVersion:1,type:"connection.ready",userId}`，并推送当前 room.snapshot。同账号新连接替换旧连接；旧连接清理不会将新连接标记离线。

客户端每20秒发送 `{protocolVersion:1,type:"ping",requestId:"..."}`，服务端回应 pong。60秒无任何完整消息会断开。JWT 到期也会断开；重连前刷新 access token 并申请新 ticket。

对局发送示例：

```json
{"protocolVersion":1,"type":"game.message","requestId":"随机唯一编号","roomId":"房间ID","targetUserId":null,"payload":{"name":"私有消息类型","json":"不透明数据"}}
```

普通成员只能发给房主；房主可指定成员或向其余成员广播。发送者从连接确定，客户端填的 senderUserId 不被采用。payload 可为任意 JSON，Unity 当前适配器采用 name/json 包装。转发附加 senderUserId,receivedAt,sequence。sequence 仅为转发次序，不证明游戏规则合法。

成功收到并完成转发入队后，发送者收到 message.ack，包括 requestId,duplicate。相同用户、房间、requestId 在2分钟窗口内不再次转发；最多4096条去重记录，满后返回429。去重不跨房间重启、不持久化，不保证 exactly-once。ack 不保证对方应用已经处理。

事件：connection.ready、lobby.roomAdded/Updated/Removed、room.snapshot、room.memberJoined/Left、room.readyChanged、room.started/paused/closed、game.message、message.ack、pong、error。room 事件的 payload 为完整房间快照（closed 可仅含 reason）；大厅事件仅含公开概要。客户端发现版本跳跃重新 GET 快照。

文本消息上限96KiB，支持分片，二进制和超限消息断开连接；每连接每秒30条，持续超频断开。每连接发送队列最多64条，满后断开慢客户端。单连接发送串行。普通 HTTP 每来源每分钟180次；认证20次；ticket 每用户每分钟10次。共享公网出口会共享 IP 限额，正式上线按实测调整。

## 恢复与线路

后端不保存、重放对局消息。私有客户端通过房主保留的初始快照和已接受操作记录恢复，并验证最终指纹。为限制内存/网络，恢复记录解压上限2MiB，压缩编码后的单条私有消息上限64KiB；超限提示重建房间。历史仅存在房主进程内，房主进程被关闭后不能恢复；重连不等于跨进程房主迁移。

邀请格式 `DC1:S:<自建房间码>` 或 `DC1:R:<Unity加入码>`。原始无前缀代码按当前所选线路处理。不同线路邀请提示先切换，不自动退出活动房间，不包含登录凭据。
