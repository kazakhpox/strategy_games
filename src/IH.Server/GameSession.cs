using IH.Core;
using IH.Shared;

namespace IH.Server;

public sealed class Slot
{
    public string Name = "", Token = "";
    public Conn? Conn;
    public bool Ready;
    public long DisconnectedAt;
}

public sealed class SlotSave { public string Name = "", Token = ""; }

public sealed class SessionSave
{
    public GameState State = new();
    public List<SlotSave> Slots = new();
}

/// <summary>One game: a lobby before the start, then the authoritative simulation plus per-client delta trackers.</summary>
public sealed class GameSession
{
    private static readonly string[] AiNames =
        { "Аврора", "Борей", "Гиперион", "Дедал", "Зефир", "Икар", "Кассиопея", "Лира", "Меркурий", "Орион", "Персей" };

    public readonly int Id;
    public readonly GameSettings Settings;
    public readonly List<Slot> Slots = new();
    public Game? Game { get; private set; }
    private readonly GameServer _server;
    private readonly List<DeltaTracker> _trackers = new();
    private int _host;
    private int _timerKey = -1, _savedTurn = -1;
    private long _deadline;

    public GameSession(GameServer server, int id, GameSettings settings)
    {
        _server = server; Id = id; Settings = settings;
    }

    public GameSession(GameServer server, int id, SessionSave save)
    {
        _server = server; Id = id; Settings = save.State.Settings;
        Game = new Game(save.State);
        long now = server.Now;
        foreach (var s in save.Slots)
        {
            Slots.Add(new Slot { Name = s.Name, Token = s.Token, DisconnectedAt = now });
            _trackers.Add(new DeltaTracker());
        }
        for (int i = 0; i < Slots.Count; i++) Game.P(i).Connected = false;
        _savedTurn = Game.S.Turn;
    }

    public bool Started => Game != null;

    public GameInfo Info() => new()
    {
        Id = Id, Name = Settings.Name, Players = Slots.Count, MaxPlayers = Settings.MaxPlayers,
        Started = Started, Turn = Game?.S.Turn ?? 0,
    };

    public int FindSlotByToken(string token) => Slots.FindIndex(s => s.Token == token);

    // -------------------------------------------------------------- lobby
    public string? Join(Conn c)
    {
        if (Started)
        {
            // a seat can be reclaimed by name when its owner is offline (lost token, server restarted from a save)
            int seat = Slots.FindIndex(s => s.Conn == null && string.Equals(s.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            if (seat < 0) return "Игра уже идёт. Войти можно только на своё место (под тем же именем)";
            Slots[seat].Token = c.Token;
            Attach(c, seat);
            return null;
        }
        if (Slots.Count >= Settings.MaxPlayers) return "В игре нет свободных мест";
        string name = c.Name;
        for (int n = 2; Slots.Any(s => s.Name == name); n++) name = $"{c.Name} ({n})";
        Slots.Add(new Slot { Name = name, Token = c.Token, Conn = c });
        c.Session = this; c.Slot = Slots.Count - 1;
        BroadcastLobby();
        return null;
    }

    public void Attach(Conn c, int slot)
    {
        var s = Slots[slot];
        if (s.Conn != null && s.Conn != c) _server.Kick(s.Conn);
        s.Conn = c; c.Session = this; c.Slot = slot;
        if (!Started) { BroadcastLobby(); return; }
        Game!.P(slot).Connected = true;
        _trackers[slot].Reset();
        _server.Send(c, MsgType.GameStart, StartMsg(slot));
        Flush();
    }

    /// <summary>Connection lost or the player left on purpose.</summary>
    public void Detach(Conn c, bool voluntary)
    {
        int slot = c.Slot;
        c.Session = null; c.Slot = -1;
        if (slot < 0 || slot >= Slots.Count || Slots[slot].Conn != c) return;
        if (!Started)
        {
            Slots.RemoveAt(slot);
            for (int i = 0; i < Slots.Count; i++) if (Slots[i].Conn != null) Slots[i].Conn!.Slot = i;
            if (Slots.Count == 0) { _server.RemoveSession(this); return; }
            if (slot == _host) _host = 0; else if (slot < _host) _host--;
            BroadcastLobby();
            _server.BroadcastLobbyList();
            return;
        }
        Slots[slot].Conn = null;
        Slots[slot].DisconnectedAt = _server.Now;
        Game!.P(slot).Connected = false;
        Flush();
    }

    public void SetReady(Conn c, bool ready)
    {
        if (Started || c.Slot < 0) return;
        Slots[c.Slot].Ready = ready;
        BroadcastLobby();
    }

    private void BroadcastLobby()
    {
        var slots = Slots.Select(s => new SlotDto { Name = s.Name, Ready = s.Ready, Connected = s.Conn != null }).ToList();
        for (int i = 0; i < Settings.AiCount; i++)
            slots.Add(new SlotDto { Name = "ИИ " + AiNames[i % AiNames.Length], Ready = true, Connected = true, AI = true });
        for (int i = 0; i < Slots.Count; i++)
        {
            var c = Slots[i].Conn;
            if (c == null) continue;
            _server.Send(c, MsgType.LobbyState, new LobbyStateMsg
            {
                Info = Info(), Settings = Settings, Slots = slots.ToArray(), YourSlot = i, HostSlot = _host,
            });
        }
    }

    public string? Start(Conn c)
    {
        if (Started) return "Игра уже идёт";
        if (c.Slot != _host) return "Начать игру может только хост";
        var players = Slots.Select(s => (s.Name, false)).ToList();
        for (int i = 0; i < Settings.AiCount && players.Count < 12; i++) players.Add(("ИИ " + AiNames[i % AiNames.Length], true));
        if (players.Count < 2) return "Нужно минимум два участника (добавьте ИИ или дождитесь игроков)";
        try { Game = Game.Create(Settings, players, _server.Options.Balance); }
        catch (Exception ex) { return ex.Message; }
        _server.Log($"* Игра #{Id} «{Settings.Name}» началась: {Slots.Count} игрок(ов), {Settings.AiCount} ИИ, карта {Settings.MapW}x{Settings.MapH}, seed {Settings.Seed}");
        for (int i = 0; i < Slots.Count; i++)
        {
            _trackers.Add(new DeltaTracker());
            if (Slots[i].Conn != null) _server.Send(Slots[i].Conn!, MsgType.GameStart, StartMsg(i));
        }
        Flush();
        return null;
    }

    private GameStartMsg StartMsg(int slot) => new()
    {
        GameId = Id, W = Game!.S.W, H = Game.S.H, YourId = slot, Settings = Settings,
    };

    // --------------------------------------------------------------- game
    public void HandleCommand(Conn c, CommandMsg m)
    {
        string? err;
        if (Game == null) err = "Игра ещё не началась";
        else
        {
            try { err = Game.Execute(c.Slot, m.Cmd); }
            catch (Exception ex)
            {
                _server.Log($"! Ошибка при выполнении команды {m.Cmd.Kind}: {ex}");
                err = "Внутренняя ошибка сервера";
            }
        }
        _server.Send(c, MsgType.CmdResult, new CmdResultMsg { Seq = m.Seq, Ok = err == null, Error = err ?? "" });
        if (Game != null) Flush();
    }

    public void Chat(Conn c, string text)
    {
        if (text.Length == 0) return;
        var msg = new TextMsg { From = c.Slot, Text = $"{Slots[c.Slot].Name}: {text}" };
        foreach (var s in Slots) if (s.Conn != null) _server.Send(s.Conn, MsgType.ChatFrom, msg);
    }

    private int TimerLeft() => _deadline <= 0 ? -1 : (int)Math.Max(0, (_deadline - _server.Now + 999) / 1000);

    /// <summary>Sends every connected player the changes they are allowed to see.</summary>
    public void Flush()
    {
        var g = Game!;
        UpdateTimer();
        var events = g.Events.ToList();
        g.Events.Clear();
        for (int i = 0; i < Slots.Count; i++)
        {
            var conn = Slots[i].Conn;
            if (conn == null) { g.UpdateVisibility(g.P(i)); continue; }
            var view = g.BuildView(i);
            var mine = new List<EventDto>();
            foreach (var e in events)
            {
                if (e.OnlyPlayer >= 0) { if (e.OnlyPlayer == i) mine.Add(e.Dto); continue; }
                if (e.Tiles == null || e.Tiles.Any(t => view.Visible[t])) mine.Add(e.Dto);
            }
            var delta = _trackers[i].Build(g, i, view, mine, TimerLeft());
            if (delta != null) _server.Send(conn, MsgType.Delta, delta);
        }
        if (_server.Options.AutosaveEvery > 0 && g.S.Turn != _savedTurn && g.S.Turn % _server.Options.AutosaveEvery == 0) Save();
    }

    private void UpdateTimer()
    {
        var g = Game!;
        int key = g.S.Turn * 64 + (Settings.Simultaneous ? 0 : g.S.CurrentPlayer + 1);
        if (key == _timerKey) return;
        _timerKey = key;
        _deadline = Settings.TurnTimerSec > 0 ? _server.Now + Settings.TurnTimerSec * 1000L : 0;
    }

    private bool WaitingOn(int pid)
    {
        var p = Game!.P(pid);
        if (!p.Alive) return false;
        return Settings.Simultaneous ? !p.EndedTurn : Game.S.CurrentPlayer == pid;
    }

    public void Tick(long now)
    {
        if (Game == null) return;
        if (!Slots.Any(s => s.Conn != null)) { _timerKey = -1; return; } // nobody online: the game is paused
        bool changed = false;
        if (_deadline > 0 && now >= _deadline)
        {
            for (int i = 0; i < Slots.Count; i++) if (WaitingOn(i)) { Game.ForceEndTurn(i); changed = true; }
        }
        long afk = _server.Options.AfkSeconds * 1000L;
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Conn == null && now - Slots[i].DisconnectedAt > afk && WaitingOn(i)) { Game.ForceEndTurn(i); changed = true; }
        if (changed) Flush();
    }

    public void Save()
    {
        if (Game == null) return;
        try
        {
            Directory.CreateDirectory(_server.Options.SaveDir);
            string path = Path.Combine(_server.Options.SaveDir, $"game{Id}.json");
            var save = new SessionSave { State = Game.S, Slots = Slots.Select(s => new SlotSave { Name = s.Name, Token = s.Token }).ToList() };
            File.WriteAllText(path + ".tmp", SaveGame.ToJson(save));
            File.Move(path + ".tmp", path, true);
            _savedTurn = Game.S.Turn;
        }
        catch (Exception ex) { _server.Log($"! Не удалось сохранить игру #{Id}: {ex.Message}"); }
    }
}
