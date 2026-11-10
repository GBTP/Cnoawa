using System.Linq;
using Cnoawa.Host;
using CnoawaProtocol;

namespace Cnoawa;

/// <summary>
/// 薄壳：服务端专用的房间管理器，委托给共享的 <see cref="HostLogic"/>。
///
/// 原状态机逻辑已搬到 CnoawaHostLogic/HostLogic.cs。本类保留是因为：
///  - GameNode 持有 NodeRoom 字典，按 roomId 索引
///  - NodeConnection.CurrentRoom 指向 NodeRoom，路由消息到 HandleMessage/HandleLeaveRoom
///  - 服务端要给主 API 心跳上报房间列表（GetInfo 返回 HostRoomInfo，ApiRegistration 再映射成心跳用的 RoomInfo）
///
/// 行为与原 NodeRoom 逐字节一致：构造时创建 HostLogic + TcpHostTransport + HttpRandomLevelProvider，
/// 把 NodeConnection（已实现 IHostPlayer）作为玩家句柄传入。
/// </summary>
public class NodeRoom : IDisposable
{
    readonly HostLogic _host;
    readonly TcpHostTransport _hostTransport;
    readonly HttpRandomLevelProvider _randomLevelProvider;
    bool _disposed;

    public int RoomId => _host.RoomId;
    public string RoomName => _host.RoomName;
    public int MaxPlayers => _host.MaxPlayers;
    public bool IsPrivate => _host.IsPrivate;
    public NodeConnection? Creator => (NodeConnection?)_host.Creator;
    public RoomState State => _host.State;
    public RoomType RoomType => _host.RoomType;
    public int? SelectedLevelId => _host.SelectedLevelId;
    public string? SelectedLevelName => _host.SelectedLevelName;
    public bool IsEmpty => _host.IsEmpty;
    public int PlayerCount => _host.PlayerCount;
    public Action? OnStateChanged { get; set; }
    public Action<int>? OnRoomEmpty { get; set; }

    public NodeRoom(int roomId, string roomName, int maxPlayers, bool isPrivate, NodeConnection creator, string apiUrl, string nodeToken)
    {
        _randomLevelProvider = new HttpRandomLevelProvider(apiUrl, nodeToken);
        _hostTransport = new TcpHostTransport();
        _host = new HostLogic(roomId, roomName, maxPlayers, isPrivate, creator,
            new MemoryPackSerializerWrapper(), _hostTransport, _randomLevelProvider);
        // 鸡生蛋解决：HostLogic 构造完后再把 host 引用注入 transport
        _hostTransport.Host = _host;
        _host.OnStateChanged = () => OnStateChanged?.Invoke();
        _host.OnRoomEmpty = id => OnRoomEmpty?.Invoke(id);
    }

    public void AddPlayer(NodeConnection conn)
    {
        _host.AddPlayer(conn);
        // 这一行是 NodeConnection 路由房间消息的唯一依据。统一核心重构把它从
        // NodeRoom.AddPlayerInternal 搬丢后，所有建房/加入的玩家都会停在 Lobby 路由，
        // 后续消息被当成「未加入房间」，断线时也不会从房间成员里移除。
        conn.CurrentRoom = this;
    }

    public void RemovePlayer(NodeConnection conn)
    {
        _host.RemovePlayer(conn);
        // 踢人/主动离开/连接断开都会走到这里；只有真的不在成员表里才清引用，
        // 避免 HostLogic 因状态机拒绝移除时把仍在房间里的连接误标成不在房间。
        if (!_host.Players.Contains(conn))
            conn.CurrentRoom = null;
    }

    public void HandleMessage(NodeConnection sender, MessageType type, byte[] payload)
        => _host.HandleMessage(sender, type, payload);

    public void HandleJoin(NodeConnection conn)
    {
        _host.HandleJoin(conn);
        // HandleJoin 可能因房间已满/已在游戏中拒绝；只有真进了成员表才算加入成功。
        if (_host.Players.Contains(conn))
            conn.CurrentRoom = this;
    }

    public void HandleLeaveRoom(NodeConnection sender)
    {
        // 原 NodeRoom.HandleLeaveRoom: RemovePlayer + sender.SendEmpty(LeaveRoom)
        _host.HandleLeaveRoom(sender);
        if (!_host.Players.Contains(sender))
            sender.CurrentRoom = null;
    }

    public HostRoomInfo GetInfo() => _host.GetInfo();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 服务端特有清理：和原 NodeRoom 一致，先断开每个连接（CurrentRoom=null + Close 强制关），
        // 再让 HostLogic 清状态。原版用 Close（强制关），不是 CloseAfterSend（优雅关）。
        foreach (var p in _host.Players)
        {
            if (p is NodeConnection conn)
            {
                conn.CurrentRoom = null;
                conn.Close();
            }
        }
        _host.Dispose();
    }
}
