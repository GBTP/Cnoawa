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
    readonly Channel<byte[]> _sendQueue = Channel.CreateBounded<byte[]>(256);
    readonly CancellationTokenSource _cts = new();

    DateTime _lastRecv = DateTime.UtcNow;
    const int HeartbeatTimeoutSec = 30;
    const int PingIntervalSec = 15;

    public int ConnId => _connId;
    public bool IsAuthenticated { get; private set; }
    public int UserId { get; private set; }
    public byte PlayerId { get; set; }
    public NodeRoom? CurrentRoom { get; set; }

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
            Close();
            _node.RemoveConnection(_connId);
            await writeTask;
            await pingTask;
        }
    }

    void HandleMessage(MessageType type, byte[] payload)
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
                if (CurrentRoom != null)
                    CurrentRoom.HandleMessage(this, type, payload);
                else
                    HandleLobbyMessage(type, payload);
                break;
        }
    }

    void HandleAuth(byte[] payload)
    {
        var msg = MemoryPackSerializer.Deserialize<AuthMessage>(payload);
        if (msg == null || string.IsNullOrEmpty(msg.Token))
        {
            SendMessage(MessageType.AuthResult, new AuthResultMessage { Success = false, Reason = "无效的认证消息" });
            return;
        }

        var userId = _node.ValidateToken(msg.Token);
        if (userId == null)
        {
            SendMessage(MessageType.AuthResult, new AuthResultMessage { Success = false, Reason = "Token 无效或已过期" });
            return;
        }

        IsAuthenticated = true;
        UserId = userId.Value;

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
                var room = _node.CreateRoom(create.RoomId, create.RoomName, create.MaxPlayers, create.IsPrivate, create.Password, this);
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
        _sendQueue.Writer.TryWrite(frame);
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

    public void Close()
    {
        _cts.Cancel();
        _sendQueue.Writer.TryComplete();
        try { _tcp.Close(); } catch { }
    }
}
