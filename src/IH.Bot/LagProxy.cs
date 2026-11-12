using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace IH.Bot;

/// <summary>
/// UDP proxy that degrades the link between clients and the server: added delay, jitter and packet loss
/// in both directions. Emulates a poor VPN tunnel (e.g. Radmin VPN relayed through the internet).
/// </summary>
public sealed class LagProxy : IDisposable
{
    private readonly UdpClient _front;
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<IPEndPoint, UdpClient> _upstreams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _delayMs, _jitterMs, _lossPct;
    public long Forwarded, Dropped;

    public int Port => ((IPEndPoint)_front.Client.LocalEndPoint!).Port;

    public LagProxy(int listenPort, IPEndPoint target, int delayMs, int jitterMs, int lossPct)
    {
        _target = target; _delayMs = delayMs; _jitterMs = jitterMs; _lossPct = lossPct;
        _front = new UdpClient(new IPEndPoint(IPAddress.Any, listenPort));
        _ = Task.Run(FrontLoop);
    }

    private async Task FrontLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _front.ReceiveAsync(_cts.Token);
                var up = _upstreams.GetOrAdd(r.RemoteEndPoint, ep =>
                {
                    var u = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    _ = Task.Run(() => BackLoop(u, ep));
                    return u;
                });
                Schedule(r.Buffer, b => up.Send(b, b.Length, _target));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task BackLoop(UdpClient up, IPEndPoint client)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await up.ReceiveAsync(_cts.Token);
                Schedule(r.Buffer, b => _front.Send(b, b.Length, client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private void Schedule(byte[] data, Action<byte[]> send)
    {
        if (_lossPct > 0 && Random.Shared.Next(100) < _lossPct) { Interlocked.Increment(ref Dropped); return; }
        int delay = _delayMs + (_jitterMs > 0 ? Random.Shared.Next(_jitterMs + 1) : 0);
        Interlocked.Increment(ref Forwarded);
        if (delay <= 0) { TrySend(data, send); return; }
        _ = Task.Delay(delay).ContinueWith(_ => TrySend(data, send));
    }

    private void TrySend(byte[] data, Action<byte[]> send)
    {
        if (_cts.IsCancellationRequested) return;
        try { send(data); } catch (ObjectDisposedException) { } catch (SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _front.Dispose();
        foreach (var u in _upstreams.Values) u.Dispose();
    }
}
