using System.Diagnostics;
using LiteNetLib;

namespace IH.Shared;

public enum NetState { Offline, Connecting, Lobby, Room, InGame, Reconnecting }

/// <summary>
/// Client side of the protocol, shared by the Godot client and the headless bot.
/// Call <see cref="Poll"/> regularly from a single thread; all callbacks fire from inside Poll.
/// Handles automatic reconnection with session restore via the seat token.
/// </summary>
public sealed class NetClient
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, long> _sentAt = new();
    private NetPeer? _peer;
    private string _host = "";
    private int _port, _seq;
    private long _nextRetry, _nextPing;
    private bool _wantConnection;

    public string Name = "Игрок", Token = "";
    public NetState State { get; private set; } = NetState.Offline;
    public bool AutoReconnect = true;
    public int PingMs { get; private set; } = -1;
    public long BytesReceived { get; private set; }
    public int DeltasReceived { get; private set; }
    public int PendingCommands => _sentAt.Count;

    public LobbyListMsg Lobby = new();
    public LobbyStateMsg? Room;
    public ClientWorld? World;

    public event Action<NetState>? StateChanged;
    public event Action? LobbyChanged;
    public event Action<ClientWorld>? GameStarted;
    public event Action<DeltaMsg>? DeltaApplied;
    /// <summary>seq, ok, error text, round-trip time in ms.</summary>
    public event Action<int, bool, string, int>? CommandResult;
    public event Action<string>? ChatReceived;
    public event Action<string>? ErrorReceived;
    public event Action<string>? TokenIssued;

    public NetClient()
    {
        // 20 attempts x 500 ms: a relayed route (VPN, Steam) may need several seconds to come up
        _net = new NetManager(_listener) { DisconnectTimeout = 15000, UpdateTime = 10, IPv6Enabled = false, MaxConnectAttempts = 20 };
        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            Send(MsgType.Hello, new HelloMsg { Name = Name, Token = Token });
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (_peer != null && peer != _peer) return;
            _peer = null;
            _sentAt.Clear();
            PingMs = -1;
            if (_wantConnection && AutoReconnect && info.Reason != DisconnectReason.ConnectionRejected)
            {
                _nextRetry = _clock.ElapsedMilliseconds + 1500;
                SetState(NetState.Reconnecting);
            }
            else
            {
                _wantConnection = false;
                SetState(NetState.Offline);
                if (info.Reason == DisconnectReason.ConnectionFailed) ErrorReceived?.Invoke("Сервер не отвечает");
                else if (info.Reason == DisconnectReason.ConnectionRejected) ErrorReceived?.Invoke("Сервер отклонил подключение");
            }
        };
        _listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            byte[] data = reader.GetRemainingBytes();
            reader.Recycle();
            BytesReceived += data.Length;
            if (data.Length >= 2) Handle(data);
        };
        _net.Start();
    }

    private void SetState(NetState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    public void Connect(string host, int port)
    {
        _host = host; _port = port; _wantConnection = true;
        SetState(NetState.Connecting);
        try { _net.Connect(host, port, Codec.ConnectionKey); }
        catch (Exception ex)
        {
            _wantConnection = false;
            SetState(NetState.Offline);
            ErrorReceived?.Invoke("Неверный адрес сервера: " + ex.Message);
        }
    }

    public void Disconnect()
    {
        _wantConnection = false;
        _peer?.Disconnect();
        _net.DisconnectAll();
        _peer = null;
        World = null; Room = null;
        SetState(NetState.Offline);
    }

    public void Dispose() => _net.Stop(false);

    public void Poll()
    {
        _net.PollEvents();
        long now = _clock.ElapsedMilliseconds;
        if (State == NetState.Reconnecting && _peer == null && now >= _nextRetry)
        {
            _nextRetry = now + 3000;
            try { _net.Connect(_host, _port, Codec.ConnectionKey); } catch { /* retried later */ }
        }
        if (_peer != null && now >= _nextPing)
        {
            _nextPing = now + 2000;
            Send(MsgType.Ping, new PingMsg { T = now });
        }
    }

    public void Send(MsgType type, IWire? body = null) => _peer?.Send(Codec.Encode(type, body), DeliveryMethod.ReliableOrdered);

    /// <summary>Sends a game command; the result arrives through <see cref="CommandResult"/> with the same sequence number.</summary>
    public int SendCommand(GameCommand cmd)
    {
        int seq = ++_seq;
        if (_peer == null) return seq;
        _sentAt[seq] = _clock.ElapsedMilliseconds;
        Send(MsgType.Command, new CommandMsg { Seq = seq, Cmd = cmd });
        return seq;
    }

    public int SendCommand(CmdType t, int a = 0, int b = 0, int c = 0, string s = "") => SendCommand(new GameCommand(t, a, b, c, s));

    private void Handle(byte[] d)
    {
        switch (Codec.PeekType(d, 0))
        {
            case MsgType.Welcome:
            {
                var m = Codec.Decode<WelcomeMsg>(d, 0, d.Length);
                Token = m.Token;
                TokenIssued?.Invoke(Token);
                if (!m.Resumed) { World = null; Room = null; SetState(NetState.Lobby); }
                break;
            }
            case MsgType.LobbyList:
                Lobby = Codec.Decode<LobbyListMsg>(d, 0, d.Length);
                if (State != NetState.Lobby) { World = null; Room = null; SetState(NetState.Lobby); }
                LobbyChanged?.Invoke();
                break;
            case MsgType.LobbyState:
                Room = Codec.Decode<LobbyStateMsg>(d, 0, d.Length);
                SetState(NetState.Room);
                LobbyChanged?.Invoke();
                break;
            case MsgType.GameStart:
                World = new ClientWorld(Codec.Decode<GameStartMsg>(d, 0, d.Length));
                SetState(NetState.InGame);
                GameStarted?.Invoke(World);
                break;
            case MsgType.Delta:
            {
                if (World == null) break;
                var m = Codec.Decode<DeltaMsg>(d, 0, d.Length);
                World.Apply(m);
                DeltasReceived++;
                DeltaApplied?.Invoke(m);
                break;
            }
            case MsgType.CmdResult:
            {
                var m = Codec.Decode<CmdResultMsg>(d, 0, d.Length);
                int rtt = _sentAt.Remove(m.Seq, out long t) ? (int)(_clock.ElapsedMilliseconds - t) : -1;
                CommandResult?.Invoke(m.Seq, m.Ok, m.Error, rtt);
                break;
            }
            case MsgType.ChatFrom:
                ChatReceived?.Invoke(Codec.Decode<TextMsg>(d, 0, d.Length).Text);
                break;
            case MsgType.Pong:
                PingMs = (int)(_clock.ElapsedMilliseconds - Codec.Decode<PingMsg>(d, 0, d.Length).T);
                break;
            case MsgType.Error:
                ErrorReceived?.Invoke(Codec.Decode<TextMsg>(d, 0, d.Length).Text);
                break;
        }
    }
}
