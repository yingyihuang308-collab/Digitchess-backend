# 实施记录与文件边界

A 环境：检查Unity版本/Packages、现有UnityMpsRoomService与UnityRelayCommandGateway。项目使用Plastic SCM，当前根目录不是Git仓库，未运行提交/发布。
B 账号：Data/Auth/迁移、RSA JWT、成熟PasswordHasher、刷新家族锁与重放保护。
C 房间：Rooms，单实例锁、公开列表与私密邀请、在线准备、权限、超时、返回房间。
D 转发：Realtime，短时单次ticket、角色路由、去重、限流、有界队列、心跳。
E 私有Unity：Networking目录、AppFlowController、UnityMpsRoomService、UnityRelayCommandGateway、GameController和RoomInfo昵称字段。复用规则，不复制至后端。
F 测试：37项真实协议/数据库检查、重启持久化、Unity分支与测试源码编译；完整Unity执行受现有编辑器实例/界面权限阻碍。
G 运维：Compose、Dockerfile、Caddy、本地脚本、部署/协议/Unity/验收文档。

没有修改云账户、创建收费实例、发布公网服务或推送公开仓库。后端可单独复制发布，私有Unity源码不能一起发布。
