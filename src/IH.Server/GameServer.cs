using System.Diagnostics;
using IH.Core;
using IH.Shared;
using LiteNetLib;

namespace IH.Server;

public sealed class ServerOptions
{
    public int Port = 27015;
    public string SaveDir = "saves";
    public int AutosaveEvery = 10;
    /// <summary>Seconds a disconnected player may hold up the turn before it is ended for them.</summary>
    public int AfkSeconds = 45;
    public BalanceConfig? Balance;
    public bool Quiet;
}

public sealed class Conn
{
    public NetPeer Peer = null!;
    public string Name = "", Token = "";
    public bool Greeted;
    public GameSession? Session;
    public int Slot = -1;
}

/// <summary>
/// Headless authoritative server: owns the network socket, the lobby and all running game sessions.
/// Everything runs on the thread that calls <see cref="Tick"/>.
/// </summary>
public sealed class GameServer
{
    public readonly ServerOptions Options;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Dictionary<int, Conn> _conns = new();
    private readonly Dictionary<int, GameSession> _sessions = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _nextSessionId = 1;

    public int Port => _net.LocalPort;
    public IReadOnlyDictionary<int, GameSession> Sessions => _sessions;
    public long Now => _clock.ElapsedMilliseconds;

    public GameServer(ServerOptions options)
    {
        Options = options;
        _net = new NetManager(_listener)
        {
            DisconnectTimeout = 15000, // tolerant of VPN hiccups
            UpdateTime = 10,
            UnconnectedMessagesEnabled = false,
            IPv6Enabled = false,
        };
        _listener.ConnectionRequestEvent += req =>
        {
            if (_conns.Count >= 64) req.Reject(); else req.AcceptIfKey(Codec.ConnectionKey);
        };
        _listener.PeerConnectedEvent += peer => _conns[peer.Id] = new Conn { Peer = peer };
        _listener.PeerDisconnectedEvent += (peer, _) =>
        {
            if (!_conns.Remove(peer.Id, out var c)) return;
            Log($"- {(c.Name.Length > 0 ? c.Name : "клиент")} отключился");
            c.Session?.Detach(c, false);
        };
        _listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            byte[] data = reader.GetRemainingBytes();
            reader.Recycle();
            if (!_conns.TryGetValue(peer.Id, out var c) || data.Length < 2) return;
            try { Handle(c, data); }
            catch (Exception ex)
            {
                Log($"! Некорректный пакет от {c.Name}: {ex.Message}");
                Send(c, MsgType.Error, new TextMsg { Text = "Некорректный пакет" });
            }
        };
    }

    public void Start()
    {
        if (!_net.Start(Options.Port)) throw new InvalidOperationException($"Не удалось открыть UDP-порт {Options.Port}");
        Log($"Сервер Infinite Horizons запущен, UDP-порт {Port}");
    }

    public void Stop()
    {
        foreach (var s in _sessions.Values) s.Save();
        _net.Stop(true);
    }

    public void Tick()
    {
        _net.PollEvents();
        long now = Now;
        foreach (var s in _sessions.Values.ToList()) s.Tick(now);
    }

    public void Log(string text)
    {
        if (!Options.Quiet) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
    }

    public void Send(Conn c, MsgType type, IWire? body) => c.Peer.Send(Codec.Encode(type, body), DeliveryMethod.ReliableOrdered);

    public GameSession AddSession(GameSettings settings)
    {
        var s = new GameSession(this, _nextSessionId++, settings);
        _sessions[s.Id] = s;
        return s;
    }

    /// <summary>Restores a session from an autosave; players rejoin by token or by name.</summary>
    public GameSession LoadSession(string path)
    {
        var save = SaveGame.FromJson<SessionSave>(File.ReadAllText(path));
        var s = new GameSession(this, _nextSessionId++, save);
        _sessions[s.Id] = s;
        Log($"Загружена игра «{s.Settings.Name}» (ход {save.State.Turn}) из {path}");
        return s;
    }

    public void RemoveSession(GameSession s)
    {
        _sessions.Remove(s.Id);
        BroadcastLobbyList();
    }

    private LobbyListMsg LobbyList() => new() { Games = _sessions.Values.Select(s => s.Info()).ToArray() };

    public void BroadcastLobbyList()
    {
        var msg = LobbyList();
        foreach (var c in _conns.Values) if (c.Greeted && c.Session == null) Send(c, MsgType.LobbyList, msg);
    }

    private void Handle(Conn c, byte[] data)
    {
        var type = Codec.PeekType(data, 0);
        if (type == MsgType.Ping) { Send(c, MsgType.Pong, Codec.Decode<PingMsg>(data, 0, data.Length)); return; }
        if (type == MsgType.Hello) { OnHello(c, Codec.Decode<HelloMsg>(data, 0, data.Length)); return; }
        if (!c.Greeted) { Send(c, MsgType.Error, new TextMsg { Text = "Сначала представьтесь" }); return; }

        switch (type)
        {
            case MsgType.ListGames:
                Send(c, MsgType.LobbyList, LobbyList());
                break;
            case MsgType.CreateGame:
            {
                if (c.Session != null) { Error(c, "Вы уже в игре"); break; }
                if (_sessions.Count >= 16) { Error(c, "На сервере слишком много игр"); break; }
                var st = Codec.Decode<GameSettings>(data, 0, data.Length);
                st.Name = Clean(st.Name, 32, "Новая игра");
                st.MapW = Math.Clamp(st.MapW, 24, 160); st.MapH = Math.Clamp(st.MapH, 16, 120);
                st.MaxPlayers = Math.Clamp(st.MaxPlayers, 1, 8); st.AiCount = Math.Clamp(st.AiCount, 0, 11);
                st.TurnTimerSec = Math.Clamp(st.TurnTimerSec, 0, 3600);
                st.MapType = Math.Clamp(st.MapType, 0, 2);
                var s = AddSession(st);
                s.Join(c);
                Log($"+ {c.Name} создал игру «{st.Name}» (#{s.Id})");
                BroadcastLobbyList();
                break;
            }
            case MsgType.JoinGame:
            {
                if (c.Session != null) { Error(c, "Вы уже в игре"); break; }
                int id = Codec.Decode<IntMsg>(data, 0, data.Length).V;
                if (!_sessions.TryGetValue(id, out var s)) { Error(c, "Игра не найдена"); break; }
                string? err = s.Join(c);
                if (err != null) Error(c, err); else BroadcastLobbyList();
                break;
            }
            case MsgType.LeaveGame:
                if (c.Session != null)
                {
                    c.Session.Detach(c, true);
                    Send(c, MsgType.LobbyList, LobbyList());
                    BroadcastLobbyList();
                }
                break;
            case MsgType.Ready:
                c.Session?.SetReady(c, Codec.Decode<BoolMsg>(data, 0, data.Length).V);
                break;
            case MsgType.StartGame:
            {
                string? err = c.Session == null ? "Вы не в игре" : c.Session.Start(c);
                if (err != null) Error(c, err); else BroadcastLobbyList();
                break;
            }
            case MsgType.Command:
            {
                var m = Codec.Decode<CommandMsg>(data, 0, data.Length);
                if (c.Session == null) Send(c, MsgType.CmdResult, new CmdResultMsg { Seq = m.Seq, Error = "Вы не в игре" });
                else c.Session.HandleCommand(c, m);
                break;
            }
            case MsgType.Chat:
            {
                var m = Codec.Decode<TextMsg>(data, 0, data.Length);
                c.Session?.Chat(c, Clean(m.Text, 200, ""));
                break;
            }
        }
    }

    private void Error(Conn c, string text) => Send(c, MsgType.Error, new TextMsg { Text = text });

    private static string Clean(string s, int max, string fallback)
    {
        s = new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (s.Length > max) s = s[..max];
        return s.Length == 0 ? fallback : s;
    }

    private void OnHello(Conn c, HelloMsg h)
    {
        if (h.Proto != Codec.ProtocolVersion)
        {
            Error(c, $"Несовместимая версия клиента (сервер: {Codec.ProtocolVersion}, клиент: {h.Proto})");
            return;
        }
        if (c.Greeted) return;
        c.Name = Clean(h.Name, 20, "Игрок");
        c.Greeted = true;

        // reconnection: the token identifies a seat in an existing session
        if (h.Token.Length > 0)
            foreach (var s in _sessions.Values)
            {
                int slot = s.FindSlotByToken(h.Token);
                if (slot < 0) continue;
                c.Token = h.Token;
                Send(c, MsgType.Welcome, new WelcomeMsg { Token = c.Token, Resumed = true });
                s.Attach(c, slot);
                Log($"+ {c.Name} переподключился к игре #{s.Id}");
                return;
            }

        c.Token = Guid.NewGuid().ToString("N");
        Send(c, MsgType.Welcome, new WelcomeMsg { Token = c.Token });
        Send(c, MsgType.LobbyList, LobbyList());
        Log($"+ {c.Name} подключился");
    }

    /// <summary>Drops a stale connection that still occupies a seat being reclaimed.</summary>
    public void Kick(Conn c)
    {
        _conns.Remove(c.Peer.Id);
        c.Session = null;
        c.Peer.Disconnect();
    }
}
