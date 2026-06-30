using System.Net.Sockets;
using System.Threading.Channels;
using CnoawaProtocol;
using MemoryPack;

namespace Cnoawa;

public class NodeConnection
{
    readonly int _connId;
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly GameNode _node;
    readonly Channel<byte[]> _sendQueue = Channel.CreateBounded<byte[]>(512);
    readonly CancellationTokenSource _cts = new();

    DateTime _lastRecv = DateTime.UtcNow;
    const int HeartbeatTimeoutSec = 30;
    const int PingIntervalSec = 15;

    public int ConnId => _connId;
    public bool IsAuthenticated { get; private set; }
    public int UserId { get; private set; }
    public int? AuthorizedRoomId { get; private set; }
    public byte PlayerId { get; set; }
    public volatile NodeRoom? CurrentRoom;

    public NodeConnection(int connId, TcpClient tcp, GameNode node)
    {
        _connId = connId;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _node = node;
    }

    public async Task RunAsync(CancellationToken externalCt)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, _cts.Token);
        var ct = linkedCts.Token;

        var writeTask = WriteLoop(ct);
        var pingTask = PingLoop(ct);
        var authTimeoutTask = AuthTimeoutAsync(ct);

        try
        {
            var headerBuf = new byte[4];
            var payloadBuf = new byte[65536];

            while (!ct.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadFrameAsync(_stream, headerBuf, payloadBuf, ct);
                if (frame == null) break;

                _lastRecv = DateTime.UtcNow;
                var (type, payload) = frame.Value;

                if (type == (MessageType)0xAA && payload.Length >= 2 && payload[0] == 0x55 && payload[1] == 0x01)
                {
                    Send(FrameCodec.Encode((MessageType)0xAA, new byte[] { 0x55, 0x02 }));
                    CloseAfterSend();
                    break;
                }

                HandleMessage(type, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cnoawa] 连接 #{_connId} 异常: {ex.Message}");
        }
        finally
        {
            if (!_gracefulClose)
                Close();
            _node.RemoveConnection(_connId);
            await writeTask;
            await pingTask;
            await authTimeoutTask;
        }
    }

    void HandleMessage(MessageType type, byte[] payload)
    {
        try
        {
            switch (type)
            {
                case MessageType.Auth:
                    HandleAuth(payload);
                    break;
                case MessageType.Ping:
                    Send(FrameCodec.EncodeEmpty(MessageType.Pong));
                    break;
                case MessageType.Pong:
                    break;
                default:
                    if (!IsAuthenticated)
                    {
                        SendError(401, "未认证");
                        return;
                    }
                    if (type == MessageType.LeaveRoom && CurrentRoom != null)
                    {
                        CurrentRoom.HandleLeaveRoom(this);
                        return;
                    }
                    if (CurrentRoom != null)
                        CurrentRoom.HandleMessage(this, type, payload);
                    else
                        HandleLobbyMessage(type, payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cnoawa] #{_connId} 消息处理异常 ({type}): {ex.Message}");
        }
    }

    async Task AuthTimeoutAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(5000, ct);
            if (!IsAuthenticated)
            {
                Console.WriteLine($"[Cnoawa] #{_connId} 认证超时，断开");
                Close();
            }
        }
        catch (OperationCanceledException) { }
    }

    void HandleAuth(byte[] payload)
    {
        if (IsAuthenticated) return;

        var msg = MemoryPackSerializer.Deserialize<AuthMessage>(payload);
        if (msg == null || string.IsNullOrEmpty(msg.Token))
        {
            SendMessage(MessageType.AuthResult, new AuthResultMessage { Success = false, Reason = "无效的认证消息" });
            return;
        }

        const byte CurrentProtocolVersion = 1;
        if (msg.ProtocolVersion != CurrentProtocolVersion)
        {
            SendMessage(MessageType.AuthResult, new AuthResultMessage
            {
                Success = false,
                Reason = $"协议版本不匹配，需要 v{CurrentProtocolVersion}，收到 v{msg.ProtocolVersion}。请更新客户端。"
            });
            return;
        }

        var result = _node.ValidateToken(msg.Token);
        if (result == null)
        {
            SendMessage(MessageType.AuthResult, new AuthResultMessage { Success = false, Reason = "Token 无效或已过期" });
            return;
        }

        IsAuthenticated = true;
        UserId = result.Value.userId;
        AuthorizedRoomId = result.Value.roomId;

        _node.DisconnectExistingUser(UserId, _connId);

        SendMessage(MessageType.AuthResult, new AuthResultMessage
        {
            Success = true,
            UserId = UserId,
            PlayerId = PlayerId
        });

        Console.WriteLine($"[Cnoawa] #{_connId} 认证成功: userId={UserId}");
    }

    void HandleLobbyMessage(MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.CreateRoom:
                var create = MemoryPackSerializer.Deserialize<CreateRoomMessage>(payload);
                if (create == null) return;
                if (AuthorizedRoomId == null)
                {
                    SendError(403, "连接令牌中未包含房间ID");
                    return;
                }
                var maxPlayers = Math.Clamp(create.MaxPlayers, 2, 32);
                var roomName = string.IsNullOrEmpty(create.RoomName) ? "未命名房间" : create.RoomName.Length > 50 ? create.RoomName[..50] : create.RoomName;
                var room = _node.CreateRoom(AuthorizedRoomId.Value, roomName, maxPlayers, create.IsPrivate, create.Password, this);
                if (room == null)
                {
                    SendError(409, "房间ID已存在");
                    return;
                }
                room.AddPlayer(this);
                SendMessage(MessageType.CreateRoomResult, new CreateRoomResultMessage { Success = true });
                break;

            case MessageType.JoinRoom:
                var join = MemoryPackSerializer.Deserialize<JoinRoomMessage>(payload);
                if (join == null) return;
                var target = _node.GetRoom(join.RoomId);
                if (target == null)
                {
                    SendMessage(MessageType.JoinRoomResult, new JoinRoomResultMessage { Success = false, Reason = "房间不存在" });
                    return;
                }
                target.HandleJoin(this, join.Password);
                break;

            default:
                SendError(400, "未加入房间");
                break;
        }
    }

    public void SendMessage<T>(MessageType type, T message) where T : class
    {
        var payload = MemoryPackSerializer.Serialize(message);
        var frame = FrameCodec.Encode(type, payload);
        Send(frame);
    }

    public void SendRaw(MessageType type, byte[] payload)
    {
        Send(FrameCodec.Encode(type, payload));
    }

    public void SendEmpty(MessageType type)
    {
        Send(FrameCodec.EncodeEmpty(type));
    }

    public void SendError(int code, string message)
    {
        SendMessage(MessageType.Error, new ErrorMessage { Code = code, Message = message });
    }

    void Send(byte[] frame)
    {
        if (!_sendQueue.Writer.TryWrite(frame))
        {
            Console.WriteLine($"[Cnoawa] #{_connId} 发送队列满，断开连接");
            Close();
        }
    }

    async Task WriteLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _sendQueue.Reader.ReadAllAsync(ct))
            {
                await _stream.WriteAsync(frame, ct);
            }
        }
        catch { }

        if (_gracefulClose)
        {
            await Task.Delay(50);
            Close();
        }
    }

    async Task PingLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingIntervalSec * 1000, ct);

                if ((DateTime.UtcNow - _lastRecv).TotalSeconds > HeartbeatTimeoutSec)
                {
                    Console.WriteLine($"[Cnoawa] #{_connId} 心跳超时，断开");
                    Close();
                    return;
                }

                Send(FrameCodec.EncodeEmpty(MessageType.Ping));
            }
        }
        catch { }
    }

    volatile bool _gracefulClose;

    public void CloseAfterSend()
    {
        _gracefulClose = true;
        _sendQueue.Writer.TryComplete();
    }

    public void Close()
    {
        _cts.Cancel();
        _sendQueue.Writer.TryComplete();
        try { _tcp.Close(); } catch { }
    }
}
