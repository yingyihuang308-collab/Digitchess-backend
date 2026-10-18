# Unity 双线路接入

检查到 Unity 6000.6.0f1、NGO 2.13.2、Multiplayer Services 2.3.1。现有 UnityMpsRoomService 使用 Unity Authentication 匿名登录与 Multiplayer Sessions（包括大厅/Relay），对局通过 NGO CustomMessagingManager 的命名消息传输，没有用大量 RPC/NetworkVariable 实现对局。

## 变更位置（私有 Unity 项目）

- Runtime/Networking/NetworkEntryView：入口线路选择、注册/登录、Relay 本次昵称、重试、上次线路偏好。不保存密码和 token。
- BackendHttp：HTTP 账号、访问令牌轮换、仅内存凭证。
- BackendSocket：原生 ClientWebSocket；WebGL 通过 Plugins/WebGL/DigitChessSocket.jslib。原生与WebGL实现都需实际构建验证。
- SelfHostedRoomService：复用 IRoomService 等现有接口，实现账号独立的房间与消息服务。
- ICommandWire：私有命名消息传输接口。
- UnityRelayCommandGateway：保留现有 NGO 分支，新增可注入 ICommandWire 的分支，复用现有握手、规则验证与结果重放。名称为了现有引用和测试兼容而保留，并非自建服务伪装成 Unity Relay。
- AppFlowController：线路入口、服务清理、身份选择、房间刷新与对局创建。
- GameController：自建断线/恢复期间锁定输入，保留已验证棋盘。
- UnityMpsRoomService：Relay 昵称与带线路邀请解析。

## 操作

打开主菜单 → 联机 → 选择“自建服务器 / Oracle”或“Unity Relay”。

自建线路：填写登录名、密码、昵称，首次选注册并连接，以后登录。后端保留昵称；修改昵称接口已实现，当前游戏入口没有独立的已登录资料编辑页。主菜单显示房间成员昵称，身份仍按玩家ID验证。

Relay 线路：填写本次昵称并连接，使用 Unity 匿名身份，不调用自建后端。Unity Multiplayer Sessions 仍依赖 Unity Authentication、大厅和 Relay，无法覆盖 Unity 平台整体故障。

Resources/DigitChessNetworkEndpoints.json：developmentUrl 用于 Editor/Development Build；productionUrl 必须部署后填为 HTTPS 域名，空值时正式包明确报错。局域网双设备测试将 developmentUrl 改为开发电脑局域网IP，后端仅在明确需要时临时监听该接口，并配置防火墙。不要使用 localhost 让其他设备连接。

WebGL 页面 origin 需加入后端 Cors__Origins__0 等配置。HTTPS 页面不能连接本地 HTTP/WS 混合内容；在本地HTTP页面测试，或配置可信的本地HTTPS代理。WebGL 桥不含账号/棋局逻辑。

点击在线菜单“选择线路 / 重新登录”重新选线；已有房间必须先离开。切换会取消查询、清理对局、连接和事件订阅，不依赖旧线路注销成功。网络请求有超时，失败可重试或选另一条线路。进入线路前不强制执行两种认证。

自建线路同线路断线尝试3次、总窗口30秒；后端宽限默认45秒。原房主进程仍在时可通过私有恢复记录重新核对棋局。房间已经被关闭、服务器重启、房主进程退出、恢复数据过大时需重新建房。重连期间输入锁定。

## 保留限制

不实现跨线路活动房间共享、对局无缝迁移、账号绑定、统一战绩、Unity对局中的房主迁移。线路故障后切换需要重建房间。

真实 Unity Relay 联调需要原项目 UGS 权限、可用项目配置和两个客户端。C#静态编译、后端WebSocket测试不能代替 Unity PlayMode、WebGL构建及真实双客户端验收。请查看 VERIFICATION.md 的实际结果。
