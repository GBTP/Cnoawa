# Cnoawa - Community Node of Anoawa

Anoawa 社区音游的联机游戏节点服务器。社区成员可自行部署，为玩家提供低延迟的联机对战服务。

## 什么是 Cnoawa？

[Anoawa](https://github.com/GBTP/Anoawa) 是一款基于 Arcaea 玩法的社区音乐游戏。Cnoawa 是它的联机节点服务器——玩家创建房间后，游戏客户端直接通过 TCP 连接到 Cnoawa 节点进行实时对战。

任何人都可以部署自己的 Cnoawa 节点，为社区提供联机服务。

## 特性

- **去中心化架构**：多个社区节点并存，玩家可选择延迟最低的节点
- **本地 JWT 验签**：注册时获取主 API 的 RSA 公钥，后续玩家认证完全在本地完成，不依赖主 API
- **完整房间生命周期**：大厅 → 投票选曲 → 下载谱面 → 游戏同步 → 结算
- **竞技/娱乐双模式**：娱乐模式支持技能系统（干扰、增益、对抗）
- **自动更新**：通过 `run.sh` 脚本循环运行，管理员可远程触发更新

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 一个可从公网访问的 TCP 端口
- Anoawa 管理员提供的节点注册密钥

## 快速开始

### 1. 获取注册密钥

联系 Anoawa 管理员，在管理面板的「联机管理」中创建节点注册密钥。

### 2. 构建并运行

```bash
git clone https://github.com/GBTP/Cnoawa.git
cd Cnoawa

# 直接运行
dotnet run --project src/Cnoawa/Cnoawa.csproj -- \
  7776 \
  https://api.anoawa.com \
  你的公网地址 \
  "节点名称" \
  "欢迎来玩" \
  "你的注册密钥"
```

### 3. 生产部署（推荐）

使用 `run.sh` 脚本自动更新运行：

```bash
chmod +x run.sh
./run.sh 7776 https://api.anoawa.com 你的公网地址 "节点名称" "欢迎来玩" "你的注册密钥"
```

脚本会自动 `git pull` → 构建 → 运行，进程退出后自动拉取最新代码并重启。管理员可通过后台远程触发节点更新。

注册密钥也可通过环境变量传入：

```bash
export NODE_REGISTRATION_KEY="你的注册密钥"
./run.sh 7776 https://api.anoawa.com 你的公网地址 "节点名称" "欢迎来玩"
```

## 参数说明

| 参数 | 必填 | 说明 |
|------|------|------|
| `port` | 是 | TCP 监听端口 |
| `apiUrl` | 是 | 主 API 地址 |
| `publicAddress` | 是 | 本节点公网地址（IP 或域名），客户端通过此地址连接 |
| `name` | 否 | 节点名称，显示给玩家（默认：`节点-主机名`） |
| `message` | 否 | 节点寄语（默认：`欢迎`） |
| `registrationKey` | 否 | 注册密钥，也可通过 `NODE_REGISTRATION_KEY` 环境变量传入 |

## 架构概览

```
主 API（云端）              Cnoawa 节点（社区部署）           游戏客户端
├── 节点注册/心跳      ←→  ├── TCP 监听               ←→  ├── TCP 长连接
├── 房间列表聚合            ├── JWT 本地验签                ├── 认证
└── 房间分配                ├── 房间状态机                  └── 实时游戏消息
                            └── 心跳上报
```

### 房间状态机

```
Lobby → ChartSelect → Downloading → Playing → Lobby（循环）
```

- **Lobby**：玩家加入/离开、准备、房主设置
- **ChartSelect**：30 秒投票选曲，全员投完或超时后随机抽取
- **Downloading**：各客户端下载谱面并上报进度，全员就绪后倒计时开始
- **Playing**：实时同步 combo/score，娱乐模式可释放技能
- 游戏结束后广播排名，房间自动回到 Lobby

### TCP 协议

基于 MemoryPack 二进制序列化的自定义帧协议：

```
[4字节 帧长度 uint32 LE][1字节 MessageType][MemoryPack payload]
```

- 最大帧 64KB
- 15 秒 Ping/Pong 心跳保活
- 30 秒无响应自动断开

## 网络要求

- 节点必须有公网可达的 TCP 端口（不能在 NAT 后面，除非做了端口映射）
- 注册时主 API 会通过 TCP 探测验证节点可达性
- 建议使用固定 IP 或 DDNS 域名

## 许可证

MIT License
