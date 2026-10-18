# DigitChess 通用后端

独立 .NET10 / PostgreSQL17 后端：账号、房间、WebSocket消息转发。没有棋局、走法、GameState 或私有项目代码。Unity 双线路改动保留在父目录的私有 Assets 中，发布后端时仅发布本目录。

## 当前机器直接运行

已安装 .NET SDK10 与 Homebrew PostgreSQL17。项目专用数据在 `.local/pgdata`，只监听127.0.0.1:55432，使用SCRAM密码认证，未设置开机启动。真实秘密只在被忽略的 `.local` 内，不写入本文。

```sh
cd '/Users/huangyingyi/Documents/工作/digitchess-backend'
./scripts/init-local.sh
./scripts/run-local.sh
```

后端地址 http://127.0.0.1:5080；另一个终端检查：

```sh
curl -f http://127.0.0.1:5080/health/ready
```

如果5080已有本次启动的服务，不要重复启动。Ctrl+C停止前台后端；数据库单独停止：

```sh
/opt/homebrew/opt/postgresql@17/bin/pg_ctl -D .local/pgdata -m fast stop
```

init-local.sh幂等，不覆盖已有数据库、密码或私钥。若本机 PostgreSQL 不在默认路径，传入 PG_BIN。不要删除 `.local` 来解决启动错误，它包含账号数据库与秘密。

## 其他机器

安装 .NET10 SDK、PostgreSQL17、OpenSSL。macOS Homebrew：`brew install postgresql@17`。Linux按发行版安装官方PostgreSQL包，指定PG_BIN，或用Docker启动数据库。

Docker数据库替代方式：

```sh
cp .env.example .env
# 在.env中填随机POSTGRES_PASSWORD
docker compose -f compose.local.yaml up -d db
```

Docker数据库与原生数据库都占用55432，只选一种；它们的数据卷彼此独立。使用Docker时自行生成本地RSA私钥，并设置 `ConnectionStrings__Database`、`Auth__PrivateKeyPath`、`ASPNETCORE_ENVIRONMENT=Development`、`ASPNETCORE_URLS=http://127.0.0.1:5080`，不要再运行原生init脚本。然后：

```sh
dotnet restore src/Backend
dotnet run --project src/Backend -- --migrate
dotnet run --no-build --project src/Backend
```

所有环境都显式执行迁移。更新schema时：

```sh
dotnet tool install --tool-path .local/tools dotnet-ef --version 10.0.12
.local/tools/dotnet-ef migrations add 描述名称 --project src/Backend
```

迁移源文件提交仓库；真实私钥、数据库数据、密码和备份不提交。

## 双客户端集成测试

使用单独终端，以5秒测试宽限启动后端（正式默认45秒）：

```sh
. .local/local.env
export Rooms__ReconnectSeconds=5
dotnet run --project src/Backend
```

再运行：

```sh
dotnet run --project tests/Integration
```

测试创建随机账号和房间，验证真实数据库、HTTP与WebSocket。测试中会断开自己的连接、关闭自己的房间；只对本地测试服务运行。默认读取本地私钥生成一个正确签名但已过期的测试JWT，以核查过期校验；不会输出私钥。

测试结束后重启后端和PostgreSQL，再运行：

```sh
python3 scripts/probe-persistence.py
```

并发刷新可另行运行 `python3 scripts/probe-refresh-concurrency.py`。

持久化探针使用 `.local/persistence-probe.json` 的随机测试账号，检查固定ID、昵称和密码登录在重启后仍有效。不用于正式账号。

## 文档

- [协议与数据行为](docs/PROTOCOL.md)
- [Unity接入与线路切换](docs/UNITY.md)
- [Oracle部署、备份与回滚](docs/DEPLOYMENT.md)
- [实际验证结果与限制](docs/VERIFICATION.md)

第一版为单实例，房间存在内存中；重启需重新建房。昵称可改但不能用作账号身份；没有找回密码、邮箱验证、第三方登录、跨线路账号绑定和战绩共享。房主负责规则判断，仍有房主作弊风险。跨线路切换需要重新建房，不是对局无缝迁移。
