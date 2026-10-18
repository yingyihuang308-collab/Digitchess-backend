# 在已有 Oracle Linux 服务器部署

当前交付不访问你的 OCI 账户，不创建收费资源，也不发布公网服务。本机使用原生 PostgreSQL；Docker/Compose/Caddy 配置尚需在有 Docker 的机器上验收。

## 1. 准备机器

- 用独立 SSH 用户登录，确认 `uname -m` 为 x86_64 或 aarch64；后端不运行 Unity。.NET 10、PostgreSQL17、Caddy2 的镜像须在目标架构上可拉取运行。
- 确认磁盘空间、系统更新、时间同步。通过 Docker 官方文档按实际发行版安装 Docker Engine 与 Compose 插件，不使用来源不明的一键脚本。
- 不把加入 docker 组当普通文件权限操作；该组近似主机管理员权限，由服务器管理员决定。
- 将独立 digitchess-backend 复制到部署目录；不上传私有 Unity 项目。

## 2. 配置秘密与域名

```sh
cd digitchess-backend
mkdir -p .local
chmod 700 .local
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out .local/jwt-private.pem
chmod 600 .local/jwt-private.pem
cp .env.example .env
chmod 600 .env
openssl rand -hex 24
id -u
id -g
```

将随机密码、域名、实际 UID/GID 和 WebGL 页面的 origin 写入 .env。不要发送或提交这些值。BACKEND_UID/GID 必须匹配私钥文件所有者，Compose 用这个非root身份读取只读密钥；确认宿主机及 Docker 的文件权限映射。数据库密码推荐随机十六进制，避免连接字符串中的分号等特殊字符。环境文件本身仍是秘密文件，数据库密码会出现在受管理员保护的容器配置中。

域名 A/AAAA 记录指向服务器实际可用地址；未配置IPv6时不要留下无效AAAA。游戏客户端 productionUrl 改成 `https://实际域名` 后重新构建。修改密钥将使旧 access token 失效，当前版本没有多密钥平滑轮换。

## 3. 网络

OCI 安全列表或 NSG 与主机防火墙两层均需配置：
- TCP443 对玩家开放。
- TCP80 用于 Caddy 证书签发和 HTTPS 重定向。
- TCP22 只允许管理来源。
- 不开放 PostgreSQL5432、本地55432或后端8080。

Compose 中 db 没有公网端口，backend 只在容器网络访问，只有 Caddy 发布80/443。固定 edge 子网172.30.90.0/24，如与现有网络冲突，需要一起修改 IPAM、Caddy 地址和 Proxy__Address。后端只信任该代理传入的协议与来源地址，避免所有玩家共用一个限流桶以及伪造 X-Forwarded-For。

## 4. 构建与迁移

```sh
docker compose config --quiet
docker compose build
docker compose up -d
docker compose ps
docker compose logs --tail=60 migrate backend caddy
curl -f https://实际域名/health/live
curl -f https://实际域名/health/ready
```

migrate 容器等待数据库就绪后执行版本化 EF 迁移，成功后 backend 才启动。失败先修正迁移/凭据，不通过 EnsureCreated 跳过迁移。Caddy 自动提供 HTTPS/WSS，响应头超时20秒不代表 WebSocket 连接寿命20秒；连接使用应用心跳维护。不要添加记录 ticket URL 的访问日志。

WebGL 前端 origin 加入 Cors__Origins__0，可在 compose 中增加 Origins__1 等；不可为了省事开放任意 origin。原生 Unity 客户端没有浏览器 CORS 限制。

随后从外部网络运行集成测试（测试账号只用于测试环境）。集成测试的房主超时检查默认针对5秒测试配置，正式默认45秒需调整等待时间或使用独立测试实例。不要在正式服务上为测试缩短所有玩家的重连窗口。

## 5. 运行与备份

容器配置 restart unless-stopped 与日志轮转。健康检查数据库后还应配置外部监控，检查 HTTPS readiness、磁盘空间、数据库备份结果和 TLS 证书。服务重启会丢失房间，更新应先通知并等待活动房间结束。

```sh
./scripts/backup.sh
```

备份是包含密码哈希的敏感数据。将 .local/backups 下的 dump 加密后复制到独立存储，并定期实际恢复演练；只留在同一台Oracle虚拟机上不算异地备份。备份程序应由现有运维调度每天执行，保留周期按业务确定；本次没有擅自安装系统定时任务。

恢复到一个全新数据库，先停止 backend，保留原库以便回滚：

```sh
docker compose stop backend
docker compose exec -T db createdb -U digitchess digitchess_restore
docker compose exec -T db pg_restore -U digitchess -d digitchess_restore --no-owner < .local/backups/你选择的备份.dump
```

核对恢复库后，修改后端连接配置指向恢复库，重新创建 migrate/backend 容器并检查健康和登录。不要直接覆盖唯一的原库。

## 6. 更新与回滚

更新前保存当前镜像标签、源代码版本、配置和数据库dump。生产镜像应在验收后固定到具体版本/摘要；当前大版本标签是开发交付配置。

先构建新镜像，停止接收新对局，备份数据库，执行迁移，再更新后端，验证健康与登录。每次发布给镜像独立标签，不覆盖唯一旧镜像。

代码回滚只有在数据库结构向后兼容时才可直接切回旧镜像。如果迁移不兼容，先停止写入，恢复到独立数据库并配回旧版；不要盲目运行 down migration。API程序重启和数据库回滚都无法恢复内存房间，玩家必须重新建房。

## 参考

- .NET支持周期：https://dotnet.microsoft.com/platform/support/policy/dotnet-core
- PostgreSQL约束：https://www.postgresql.org/docs/current/ddl-constraints.html
- Unity Relay协议：https://docs.unity.com/relay/relay-message-protocol
- Oracle免费资源资格：https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm
