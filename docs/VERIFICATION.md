# 实际验证记录

日期：2026-10-17（America/Los_Angeles）。本机 macOS ARM64，.NET SDK10.0.401 / Runtime10.0.12，PostgreSQL17.11_1。

## 已实际执行

- 后端.NET构建成功，0警告、0错误。
- EF Core InitialAccounts 迁移实际应用到本地PostgreSQL。
- tests/Integration真实连接127.0.0.1:5080与PostgreSQL，37项检查通过：
  - 数据库readiness；3个账号注册和登录。
  - 密码错误、并发重名、伪造token、正确签名但已过期token。
  - 本人昵称更新与他人资料不受影响。
  - 私密房间不在大厅暴露；私密房不能只用ID绕过邀请。
  - 两个加入请求竞争最后一个座位；容量和座位唯一。
  - 非成员不能读取房间、转发消息；普通成员不能开始对局。
  - 满员在线准备后开始。
  - senderUserId不可伪造；普通成员不能任意广播；房主可向成员路由。
  - requestId重复确认、不重复转发。
  - 新连接替换旧连接，旧连接清理不误删新连接。
  - 房主断线paused、重连playing、5秒测试宽限后关闭房间。
  - refresh轮换、重放撤销整个家族、注销阻止刷新。
  - ticket单次使用；消息频率限制；超大WS消息断开。
- 额外并发刷新探针通过3项检查：新登录创建会话、同一令牌并发刷新只有一个成功、复用撤销新签发后代。
- 重启PostgreSQL与后端后，probe-persistence验证相同玩家ID、修改后的昵称和密码登录保留。
- 使用pg_dump生成本地备份并恢复到独立数据库digitchess_restore_check，恢复的8个测试用户与制作备份时的原库一致。未覆盖原库。
- 直接查询数据库聚合结果：所有用户密码字段符合成熟哈希格式；刷新令牌均为64位十六进制哈希。未输出密码或哈希内容。
- 用Unity项目实际程序集和编译参数运行C#编译器：Rules、Runtime、PlayModeTests编译通过；新增网络类及恢复/邀请测试源已纳入编译。
- 对Runtime额外编译UNITY_WEBGL非Editor分支通过。这是C#分支检查，不是WebGL/IL2CPP构建。

编译发现的警告来自已有代码：ChoosePlacement过时枚举、StoryDeveloperSidebar未使用字段/开发宏、原测试的AppDomain.GetAssemblies用法。新增后端没有构建警告。

## 尚未执行 / 具体阻碍

- Unity Test Runner：尝试batchmode运行EditMode，被现有已打开的同项目Unity实例拒绝。尝试访问编辑器界面时，工具报告没有Computer Use权限。没有关闭用户现有编辑器、没有绕过该权限，也没有将编译成功记作测试通过。
- 新增SelfHostedGatewayTests的真实Unity执行：已编译，未运行。覆盖注入通道握手、离线期间漏收落子后的恢复重放、恢复后继续对局、邀请线路与版本校验。
- 两个真实Unity客户端的完整对局与原有PlayMode回归。
- 真实UnityRelay联机及Unity服务故障切换：需要上述Unity运行环境及UGS服务权限。
- WebGL构建、JS插件链接、浏览器CORS与实际WSS握手。
- DockerCompose/Caddy启动与证书：本机没有Docker；本次以原生PostgreSQL完成后端运行验证。
- Oracle公网部署、目标架构镜像运行、异地备份调度和Oracle环境恢复演练：尚无可用账户/服务器目标。

## 后续人工/编辑器验收

在现有Unity Test Runner中执行SelfHostedGatewayTests及完整PlayMode套件；进入主菜单“联机”，两个实例分别登录自建账号并建房对局。再用两个实例选择Relay并对局。故障演练只停止所测试线路，确认另一条线路的登录和建房没有依赖它。对局恢复必须检查棋盘指纹、准备/返回房间、悔棋和重开状态，而不能只看WS重新连上。

默认正式/本地交互服务的重连窗口为45秒；集成测试启动的专用进程使用5秒。第一版没有跨线路对局迁移、账号绑定和房主进程退出后的状态恢复。
