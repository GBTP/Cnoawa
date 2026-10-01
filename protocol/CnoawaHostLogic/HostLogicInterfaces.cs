// 联机房主逻辑的传输与序列化抽象。
//
// 本文件是共享代码：服务端（Cnoawa .NET 10）和 Unity 客户端各有一份，必须逐字节一致。
// 同步纪律与 CnoawaProtocol 相同：改这里后原样复制到
// Anoawa/Assets/Plugins/CnoawaHostLogic/HostLogicInterfaces.cs。
//
// 语法限制：必须兼容 C# 9（Unity）。禁用集合表达式 = []、required 修饰符、
// 文件作用域命名空间。数组属性用 nullable T[]? 声明。
using System;
using MemoryPack;

namespace Cnoawa.Host
{
    /// <summary>
    /// 序列化器抽象。默认实现是 <see cref="MemoryPackSerializerWrapper"/>，
    /// 留这个接口是为了可注入（测试可换 JSON，将来可换别的二进制方案）。
    /// </summary>
    public interface ISerializer
    {
        byte[] Serialize<T>(T message) where T : class;
        T Deserialize<T>(byte[] payload) where T : class;
    }

    /// <summary>
    /// <see cref="ISerializer"/> 的默认实现，包装 MemoryPack 静态方法。
    /// 放共享程序集是因为两边（服务端 + Unity）都引了 MemoryPack 1.21.4，
    /// 同一份包装避免漂移。MemoryPack 无状态，直接转发静态调用。
    /// </summary>
    public sealed class MemoryPackSerializerWrapper : ISerializer
    {
        public byte[] Serialize<T>(T message) where T : class
            => MemoryPackSerializer.Serialize(message);

        public T Deserialize<T>(byte[] payload) where T : class
            => MemoryPackSerializer.Deserialize<T>(payload);
    }
    /// <summary>
    /// 客户端侧传输通道：非房主用。一条到「权威方」的连接——
    /// 社区线是到节点，TapTap 线是到房主（经 TapTap 中转），LAN 线是到房主（直连）。
    ///
    /// 签名与现有 <c>TcpTransport</c> 完全匹配，<c>TcpTransport</c> 加 <c>: ITransport</c> 即可实现，零行为改动。
    /// 入站消息（<see cref="OnDataReceived"/>）必须在主线程 <see cref="Tick"/> 里触发，
    /// 这样上层不用关心 transport 内部的线程模型。
    /// </summary>
    public interface ITransport
    {
        bool IsConnected { get; }

        /// <summary>连接建立。在主线程 Tick 里触发。</summary>
        event Action OnConnected;

        /// <summary>连接断开。在主线程 Tick 里触发。</summary>
        event Action OnDisconnected;

        /// <summary>
        /// 入站消息。type 是 <c>(byte)MessageType</c>，payload 是已解帧的 MemoryPack 字节。
        /// 在主线程 Tick 里触发。
        /// </summary>
        event Action<byte, byte[]> OnDataReceived;

        /// <summary>
        /// 社区线用地址+端口连接；TapTap/LAN 线在构造时已建立连接，
        /// 调这个方法可抛 <see cref="NotSupportedException"/>。
        /// </summary>
        void Connect(string address, ushort port);

        void Disconnect();

        /// <summary>
        /// 出站：发送一个已序列化的消息。实现负责加自己的帧格式
        /// （社区线 [4字节长度][1字节type][payload]，TapTap 线 JSON 封套）。
        /// </summary>
        void Send(byte messageType, byte[] payload);

        void SendEmpty(byte messageType);

        /// <summary>主线程每帧调用，把后台线程/SDK 回调里收到的包派发到 <see cref="OnDataReceived"/>。</summary>
        void Tick();
    }

    /// <summary>
    /// 房主侧玩家句柄。HostLogic 用它代表房间里的一个玩家。
    /// 社区线由 NodeConnection 实现，LAN 线由 LanHostPlayer 实现，TapTap 线由 TapTapHostPlayer 实现。
    /// </summary>
    public interface IHostPlayer
    {
        /// <summary>协议内 byte 玩家 ID（1-255，房间内自增分配）。TapTap 线由房主把 string openId 映射到 byte。</summary>
        byte PlayerId { get; set; }

        /// <summary>Bnoawa 业务用户 ID。TapTap 线房主在成员加入时查 BnoawaAuth 补全。</summary>
        int UserId { get; }

        /// <summary>transport 内部句柄 ID，用于 SendToAllExcept 排除。社区线是 ConnId(int)，其他线可复用 PlayerId。</summary>
        int TransportId { get; }

        /// <summary>发送已序列化的消息。实现负责加帧/封套。</summary>
        void SendRaw(byte messageType, byte[] payload);

        void SendEmpty(byte messageType);

        void SendError(int code, string message);

        /// <summary>踢人。社区线=CloseAfterSend+关 TCP，TapTap 线=KickRoomPlayer，LAN 线=关 socket。</summary>
        void Kick();
    }

    /// <summary>
    /// 房主侧广播能力。HostLogic 持有它来扇出消息给成员。
    /// 社区线 TcpHostTransport 遍历连接，TapTap 线 TapTapHostTransport 走 SendCustomMessage，LAN 线遍历 TCP。
    /// </summary>
    public interface IHostTransport
    {
        /// <summary>
        /// 广播给所有成员。
        /// TapTap 线 SendCustomMessage(type=0) 天然不含发送者（房主），但房主自己需要收到
        /// StateChange/Countdown 等自己广播的消息——TapTap 实现要本地投递一份给房主。
        /// 社区线节点模式房主不是 _players 一员，无此问题。
        /// </summary>
        void SendToAll(byte messageType, byte[] payload);

        /// <summary>
        /// 广播给除 <paramref name="excludeTransportId"/> 外的所有人。
        /// 约定 <paramref name="excludeTransportId"/> == -1 表示「无人排除」（房主也要收到自己广播）。
        /// TapTap 线 type=0 天然排除房主；若 exclude == -1 或 exclude != 房主自己的 TransportId，
        /// 则房主本地投递一份。
        /// </summary>
        void SendToAllExcept(int excludeTransportId, byte messageType, byte[] payload);

        /// <summary>定向发送给指定玩家。</summary>
        void SendTo(IHostPlayer player, byte messageType, byte[] payload);

        /// <summary>踢人。委托给 <see cref="IHostPlayer.Kick"/>。</summary>
        void KickPlayer(IHostPlayer player);
    }

    /// <summary>
    /// 随机选曲数据源。NodeRoom 原 FinalizeVoteRandomAsync 用 HttpClient 调主 API，
    /// 抽象出来让共享 HostLogic 不依赖 HttpClient（Unity 客户端房主侧走 BnoawaSDK）。
    /// </summary>
    public interface IRandomLevelProvider
    {
        /// <summary>返回 count 个候选谱面。返回 null/空数组表示无可用谱面。</summary>
        System.Threading.Tasks.Task<RandomLevelItem[]?> GetRandomLevelsAsync(int count);
    }

    /// <summary>随机选曲候选条目。</summary>
    public class RandomLevelItem
    {
        public int Id { get; set; }
        public string LevelName { get; set; } = "";
    }

    /// <summary>
    /// 替换 NodeRoom 里的 Random.Shared（.NET 6+ ThreadStatic 单例，Unity Mono 不存在）。
    /// 用一把锁包一个 Random，跨线程安全。HostLogic 的状态变更都在 _stateLock 内，
    /// 调用方持锁时 Next 不会与其他线程竞争，但保留锁以防万一。
    /// </summary>
    public static class ThreadSafeRandom
    {
        static readonly Random _random = new Random();
        static readonly object _lock = new object();

        public static int Next(int maxValue)
        {
            lock (_lock)
            {
                return _random.Next(maxValue);
            }
        }
    }

    /// <summary>
    /// 房间信息（共享版）。替代服务端 GameNode.cs 里的 RoomInfo（给主 API 心跳用）
    /// 和 TapTap 的 RoomInfo（SDK 实体），避免同名冲突。服务端 ApiRegistration 负责把它
    /// 映射成心跳用的私有 RoomInfo。
    /// </summary>
    public class HostRoomInfo
    {
        public int RoomId { get; set; }
        public string RoomName { get; set; } = "";
        public int HostUserId { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public string Status { get; set; } = "Lobby";
        public bool IsPrivate { get; set; }
        public int? SelectedLevelId { get; set; }
        public string? SelectedLevelName { get; set; }
    }
}
