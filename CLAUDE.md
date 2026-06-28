# Cnoawa - Community Node of Anoawa

Anoawa 社区音游的多人游戏节点服务器。社区成员可自行部署，为玩家提供联机服务。

## 架构

服务器权威模型：所有玩家（包括房间创建者）都是纯客户端，连接到游戏节点。节点负责房间管理和游戏状态机。

```
主 API（云）               Cnoawa 节点（社区部署）            客户端（Unity）
├── 节点注册/心跳      ←→  ├── TCP 监听                  ←→  ├── TCP 连接
├── 房间列表（聚合）        ├── Token 验证（问主API）          ├── 认证
└── 房间分配               ├── 房间管理                      └── 收发游戏消息
                           └── 游戏状态机
```

## 项目结构

```
Cnoawa/
├── protocol/AnoawaProtocol/   # 共享协议（复制到 Unity）
│   ├── MessageType.cs          # 枚举：MessageType, RoomState, RoomType, SkillId
│   ├── Messages.cs             # MemoryPack 消息类（30+）
│   └── FrameCodec.cs           # TCP 帧编解码（4字节长度前缀）
└── src/Cnoawa/                # 节点服务器
    ├── Program.cs              # 入口
    ├── GameNode.cs             # TCP 监听 + 连接/房间管理 + Token 验证
    ├── NodeConnection.cs       # 每连接：认证、心跳、消息路由
    ├── NodeRoom.cs             # 房间状态机 + 游戏逻辑
    └── ApiRegistration.cs      # 主 API 注册/心跳/注销
```

## 构建与运行

```bash
dotnet build src/Cnoawa/Cnoawa.csproj
dotnet run --project src/Cnoawa/Cnoawa.csproj -- <port> <apiUrl> <publicAddress> [name] [message]

# 示例
dotnet run --project src/Cnoawa/Cnoawa.csproj -- 7776 https://api.anoawa.com mynode.ddns.net "社区节点" "欢迎来玩"
```

参数：
- `port` — TCP 监听端口
- `apiUrl` — 主 API 地址
- `publicAddress` — 本节点公网地址（IP 或域名，用于客户端连接）
- `name` — 节点名称（显示给玩家）
- `message` — 节点寄语

## TCP 帧协议

```
[4字节 帧长度 uint32 LE][1字节 MessageType][MemoryPack payload]
```

- 帧长度不含自身 4 字节
- 最大帧 64KB
- 无 payload 的消息：帧长度 = 1

## 节点注册流程

1. 启动时向主 API `POST /api/nodes/register` 注册
2. 主 API 通过 TCP 探测验证可达性（发 `[0xAA,0x55,0x01]`，期望回 `[0xAA,0x55,0x02]`）
3. 注册成功后每 30 秒心跳上报（房间列表 + 活跃连接数）
4. 关闭时 `DELETE /api/nodes/{id}` 注销

## 玩家认证

节点不做本地 token 验证。收到客户端 token 后向主 API `GET /api/auth/profile` 验证：
- 200 → 认证成功，获取 userId/nickname/avatarUrl
- 401 → token 无效

## 房间状态机

```
Lobby → ChartSelect → Downloading → Playing → Results → Lobby（循环）
```

- **Lobby**：玩家加入/准备
- **ChartSelect**：投票选曲，全员投完随机抽取
- **Downloading**：各客户端下载谱面，全员就绪后 3 秒倒计时
- **Playing**：转发 ComboUpdate→PlayerSync，技能释放→技能效果，收集 PlayerFinished
- **Results**：全员完成后广播排名

## 协议同步

修改 `protocol/AnoawaProtocol/` 后必须同步复制到 Unity 项目的 `Assets/Plugins/AnoawaProtocol/`。

## 通用规范

- Commit 信息使用中文
- 不得直接 git commit，需先征得用户同意
