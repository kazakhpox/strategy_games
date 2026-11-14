using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

public enum Win { None, City, Science, Economy, Diplomacy, Demographics, Civics, Religion, Branches, Help }

/// <summary>In-game screen: map, HUD, advisors. Sends commands; all rules are decided by the server.</summary>
public partial class GameScreen : Control
{
    public readonly NetClient Net;
    public readonly ClientWorld W;
    public readonly MapView Map;
    public readonly CityBanners Banners;
    public Minimap? Minimap;
    private readonly Main _main;
    public readonly Sfx Sound = new();

    private const int TopH = 46, CityW = 452;

    // ---- top bar
    private readonly Label _turn = Ui.Lbl("", 15, null, true), _era = Ui.Lbl("", 13, Ui.Dim), _timer = Ui.Lbl("", 16, Ui.Gold, true),
        _itrText = Ui.Lbl("", 13, null, true);
    private readonly PanelContainer _itr = new() { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
    private readonly Chip _gold = new(Ico.Gold, Ui.Gold), _science = new(Ico.Science, Ui.Science), _culture = new(Ico.Culture, Ui.Culture),
        _faith = new(Ico.Faith, Ui.Faith), _favor = new(Ico.Favor, Ui.Favor);
    private readonly List<Chip> _power = new();
    private Button _leave = null!;
    private double _leaveArmedUntil;
    // ---- left column, log, bottom
    private readonly VBoxContainer _trackers = Ui.V(6), _log = Ui.V(3), _tileBox = Ui.V(3), _unitBox = Ui.V(6), _oddsBox = Ui.V(4), _attention = Ui.V(3);
    private PanelContainer _logPanel = null!, _unitPanel = null!, _oddsPanel = null!, _attentionPanel = null!, _toastPanel = null!;
    private Control _leftColumn = null!;
    private readonly Label _ping = Ui.Lbl("", 11, Ui.Dim), _toast = Ui.Lbl("", 15), _banner = Ui.Lbl("Связь потеряна, переподключение…", 18, Ui.Bad, true);
    private readonly Button _endTurn;
    private readonly LineEdit _chat = new() { PlaceholderText = "Сообщение всем (Enter — отправить)" };
    // ---- advisor window and the city panel
    private readonly ColorRect _backdrop = new() { Color = new Color(0.02f, 0.03f, 0.06f, 0.6f), MouseFilter = MouseFilterEnum.Stop, Visible = false };
    private readonly PanelContainer _window = Ui.Panel(16, Ui.Col(0x10172A, 0.99f), Ui.PanelEdge, 10);
    private readonly Label _windowTitle = Ui.Lbl("", 20, Ui.Gold, true), _cityTitle = Ui.Lbl("", 18, Ui.Gold, true);
    private readonly Icon _windowIcon = new(Ico.None, Ui.Gold, 22);
    private readonly HBoxContainer _windowTabs = Ui.H(4);
    private readonly VBoxContainer _windowBody = Ui.V(6), _cityBody = Ui.V(6);
    private readonly ScrollContainer _windowScroll = new(), _cityScroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
    private readonly PanelContainer _cityDock = Ui.Panel(12, Ui.Col(0x10172A, 0.97f), Ui.PanelEdge, 10);
    private VBoxContainer _body = null!;
    private float _bodyW = 780;

    private readonly HashSet<int> _skipped = new();
    private Win _win = Win.None;
    private int _cityId = -1, _lastTurn = -1, _confirmDisband = -1;
    private bool _scrollToFrontier;
    /// <summary>A spell waiting for the player to pick its target on the map (caster 0 = cast by the empire).</summary>
    private (Rules.ActionDef action, int caster)? _aiming;
    private bool _advancePending, _appendQueue, _firstDelta = true;
    private double _toastUntil, _timerShownAt;
    private int _timerBase = -1;

    public bool TypingInChat => _chat.HasFocus();

    /// <summary>The advisor windows, in the order of their tabs.</summary>
    private static readonly (Win win, string title, Ico icon, Color color, string key, string tip)[] Advisors =
    {
        (Win.Science, "Наука", Ico.Science, Ui.Science, "F6", "Дерево технологий"),
        (Win.Civics, "Культура", Ico.Culture, Ui.Culture, "F7", "Социальные институты"),
        (Win.Branches, "Ветки", Ico.Branch, Ui.Power, "F10", "Особые ветки развития: магия, шаманство и ваши собственные"),
        (Win.Religion, "Религия", Ico.Faith, Ui.Faith, "F8", "Вера, религии мира и миссионеры"),
        (Win.Economy, "Экономика", Ico.Gold, Ui.Gold, "F2", "Казна, содержание и ваше положение в системе «Анти-Сноуболл»"),
        (Win.Diplomacy, "Дипломатия", Ico.Favor, Ui.Favor, "F4", "Державы, договоры и Всемирный конгресс"),
        (Win.Demographics, "Рейтинг", Ico.Chart, Ui.Good, "F9", "Демография, рейтинг держав и мировые достижения"),
        (Win.Help, "Справка", Ico.Help, Ui.Dim, "F1", "Управление и краткие правила"),
    };

    public GameScreen(Main main, ClientWorld world)
    {
        _main = main; Net = main.Net; W = world;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Map = new MapView(this, world);
        AddChild(Map);
        Banners = new CityBanners(this);
        AddChild(Banners);
        AddChild(Sound);
        Sound.Muted = main.Setting("mute", "0") == "1";

        BuildTopBar();
        BuildLeftColumn();
        BuildLog();
        BuildBottom(out _endTurn);
        BuildOverlay();
        BuildWindows();

        Resized += FitWindow;
        HoverChanged(-1);
        Log("Игра началась. F1 — справка по управлению.", Ui.Dim);
    }

    // ------------------------------------------------------------ layout
    private void BuildTopBar()
    {
        var top = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var style = Ui.Box(Ui.Col(0x0C1220, 0.97f), null, 0, 12, 6);
        style.BorderWidthBottom = 1;
        style.BorderColor = Ui.Col(0x8C7A45, 0.7f);
        top.AddThemeStyleboxOverride("panel", style);
        top.CustomMinimumSize = new Vector2(0, TopH);

        _gold.TooltipText = "Золото: казна и доход за ход. Покупка в городах, улучшение юнитов, содержание. Подробности — «Экономика» (F2)";
        _science.TooltipText = "Наука за ход. Двигает дерево технологий (F6)";
        _culture.TooltipText = "Культура за ход. Двигает социальные институты (F7) и расширяет границы городов";
        _faith.TooltipText = "Вера: запас и доход. Основание религии, миссионеры, покупка зданий и мирных юнитов (F8)";
        _favor.TooltipText = "Дипломатическое одобрение: голоса во Всемирном конгрессе (F4). Его дают мир, единоверцы и мировые достижения";
        foreach (var c in new[] { _gold, _science, _culture, _faith, _favor }) c.MouseFilter = MouseFilterEnum.Stop;
        var chips = Ui.H(16, _gold, _science, _culture, _faith, _favor);
        for (int i = 0; i < Rules.Branches.Count; i++)
        {
            var b = Rules.Branches[i];
            var chip = new Chip(Ico.Power, PowerColor(i), $"{b.PowerName} — ресурс ветки «{b.Name}» для её действий и заклинаний (не больше {Rules.PowerCap}). Окно веток — F10");
            _power.Add(chip);
            if (i < 3) chips.AddChild(chip);
        }

        var itrRow = Ui.H(6, _itrText);
        _itrText.MouseFilter = MouseFilterEnum.Ignore;
        _itr.AddChild(itrRow);
        _itr.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Toggle(Win.Economy, true); };

        _leave = Ui.Btn("Выйти", LeavePressed, "Покинуть игру. Место сохранится — вернуться можно из списка игр под тем же именем", 13);
        var turnBox = Ui.H(7, new Icon(Ico.Turn, Ui.Gold, 15), _turn, _era);
        var row = Ui.H(14, turnBox, new VSeparator(), chips, Ui.Spacer(), _timer, _itr, _leave);
        top.AddChild(row);
        AddChild(Ui.Dock(top, SizeFlags.Fill, SizeFlags.ShrinkBegin, 0, 0, 0, 0));
    }

    internal static Color PowerColor(int branch) => (branch % 4) switch { 0 => Ui.Col(0x7FE0A8), 1 => Ui.Power, 2 => Ui.Col(0xFF9EC4), _ => Ui.Col(0x8FD0FF) };

    private void LeavePressed()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _leaveArmedUntil) { Net.Send(MsgType.LeaveGame); return; }
        _leaveArmedUntil = now + 3;
        _leave.Text = "Точно выйти?";
        _leave.ThemeTypeVariation = "Danger";
    }

    private void BuildLeftColumn()
    {
        var nav = Ui.V(3);
        foreach (var a in Advisors.Where(a => a.win is not (Win.Science or Win.Civics)))
        {
            var win = a.win;
            nav.AddChild(Ui.RowBtn(a.title, a.key, () => Toggle(win), a.tip, a.icon, a.color));
        }
        var navPanel = Ui.Panel(5, Ui.Col(0x10172A, 0.9f));
        navPanel.AddChild(nav);
        var column = Ui.V(6, _trackers, navPanel);
        column.CustomMinimumSize = new Vector2(226, 0);
        column.MouseFilter = MouseFilterEnum.Ignore;
        _trackers.MouseFilter = MouseFilterEnum.Ignore;
        _leftColumn = column;
        AddChild(Ui.Dock(column, SizeFlags.ShrinkBegin, SizeFlags.ShrinkBegin, 8, TopH + 8));
    }

    private void BuildLog()
    {
        _logPanel = Ui.Panel(8, Ui.Col(0x0A0F1B, 0.74f), Ui.Col(0x2C3854, 0.5f));
        _logPanel.MouseFilter = MouseFilterEnum.Ignore;
        _logPanel.CustomMinimumSize = new Vector2(308, 0);
        _chat.Visible = false;
        _chat.TextSubmitted += text =>
        {
            if (text.Trim().Length > 0) Net.Send(MsgType.Chat, new TextMsg { Text = text });
            _chat.Text = "";
            _chat.ReleaseFocus();
        };
        _chat.FocusExited += () => _chat.Visible = false;
        _log.MouseFilter = MouseFilterEnum.Ignore;
        var hint = Ui.Lbl("T — написать в чат", 11, new Color(Ui.Dim, 0.7f));
        hint.MouseFilter = MouseFilterEnum.Ignore;
        var box = Ui.V(4, _log, _chat, hint);
        box.MouseFilter = MouseFilterEnum.Ignore;
        _logPanel.AddChild(box);
        AddChild(Ui.Dock(_logPanel, SizeFlags.ShrinkEnd, SizeFlags.ShrinkBegin, 8, TopH + 8));
    }

    private void BuildBottom(out Button endTurn)
    {
        // minimap with the description of the tile under the cursor
        Minimap = new Minimap(this);
        var miniPanel = Ui.Panel(6);
        _tileBox.CustomMinimumSize = new Vector2(250, 40);
        miniPanel.AddChild(Ui.V(5, Minimap, _tileBox));
        _ping.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(Ui.Dock(miniPanel, SizeFlags.ShrinkBegin, SizeFlags.ShrinkEnd));

        // the selected unit, with the forecast of a fight above it
        _unitPanel = Ui.Panel(10);
        _unitPanel.CustomMinimumSize = new Vector2(470, 0);
        _unitPanel.AddChild(_unitBox);
        _oddsPanel = Ui.Panel(9, Ui.Col(0x1B1420, 0.96f), Ui.Col(0x8A3B40));
        _oddsPanel.AddChild(_oddsBox);
        _oddsPanel.MouseFilter = MouseFilterEnum.Ignore;
        _oddsPanel.Visible = false;
        var middle = Ui.V(6, _oddsPanel, _unitPanel);
        middle.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(Ui.Dock(middle, SizeFlags.ShrinkCenter, SizeFlags.ShrinkEnd));

        // what still needs a decision, and the end turn button
        _attentionPanel = Ui.Panel(6, Ui.Col(0x10172A, 0.93f));
        _attentionPanel.AddChild(Ui.V(4, Ui.Lbl("ТРЕБУЕТ ВНИМАНИЯ", 11, Ui.Col(0xC9B36A), true), _attention));
        endTurn = Ui.Btn("Завершить ход", EndTurn, "Enter", 17);
        endTurn.CustomMinimumSize = new Vector2(268, 54);
        endTurn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var endPanel = Ui.Panel(6);
        endPanel.AddChild(Ui.V(3, endTurn, _ping));
        var right = Ui.V(6, _attentionPanel, endPanel);
        right.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(Ui.Dock(right, SizeFlags.ShrinkEnd, SizeFlags.ShrinkEnd));
    }

    private void BuildOverlay()
    {
        _toastPanel = Ui.Panel(10, Ui.Col(0x0C111D, 0.95f), Ui.PanelEdge, 16);
        _toastPanel.MouseFilter = MouseFilterEnum.Ignore;
        _toast.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toastPanel.AddChild(_toast);
        _toastPanel.Visible = false;
        var bannerPanel = Ui.Panel(10, Ui.Col(0x3A181D, 0.96f), Ui.Bad, 16);
        bannerPanel.AddChild(_banner);
        bannerPanel.Visible = false;
        bannerPanel.MouseFilter = MouseFilterEnum.Ignore;
        bannerPanel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _toastPanel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        var overlay = Ui.V(6, bannerPanel, _toastPanel);
        overlay.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(Ui.Dock(overlay, SizeFlags.ShrinkCenter, SizeFlags.ShrinkBegin, 8, TopH + 12));
    }

    private void BuildWindows()
    {
        // advisors: a dimmed map behind and a centred window; a click outside closes it
        _backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _backdrop.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) Toggle(Win.None); };
        AddChild(_backdrop);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        _windowScroll.AddChild(_windowBody);
        _windowBody.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var close = Ui.Btn("Закрыть  Esc", () => Toggle(Win.None), size: 13);
        _window.AddChild(Ui.V(8, Ui.H(10, _windowIcon, _windowTitle, Ui.Spacer(), close), _windowTabs, new HSeparator(), _windowScroll));
        _window.Visible = false;
        center.AddChild(_window);

        // the city: a panel docked to the right, the map stays in view to show worked tiles and to buy land
        _cityScroll.AddChild(_cityBody);
        _cityBody.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _cityBody.CustomMinimumSize = new Vector2(CityW - 12, 0);
        _cityScroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        _cityScroll.CustomMinimumSize = new Vector2(CityW, 0);
        var header = Ui.H(6, _cityTitle, Ui.Spacer(),
            Ui.Btn("◀", () => CycleCity(-1), "Предыдущий город", 13), Ui.Btn("▶", () => CycleCity(1), "Следующий город", 13),
            Ui.Btn("Закрыть  Esc", () => Toggle(Win.None), size: 13));
        _cityDock.AddChild(Ui.V(8, header, new HSeparator(), _cityScroll));
        _cityDock.Visible = false;
        AddChild(Ui.Dock(_cityDock, SizeFlags.ShrinkEnd, SizeFlags.Fill, 8, TopH + 8, 8, 100)); // the end turn button stays in reach below
        _body = _windowBody;
        FitWindow();
    }

    private void FitWindow()
    {
        if (!IsInsideTree()) return;
        var size = GetViewportRect().Size;
        _bodyW = Mathf.Clamp(size.X - 150, 760, 1120);
        _windowScroll.CustomMinimumSize = new Vector2(_bodyW, Mathf.Clamp(size.Y - 210, 380, 760));
    }

    public override void _Ready() { FitWindow(); Map.ApplyCamera(); }

    // ---------------------------------------------------------- messages
    public void OnNetState(NetState s) => ((Control)_banner.GetParent()).Visible = s == NetState.Reconnecting;

    public void OnDelta(DeltaMsg d)
    {
        Map.OnDelta(d);
        Minimap?.MarkDirty();
        if (d.Events.Any(e => e.Type is EventKind.Combat or EventKind.Ranged)) Sound.Play(Sfx.Id.Combat);
        foreach (var e in d.Events)
        {
            string? text = W.Describe(e);
            if (text == null) continue;
            bool bad = e.Type is EventKind.Protest or EventKind.Secession or EventKind.Bankrupt or EventKind.WarDeclared;
            Log(text, bad ? Ui.Bad : e.Type switch
            {
                EventKind.TechDone => Ui.Science,
                EventKind.CivicDone => Ui.Culture,
                EventKind.NodeDone => Ui.Power,
                EventKind.ReligionFounded or EventKind.CityConverted => Ui.Faith,
                EventKind.Milestone or EventKind.Congress or EventKind.Resolution => Ui.Gold,
                EventKind.PeaceMade or EventKind.BordersOpened => Ui.Good,
                _ => Ui.Text,
            });
            if (e.Type == EventKind.Milestone && e.A == W.MyId) Toast($"Мировое достижение: {Array.Find(Rules.Milestones, m => m.Id == e.S)?.Name}! +{Rules.MilestoneFavor} одобрения", false);
            if (e.Type is EventKind.CivicDone or EventKind.ReligionFounded or EventKind.NodeDone or EventKind.Milestone) Sound.Play(Sfx.Id.Tech);
            switch (e.Type)
            {
                case EventKind.TechDone: Sound.Play(Sfx.Id.Tech); break;
                case EventKind.BuildDone: Sound.Play(Sfx.Id.Build); break;
                case EventKind.CityFounded or EventKind.CityCaptured or EventKind.Congress: Sound.Play(Sfx.Id.City); break;
                case EventKind.Protest or EventKind.Secession or EventKind.WarDeclared or EventKind.Bankrupt: Sound.Play(Sfx.Id.Error); break;
            }
        }
        if (d.TimerLeft >= 0) { _timerBase = d.TimerLeft; _timerShownAt = Time.GetTicksMsec() / 1000.0; }
        else if (W.TimerLeft < 0) _timerBase = -1;

        if (W.Turn != _lastTurn)
        {
            if (_lastTurn >= 0) Sound.Play(Sfx.Id.Turn);
            _lastTurn = W.Turn;
            _skipped.Clear();
            _advancePending = true;
        }
        if (_firstDelta)
        {
            _firstDelta = false;
            var home = W.Cities.Values.FirstOrDefault(c => c.Owner == W.MyId && c.Capital)?.Tile
                       ?? W.Units.Values.FirstOrDefault(u => u.Owner == W.MyId)?.Tile ?? -1;
            if (home >= 0) Map.CenterOn(home);
            _advancePending = true;
        }
        if (_advancePending && W.MyTurn)
        {
            bool busy = Map.SelectedUnit >= 0 && W.Units.TryGetValue(Map.SelectedUnit, out var cur) && IsReady(cur);
            if (!busy) { _advancePending = false; SelectNext(false); }
        }
        if (_win == Win.City && (!W.Cities.TryGetValue(_cityId, out var city) || city.Owner != W.MyId)) Toggle(Win.None);
        Refresh();
        RebuildWindow();
    }

    public void OnCommandResult(int seq, bool ok, string error)
    {
        Map.OnCommandResult(seq, ok);
        if (!ok) { Toast(error, true); Sound.Play(Sfx.Id.Error); }
    }

    public void Toast(string text, bool bad)
    {
        _toast.Text = text;
        // one line when it fits, wrapped when the message is long
        _toast.CustomMinimumSize = new Vector2(Math.Min(620, ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, 15).X + 8), 0);
        _toast.AddThemeColorOverride("font_color", bad ? Ui.Col(0xFFB3AC) : Ui.Text);
        _toastPanel.AddThemeStyleboxOverride("panel", Ui.Box(bad ? Ui.Col(0x33161A, 0.96f) : Ui.Col(0x0C111D, 0.95f), bad ? Ui.Bad : Ui.PanelEdge, 16, 14, 8));
        _toastPanel.Visible = true;
        _toastUntil = Time.GetTicksMsec() / 1000.0 + 3.5;
    }

    public void Log(string text, Color color)
    {
        var l = Ui.Wrap(text, 290, 13, color);
        l.MouseFilter = MouseFilterEnum.Ignore;
        _log.AddChild(l);
        while (_log.GetChildCount() > 7) { var first = _log.GetChild(0); _log.RemoveChild(first); first.QueueFree(); }
        // older lines fade, so the eye lands on the newest
        int n = _log.GetChildCount();
        for (int i = 0; i < n; i++) ((Control)_log.GetChild(i)).Modulate = new Color(1, 1, 1, 0.5f + 0.5f * (i + 1) / n);
    }

    public override void _Process(double delta)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (_toastPanel.Visible && now > _toastUntil) _toastPanel.Visible = false;
        if (_leaveArmedUntil > 0 && now > _leaveArmedUntil) { _leaveArmedUntil = 0; _leave.Text = "Выйти"; _leave.ThemeTypeVariation = ""; }
        string ping = Net.PingMs >= 0 ? $"пинг {Net.PingMs} мс" : "";
        if (_ping.Text != ping) _ping.Text = ping;
        if (_timerBase >= 0)
        {
            int left = Math.Max(0, _timerBase - (int)(now - _timerShownAt));
            string text = $"{left / 60}:{left % 60:00}";
            if (_timer.Text != text) _timer.Text = text;
            _timer.AddThemeColorOverride("font_color", left <= 10 ? Ui.Bad : Ui.Gold);
            _timer.Visible = true;
        }
        else _timer.Visible = false;
    }

    // ------------------------------------------------------------- input
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        switch (k.Keycode)
        {
            case Key.Enter or Key.KpEnter: EndTurn(); break;
            case Key.Escape:
                if (_aiming != null) StopAiming("Отменено");
                else if (_win != Win.None) Toggle(Win.None);
                else { Map.Select(-1); Refresh(); }
                break;
            case Key.Space: SkipUnit(); break;
            case Key.N: SelectNext(true); break;
            case Key.B: UnitCommand(CmdType.FoundCity); break;
            case Key.F: UnitCommand(CmdType.Fortify); break;
            case Key.C: if (Selected is { } s) Map.CenterOn(s.Tile); break;
            case Key.T: _chat.Visible = true; _chat.GrabFocus(); break;
            case Key.M:
                Sound.Muted = !Sound.Muted;
                _main.SaveSetting("mute", Sound.Muted ? "1" : "0");
                Toast(Sound.Muted ? "Звук выключен" : "Звук включён", false);
                break;
            case Key.F1: Toggle(Win.Help); break;
            case Key.F2: Toggle(Win.Economy); break;
            case Key.F4: Toggle(Win.Diplomacy); break;
            case Key.F6: Toggle(Win.Science); break;
            case Key.F9: Toggle(Win.Demographics); break;
            case Key.F7: Toggle(Win.Civics); break;
            case Key.F8: Toggle(Win.Religion); break;
            case Key.F10: Toggle(Win.Branches); break;
            case Key.P: UnitCommand(CmdType.Preach); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    private UnitDto? Selected => Map.SelectedUnit >= 0 && W.Units.TryGetValue(Map.SelectedUnit, out var u) ? u : null;

    private bool IsReady(UnitDto u) => u.Owner == W.MyId && u.Moves > 0 && !u.Fortified && u.Work == 0 && u.Goto < 0 && !_skipped.Contains(u.Id);

    public void SelectNext(bool wrap)
    {
        var ready = W.Units.Values.Where(IsReady).OrderBy(u => u.Id).ToList();
        var next = ready.FirstOrDefault(u => u.Id > Map.SelectedUnit) ?? (wrap || Selected == null || !IsReady(Selected) ? ready.FirstOrDefault() : null);
        if (next == null) { if (Selected != null && !IsReady(Selected)) Map.Select(-1); Refresh(); return; }
        Map.Select(next.Id);
        if (!Map.OnScreen(next.Tile)) Map.CenterOn(next.Tile);
        Refresh();
    }

    private void SkipUnit()
    {
        if (Selected is { } u) _skipped.Add(u.Id);
        SelectNext(true);
    }

    private void UnitCommand(CmdType type, int b = 0)
    {
        if (Selected is not { } u || u.Owner != W.MyId) return;
        if (type == CmdType.FoundCity && u.Def.Role != UnitRole.Settler) return;
        Net.SendCommand(type, u.Id, b);
        _advancePending = true;
    }

    /// <summary>Starts or immediately performs an action; targeted ones wait for a click on the map.</summary>
    public void BeginAction(Rules.ActionDef a, int casterId)
    {
        if (a.Target == "None") { Net.SendCommand(CmdType.Cast, casterId, -1, 0, a.Id); _advancePending = casterId != 0; return; }
        _aiming = (a, casterId);
        if (_win != Win.None) Toggle(Win.None);
        // show where the spell may land
        if (a.Target == "City") Map.Highlight = W.Cities.Values.Where(c => c.Owner == W.MyId).Select(c => c.Tile).ToArray();
        else
        {
            int range = Math.Max(1, a.Range);
            var origins = casterId != 0 && W.Units.TryGetValue(casterId, out var cu) ? new List<int> { cu.Tile }
                : W.Cities.Values.Where(c => c.Owner == W.MyId).Select(c => c.Tile).ToList();
            Map.Highlight = origins.SelectMany(o => W.Grid.Range(o, range)).Distinct().Where(t => W.Vis[t] > 0).ToArray();
        }
        Map.Invalidate();
        Toast($"{a.Name}: выберите {(a.Target == "City" ? "свой город" : "клетку")} левой кнопкой (Esc — отмена)", false);
        _toastUntil += 60; // the hint stays for as long as the target is being picked
    }

    private void StopAiming(string? message = null)
    {
        _aiming = null;
        Map.Highlight = Array.Empty<int>();
        Map.Invalidate();
        _toastUntil = 0;
        if (message != null) Toast(message, false);
    }

    public void TileClicked(int tile)
    {
        if (tile < 0) return;
        if (_aiming is { } aim)
        {
            Net.SendCommand(CmdType.Cast, aim.caster, tile, 0, aim.action.Id);
            Sound.Play(Sfx.Id.Click);
            StopAiming();
            _advancePending = aim.caster != 0;
            return;
        }
        _confirmDisband = -1;
        var own = W.UnitsAt(tile).Where(u => u.Owner == W.MyId).OrderBy(u => u.Id).ToList();
        var city = W.CityAt(tile);
        bool selectedHere = own.Any(u => u.Id == Map.SelectedUnit);
        if (city != null && city.Owner == W.MyId && (own.Count == 0 || selectedHere || _win == Win.City)) { OpenCity(city.Id); return; }
        if (own.Count > 0)
        {
            var next = own.FirstOrDefault(u => u.Id > Map.SelectedUnit && selectedHere) ?? own[0];
            Map.Select(next.Id);
            Sound.Play(Sfx.Id.Click);
        }
        else Map.Select(-1);
        if (_win == Win.City) Toggle(Win.None);
        Refresh();
    }

    /// <summary>A click on a city banner: opens the player's own city. Returns false when the click belongs to the map.</summary>
    public bool BannerClicked(Vector2 at)
    {
        if (_aiming != null) return false;
        int id = Banners.CityAt(at);
        if (id < 0 || !W.Cities.TryGetValue(id, out var c) || c.Owner != W.MyId) return false;
        Sound.Play(Sfx.Id.Click);
        OpenCity(id);
        return true;
    }

    private bool HasRes(Resource r) => r == Resource.None || W.Me.Resources.Contains((int)r);
    private bool CoastalCity(CityDto c) => W.Grid.Nb[c.Tile].Any(n => W.Vis[n] > 0 && W.Tiles[n].IsWater);

    public void TileOrdered(int tile)
    {
        if (tile >= 0 && _win == Win.City && W.Cities.TryGetValue(_cityId, out var buyer))
        {
            // with the city screen open, a right click buys land for that city
            Net.SendCommand(CmdType.BuyTile, buyer.Id, tile);
            return;
        }
        if (tile < 0 || Selected is not { } u || u.Owner != W.MyId) return;
        if (_aiming != null) { StopAiming("Отменено"); return; }
        if (!W.MyTurn) { Toast("Сейчас не ваш ход", true); return; }
        if (u.Def.X.Ranged > 0 && u.Moves > 0 && W.Grid.Distance(u.Tile, tile) <= u.Def.X.Ranged
            && W.UnitsAt(tile).Any(o => o.Owner != W.MyId && W.Players.TryGetValue(o.Owner, out var foe) && foe.AtWar))
        {
            // enemies within reach: this is a shot, not a march, so there is no path to predict
            Net.SendCommand(CmdType.MoveUnit, u.Id, tile);
            Sound.Play(Sfx.Id.Combat);
            _advancePending = true;
            return;
        }
        if (u.Moves <= 0) { Toast("У этого юнита не осталось очков движения в этом ходу", true); Sound.Play(Sfx.Id.Error); return; }
        Map.OrderMove(u, tile);
        Sound.Play(Sfx.Id.Move);
        _advancePending = true;
    }

    private void EndTurn()
    {
        if (!W.MyTurn) return;
        Net.SendCommand(CmdType.EndTurn);
    }

    private static string Cost(int v) => v > 0 ? $"−{v}" : "0";
    private static string Turns(int remaining, int rate) => rate <= 0 ? "∞" : $"{Math.Max(1, (remaining + rate - 1) / rate)} х.";
}
