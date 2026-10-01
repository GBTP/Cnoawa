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

    public void AddPlayer(NodeConnection conn) => _host.AddPlayer(conn);

    public void RemovePlayer(NodeConnection conn) => _host.RemovePlayer(conn);

    public void HandleMessage(NodeConnection sender, MessageType type, byte[] payload)
        => _host.HandleMessage(sender, type, payload);

    public void HandleJoin(NodeConnection conn) => _host.HandleJoin(conn);

    public void HandleLeaveRoom(NodeConnection sender)
    {
        // 原 NodeRoom.HandleLeaveRoom: RemovePlayer + sender.SendEmpty(LeaveRoom)
        _host.HandleLeaveRoom(sender);
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
