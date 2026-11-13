using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>Connection form, list of games on the server, and the pre-game room.</summary>
public partial class MenuScreen : Control
{
    private readonly Main _main;
    private readonly LineEdit _name = new(), _host = new(), _port = new(), _gameName = new() { Text = "Новая игра" };
    private readonly Label _status = Ui.Lbl("", 14, Ui.Bad);
    private readonly VBoxContainer _connectBox, _lobbyBox, _roomBox, _games = Ui.V(4), _slots = Ui.V(4);
    private readonly OptionButton _mapSize = new(), _mode = new();
    private readonly SpinBox _ai = new() { MinValue = 0, MaxValue = 8, Value = 2 }, _humans = new() { MinValue = 1, MaxValue = 8, Value = 4 },
        _timer = new() { MinValue = 0, MaxValue = 600, Step = 15, Value = 0, Suffix = " с" };
    private readonly Label _roomTitle = Ui.Lbl("", 18, Ui.Gold), _roomInfo = Ui.Lbl("", 13, Ui.Dim);
    private Button _startBtn = null!, _readyBtn = null!;
    private readonly OptionButton _hostSave = new();
    private readonly Label _hostInfo = Ui.Lbl("Друзья подключаются к вашему IP из Radmin VPN. Откройте входящий UDP-порт в брандмауэре.", 12, Ui.Dim);
    private readonly System.Collections.Generic.List<string> _saves = LocalServer.Saves();
    private double _connectAt = -1;
    public bool AutoHost, AutoJoin;
    public void SelectLatestSave() { if (_saves.Count > 0) _hostSave.Selected = 1; }

    private static readonly (string label, int w, int h)[] Sizes =
    {
        ("Маленькая 40x26", 40, 26), ("Средняя 56x36", 56, 36), ("Большая 80x52", 80, 52), ("Огромная 112x72", 112, 72),
    };

    public MenuScreen(Main main)
    {
        _main = main;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = Ui.Panel(20);
        panel.CustomMinimumSize = new Vector2(560, 0);
        center.AddChild(panel);
        var root = Ui.V(10);
        panel.AddChild(root);

        root.AddChild(Ui.Lbl("INFINITE HORIZONS", 30, Ui.Gold));
        root.AddChild(Ui.Lbl("4X-стратегия с бесконечным развитием и системой «Анти-Сноуболл»", 13, Ui.Dim));
        root.AddChild(new HSeparator());

        _name.Text = main.Setting("name", "Игрок"); _name.MaxLength = 20;
        _host.Text = main.Setting("host", "127.0.0.1"); _host.PlaceholderText = "IP хоста в Radmin VPN, напр. 26.123.45.67";
        _port.Text = main.Setting("port", "27015");
        foreach (var e in new[] { _name, _host, _port, _gameName }) e.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        _connectBox = Ui.V(8,
            Row("Ваше имя", _name), Row("Адрес сервера", _host), Row("Порт (UDP)", _port),
            Ui.Btn("Подключиться", ConnectPressed, size: 16),
            new HSeparator(),
            Ui.Lbl("Или станьте хостом — сервер запустится на этом компьютере:", 13, Ui.Dim),
            Row("Партия", _hostSave),
            Ui.Btn("Создать сервер и подключиться", HostPressed, size: 16),
            _hostInfo);
        root.AddChild(_connectBox);
        _hostInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hostInfo.CustomMinimumSize = new Vector2(500, 0);
        _hostSave.AddItem("Новая партия");
        foreach (string save in _saves) _hostSave.AddItem("Продолжить: " + save.GetFile() + "  (" + Time.GetDatetimeStringFromUnixTime((long)FileAccess.GetModifiedTime(save), true) + ")");

        for (int i = 0; i < Sizes.Length; i++) _mapSize.AddItem(Sizes[i].label, i);
        _mapSize.Selected = 1;
        _mode.AddItem("Одновременные ходы", 0); _mode.AddItem("Ходы по очереди", 1);
        var gamesScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 130) };
        gamesScroll.AddChild(_games);
        _games.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _lobbyBox = Ui.V(8,
            Ui.Lbl("Игры на сервере", 16, Ui.Gold), gamesScroll, new HSeparator(),
            Ui.Lbl("Создать игру", 16, Ui.Gold),
            Row("Название", _gameName), Row("Карта", _mapSize), Row("Режим ходов", _mode),
            Row("Мест для людей", _humans), Row("Противников-ИИ", _ai), Row("Таймер хода (0 — нет)", _timer),
            Ui.H(8, Ui.Btn("Создать", CreatePressed, size: 16), Ui.Btn("Отключиться", () => _main.Net.Disconnect())));
        root.AddChild(_lobbyBox);

        _startBtn = Ui.Btn("Начать игру", () => _main.Net.Send(MsgType.StartGame), size: 16);
        _readyBtn = Ui.Btn("Готов", () =>
        {
            var room = _main.Net.Room;
            if (room != null) _main.Net.Send(MsgType.Ready, new BoolMsg { V = !room.Slots[room.YourSlot].Ready });
        });
        _roomBox = Ui.V(8, _roomTitle, _roomInfo, _slots,
            Ui.H(8, _startBtn, _readyBtn, Ui.Btn("Выйти", () => _main.Net.Send(MsgType.LeaveGame))));
        root.AddChild(_roomBox);
        root.AddChild(_status);
        Refresh();
    }

    private static Control Row(string label, Control field)
    {
        var l = Ui.Lbl(label, 14);
        l.CustomMinimumSize = new Vector2(200, 0);
        field.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return Ui.H(8, l, field);
    }

    public void SetPlayerName(string name) => _name.Text = name;
    public void SetAddress(string host, string port) { _host.Text = host; _port.Text = port; }

    public void ShowError(string text)
    {
        _status.AddThemeColorOverride("font_color", Ui.Bad);
        _status.Text = text;
    }

    public void HostPressed()
    {
        if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) { ShowError("Порт — число от 1 до 65535"); return; }
        if (_name.Text.Trim().Length == 0) { ShowError("Введите имя"); return; }
        int pick = _hostSave.Selected - 1;
        string? error = LocalServer.Start(port, pick >= 0 && pick < _saves.Count ? _saves[pick] : null);
        if (error != null) { ShowError(error); return; }
        _host.Text = "127.0.0.1";
        _hostInfo.Text = LocalServer.AddressHint() + $", порт {port} (UDP). Сохранения: {LocalServer.SaveDir}";
        _hostInfo.AddThemeColorOverride("font_color", Ui.Gold);
        _status.AddThemeColorOverride("font_color", Ui.Dim);
        _status.Text = "Запуск сервера…";
        _connectAt = Time.GetTicksMsec() / 1000.0 + 1.2; // give the process a moment to open its socket
    }

    public override void _Process(double delta)
    {
        if (_connectAt < 0 || Time.GetTicksMsec() / 1000.0 < _connectAt) return;
        _connectAt = -1;
        if (!LocalServer.Running) { ShowError("Сервер не запустился (возможно, порт занят)"); return; }
        ConnectPressed();
    }

    public void ConnectPressed()
    {
        string name = _name.Text.Trim(), host = _host.Text.Trim();
        if (name.Length == 0) { ShowError("Введите имя"); return; }
        if (host.Length == 0) { ShowError("Введите адрес сервера"); return; }
        if (!int.TryParse(_port.Text.Trim(), out int port) || port < 1 || port > 65535) { ShowError("Порт — число от 1 до 65535"); return; }
        _status.Text = "";
        _main.Connect(name, host, port);
    }

    private void CreatePressed()
    {
        var size = Sizes[Mathf.Clamp(_mapSize.Selected, 0, Sizes.Length - 1)];
        _main.Net.Send(MsgType.CreateGame, new GameSettings
        {
            Name = _gameName.Text, MapW = size.w, MapH = size.h, AiCount = (int)_ai.Value, MaxPlayers = (int)_humans.Value,
            Simultaneous = _mode.Selected == 0, TurnTimerSec = (int)_timer.Value,
        });
    }

    public void Refresh()
    {
        var net = _main.Net;
        _connectBox.Visible = net.State is NetState.Offline or NetState.Connecting or NetState.Reconnecting;
        _lobbyBox.Visible = net.State == NetState.Lobby;
        _roomBox.Visible = net.State == NetState.Room;
        if (net.State == NetState.Connecting)
        {
            _status.AddThemeColorOverride("font_color", Ui.Dim);
            _status.Text = "Подключение…";
        }
        else if (net.State == NetState.Reconnecting)
        {
            _status.AddThemeColorOverride("font_color", Ui.Dim);
            _status.Text = "Связь потеряна, переподключение…";
        }
        else if (net.State is NetState.Lobby or NetState.Room && _status.Text.EndsWith("…")) _status.Text = "";

        if (net.State == NetState.Lobby)
        {
            Ui.Clear(_games);
            if (net.Lobby.Games.Length == 0) _games.AddChild(Ui.Lbl("Пока нет ни одной игры — создайте свою.", 13, Ui.Dim));
            foreach (var g in net.Lobby.Games)
            {
                int id = g.Id;
                string state = g.Started ? $"идёт, ход {g.Turn}" : $"набор игроков {g.Players}/{g.MaxPlayers}";
                var info = Ui.Lbl($"{g.Name} — {state}", 14);
                info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                _games.AddChild(Ui.H(8, info, Ui.Btn(g.Started ? "Вернуться" : "Войти",
                    () => _main.Net.Send(MsgType.JoinGame, new IntMsg { V = id }),
                    g.Started ? "Занять своё место в идущей игре (нужно то же имя)" : null)));
            }
            if (AutoHost) { AutoHost = false; CreatePressed(); CallDeferred(nameof(AutoStart)); }
            if (AutoJoin && net.Lobby.Games.Length > 0) { AutoJoin = false; _main.Net.Send(MsgType.JoinGame, new IntMsg { V = net.Lobby.Games[0].Id }); }
        }
        if (net.State == NetState.Room && net.Room != null)
        {
            var room = net.Room;
            var st = room.Settings;
            _roomTitle.Text = room.Info.Name;
            _roomInfo.Text = $"Карта {st.MapW}x{st.MapH} · {(st.Simultaneous ? "одновременные ходы" : "ходы по очереди")}" +
                             (st.TurnTimerSec > 0 ? $" · таймер {st.TurnTimerSec} с" : "");
            Ui.Clear(_slots);
            for (int i = 0; i < room.Slots.Length; i++)
            {
                var s = room.Slots[i];
                string tag = s.AI ? "ИИ" : i == room.HostSlot ? "хост" : s.Ready ? "готов" : "не готов";
                _slots.AddChild(Ui.Lbl($"{i + 1}. {s.Name}  [{tag}]{(i == room.YourSlot ? "  ← вы" : "")}", 15,
                    s.AI ? Ui.Dim : Ui.Col(Rules.PlayerColors[i % Rules.PlayerColors.Length])));
            }
            bool host = room.YourSlot == room.HostSlot;
            _startBtn.Visible = host;
            _readyBtn.Visible = !host;
            _readyBtn.Text = room.Slots[room.YourSlot].Ready ? "Не готов" : "Готов";
        }
    }

    private void AutoStart() => _main.Net.Send(MsgType.StartGame);
}
