using System.Net;
using System.Net.Sockets;

namespace IH.Shared;

/// <summary>An unreliable datagram channel to peers identified by a 64-bit id (e.g. Steam P2P messages).</summary>
public interface IPeerLink
{
    void Send(ulong peer, byte[] data, int length);
}

/// <summary>
/// The game protocol always runs over UDP. These two classes carry that UDP traffic through another
/// transport so the server and the network code stay unchanged:
///
///   client game --UDP--> TunnelClient ==link==> TunnelHost --UDP--> game server (on the host's machine)
///
/// Both are driven from one thread: feed incoming link data to OnPeerData and call Poll every frame.
/// </summary>
public sealed class TunnelHost : IDisposable
{
    private sealed class Peer { public Socket Sock = null!; public long LastSeen; }

    private readonly IPeerLink _link;
    private readonly IPEndPoint _server;
    private readonly Dictionary<ulong, Peer> _peers = new();
    private readonly byte[] _buf = new byte[65536];
    private const int MaxPeers = 32;
    private const long IdleMs = 120_000;

    public int PeerCount => _peers.Count;

    public TunnelHost(IPeerLink link, int serverPort)
    {
        _link = link;
        _server = new IPEndPoint(IPAddress.Loopback, serverPort);
    }

    public void OnPeerData(ulong peer, byte[] data, int length)
    {
        if (!_peers.TryGetValue(peer, out var p))
        {
            if (_peers.Count >= MaxPeers) return;
            p = new Peer { Sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) };
            p.Sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            p.Sock.Connect(_server); // each remote player looks like a separate local client to the server
            _peers[peer] = p;
        }
        p.LastSeen = Environment.TickCount64;
        try { p.Sock.Send(data, 0, length, SocketFlags.None); } catch (SocketException) { }
    }

    public void Poll()
    {
        long now = Environment.TickCount64;
        List<ulong>? dead = null;
        foreach (var (id, p) in _peers)
        {
            try
            {
                while (p.Sock.Available > 0)
                {
                    int n = p.Sock.Receive(_buf);
                    if (n > 0) _link.Send(id, _buf, n);
                }
            }
            catch (SocketException) { /* server not up yet or restarting */ }
            if (now - p.LastSeen > IdleMs) (dead ??= new List<ulong>()).Add(id);
        }
        if (dead == null) return;
        foreach (ulong id in dead) { _peers[id].Sock.Dispose(); _peers.Remove(id); }
    }

    public void Dispose()
    {
        foreach (var p in _peers.Values) p.Sock.Dispose();
        _peers.Clear();
    }
}

public sealed class TunnelClient : IDisposable
{
    private readonly IPeerLink _link;
    private readonly Socket _sock;
    private readonly byte[] _buf = new byte[65536];
    private EndPoint _game = new IPEndPoint(IPAddress.Loopback, 0);
    private bool _hasGame;

    public readonly ulong HostPeer;
    /// <summary>The game connects to 127.0.0.1 on this port instead of the real server address.</summary>
    public int LocalPort => ((IPEndPoint)_sock.LocalEndPoint!).Port;

    public TunnelClient(IPeerLink link, ulong hostPeer)
    {
        _link = link; HostPeer = hostPeer;
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void OnPeerData(ulong peer, byte[] data, int length)
    {
        if (peer != HostPeer || !_hasGame) return;
        try { _sock.SendTo(data, 0, length, SocketFlags.None, _game); } catch (SocketException) { }
    }

    public void Poll()
    {
        try
        {
            while (_sock.Available > 0)
            {
                EndPoint from = new IPEndPoint(IPAddress.Loopback, 0);
                int n = _sock.ReceiveFrom(_buf, ref from);
                _game = from; _hasGame = true;
                if (n > 0) _link.Send(HostPeer, _buf, n);
            }
        }
        catch (SocketException) { /* the game closed its socket; it will reconnect */ }
    }

    public void Dispose() => _sock.Dispose();
}
