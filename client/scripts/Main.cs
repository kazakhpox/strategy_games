using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>Root node: owns the network client and switches between the menu and the game screen.</summary>
public partial class Main : Control
{
    public readonly NetClient Net = new();
    private readonly ConfigFile _cfg = new();
    private const string CfgPath = "user://session.cfg";
    private MenuScreen _menu = null!;
    private GameScreen? _game;
    public GameScreen? Game => _game;
    private string _tokenKey = "";

    public override void _Ready()
    {
        _cfg.Load(CfgPath);
        GetTree().Root.Theme = Ui.BuildTheme(); // one look for every control, popup and tooltip
        MouseFilter = MouseFilterEnum.Ignore; // the root must not swallow clicks meant for the map
        _menu = new MenuScreen(this);
        AddChild(_menu);

        Net.TokenIssued += token =>
        {
            if (_tokenKey.Length == 0) return;
            _cfg.SetValue("tokens", _tokenKey, token);
            _cfg.Save(CfgPath);
        };
        Net.GameStarted += world =>
        {
            _game?.QueueFree();
            _game = new GameScreen(this, world);
            AddChild(_game);
            _menu.Visible = false;
        };
        Net.StateChanged += state =>
        {
            if (state is NetState.Lobby or NetState.Room or NetState.Offline)
            {
                _game?.QueueFree();
                _game = null;
                _menu.Visible = true;
            }
            _menu.Refresh();
            _game?.OnNetState(state);
        };
        Net.LobbyChanged += () => _menu.Refresh();
        Net.ErrorReceived += text => { if (_game != null) _game.Toast(text, true); else _menu.ShowError(text); };
        Net.DeltaApplied += d => _game?.OnDelta(d);
        Net.CommandResult += (seq, ok, error, rtt) => _game?.OnCommandResult(seq, ok, error);
        Net.ChatReceived += text => _game?.Log(text, Ui.Dim);

        // optional auto-connect for scripted runs: -- --connect=host:port --name=X [--autohost]
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--name=")) _menu.SetPlayerName(arg[7..]);
            if (arg.StartsWith("--connect="))
            {
                var parts = arg[10..].Split(':');
                _menu.SetAddress(parts[0], parts.Length > 1 ? parts[1] : "27015");
                CallDeferred(nameof(AutoConnect));
            }
            if (arg == "--autohost") _menu.AutoHost = true;
            if (arg.StartsWith("--port=")) _menu.SetAddress("127.0.0.1", arg[7..]);
            if (arg == "--steam") _menu.CallDeferred(nameof(MenuScreen.UseSteam));
            if (arg.StartsWith("--maptype=") && int.TryParse(arg[10..], out int mt)) _menu.SetMapType(mt);
            if (arg == "--autojoin") _menu.AutoJoin = true;
            if (arg == "--hostsave") _menu.SelectLatestSave();
            if (arg == "--hostlocal") _menu.CallDeferred(nameof(MenuScreen.HostPressed));
            if (arg.StartsWith("--tour=")) AddChild(new DemoTour(this, arg[7..]));
        }
    }

    private void AutoConnect() => _menu.ConnectPressed();

    public string Setting(string key, string fallback) => _cfg.GetValue("ui", key, fallback).AsString();

    public void SaveSetting(string key, string value)
    {
        _cfg.SetValue("ui", key, value);
        _cfg.Save(CfgPath);
    }

    /// <param name="tokenKey">Identity of the server for seat tokens when the address itself is not stable (Steam tunnel).</param>
    public void Connect(string name, string host, int port, string? tokenKey = null)
    {
        _cfg.SetValue("ui", "name", name);
        if (tokenKey == null) { _cfg.SetValue("ui", "host", host); _cfg.SetValue("ui", "port", port.ToString()); }
        _cfg.Save(CfgPath);
        _tokenKey = tokenKey ?? $"{host}:{port}:{name}";
        Net.Name = name;
        Net.Token = _cfg.GetValue("tokens", _tokenKey, "").AsString();
        Net.Connect(host, port);
    }

    public override void _Process(double delta)
    {
        SteamNet.Poll(); // before the game client so tunnelled packets are delivered within the same frame
        Net.Poll();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest || what == NotificationPredelete) { Net.Dispose(); SteamNet.Shutdown(); LocalServer.Stop(); }
    }
}
