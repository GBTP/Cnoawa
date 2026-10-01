using System.Collections.Generic;
using System.Linq;
using Cnoawa.Host;

namespace Cnoawa;

/// <summary>
/// 社区节点线的房主侧传输。HostLogic 通过它广播消息给房间内所有成员。
/// 直接遍历 HostLogic.Players 调每个 IHostPlayer.SendRaw，等价于原 NodeRoom.Broadcast/BroadcastExcept。
///
/// 构造时不传 HostLogic（鸡生蛋：HostLogic 构造要 IHostTransport，本类又要 HostLogic 引用）。
/// 先 new 本类（host=null），再 new HostLogic(传入本类)，再设 Host = hostLogic。
/// </summary>
public class TcpHostTransport : IHostTransport
{
    HostLogic? _host;

    public HostLogic? Host
    {
        get => _host;
        set => _host = value;
    }

    public TcpHostTransport() { }

    public TcpHostTransport(HostLogic host) { _host = host; }

    public void SendToAll(byte messageType, byte[] payload)
    {
        if (_host == null) return;
        foreach (var p in _host.Players)
            p.SendRaw(messageType, payload);
    }

    public void SendToAllExcept(int excludeTransportId, byte messageType, byte[] payload)
    {
        if (_host == null) return;
        // 社区线节点模式下房主不是 _players 一员，原 NodeRoom.BroadcastExcept 就是排除指定 ConnId。
        foreach (var p in _host.Players)
            if (p.TransportId != excludeTransportId)
                p.SendRaw(messageType, payload);
    }

    public void SendTo(IHostPlayer player, byte messageType, byte[] payload)
    {
        player.SendRaw(messageType, payload);
    }

    public void KickPlayer(IHostPlayer player)
    {
        player.Kick();
    }
}

