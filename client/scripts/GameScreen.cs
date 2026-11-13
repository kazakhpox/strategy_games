using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

public enum Win { None, City, Science, Economy, Diplomacy, Demographics }

/// <summary>In-game screen: map, HUD, advisors. Sends commands; all rules are decided by the server.</summary>
public partial class GameScreen : Control
{
    public readonly NetClient Net;
    public readonly ClientWorld W;
    public readonly MapView Map;
    public Minimap? Minimap;
    private readonly Main _main;

    private readonly Label _turn = Ui.Lbl("", 15), _gold = Ui.Lbl("", 15, Ui.Gold), _science = Ui.Lbl("", 15, Ui.Science),
        _itr = Ui.Lbl("", 15), _ping = Ui.Lbl("", 12, Ui.Dim), _timer = Ui.Lbl("", 18, Ui.Gold), _hover = Ui.Lbl("", 12, Ui.Dim),
        _toast = Ui.Lbl("", 18, Ui.Bad), _banner = Ui.Lbl("Связь потеряна, переподключение…", 20, Ui.Bad);
    private readonly VBoxContainer _unitBox = Ui.V(4), _log = Ui.V(2);
    private readonly Button _endTurn;
    private readonly LineEdit _chat = new() { PlaceholderText = "Чат (Enter — отправить)" };
    private readonly PanelContainer _window = Ui.Panel(12, new Color(0.07f, 0.09f, 0.13f, 0.985f));
    private readonly Label _windowTitle = Ui.Lbl("", 20, Ui.Gold);
    private readonly VBoxContainer _windowBody = Ui.V(6);
    private readonly ScrollContainer _windowScroll = new();
    private readonly HashSet<int> _skipped = new();
    private Win _win = Win.None;
    private int _cityId = -1, _lastTurn = -1, _confirmDisband = -1;
    private bool _advancePending, _appendQueue, _firstDelta = true;
    private double _toastUntil, _timerShownAt;
    private int _timerBase = -1;

    public bool TypingInChat => _chat.HasFocus();

    public GameScreen(Main main, ClientWorld world)
    {
        _main = main; Net = main.Net; W = world;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Map = new MapView(this, world);
        AddChild(Map);

        // ---- top bar
        var top = Ui.Panel(6);
        var topRow = Ui.H(18, _turn, _gold, _science, _itr, Ui.Spacer(), _ping,
            Ui.Btn("Наука F6", () => Toggle(Win.Science)), Ui.Btn("Экономика F2", () => Toggle(Win.Economy)),
            Ui.Btn("Дипломатия F4", () => Toggle(Win.Diplomacy)), Ui.Btn("Демография F9", () => Toggle(Win.Demographics)),
            Ui.Btn("Выйти", () => Net.Send(MsgType.LeaveGame), "Покинуть игру. Место сохранится — вернуться можно из списка игр под тем же именем"));
        top.AddChild(topRow);
        _itr.MouseFilter = MouseFilterEnum.Stop;

        // ---- right column: event log + chat
        var logPanel = Ui.Panel(6, new Color(0.05f, 0.06f, 0.1f, 0.78f));
        logPanel.CustomMinimumSize = new Vector2(380, 0);
        logPanel.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _chat.TextSubmitted += text =>
        {
            if (text.Trim().Length > 0) Net.Send(MsgType.Chat, new TextMsg { Text = text });
            _chat.Text = "";
            _chat.ReleaseFocus();
        };
        logPanel.AddChild(Ui.V(4, _log, _chat));
        var middle = Ui.H(0, Ui.Spacer(), logPanel);
        middle.SizeFlagsVertical = SizeFlags.ExpandFill;
        middle.MouseFilter = MouseFilterEnum.Ignore;

        // ---- bottom row
        Minimap = new Minimap(this);
        var miniPanel = Ui.Panel(4);
        miniPanel.AddChild(Ui.V(2, Minimap, _hover));
        var unitPanel = Ui.Panel(8);
        unitPanel.CustomMinimumSize = new Vector2(420, 0);
        unitPanel.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        unitPanel.AddChild(_unitBox);
        _endTurn = Ui.Btn("Завершить ход", EndTurn, "Enter", 18);
        _endTurn.CustomMinimumSize = new Vector2(260, 64);
        _endTurn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var endPanel = Ui.Panel(8);
        endPanel.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        _timer.HorizontalAlignment = HorizontalAlignment.Center;
        endPanel.AddChild(Ui.V(4, _timer, _endTurn));
        var bottom = Ui.H(8, miniPanel, Ui.Spacer(), unitPanel, Ui.Spacer(), endPanel);
        bottom.MouseFilter = MouseFilterEnum.Ignore;

        var layout = Ui.V(0, top, middle, bottom);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layout.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(layout);

        // ---- floating window for city screen and advisors
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        _windowScroll.CustomMinimumSize = new Vector2(820, 520);
        _windowScroll.AddChild(_windowBody);
        _windowBody.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _window.AddChild(Ui.V(8, Ui.H(8, _windowTitle, Ui.Spacer(), Ui.Btn("Закрыть (Esc)", () => Toggle(Win.None))), new HSeparator(), _windowScroll));
        _window.Visible = false;
        center.AddChild(_window);

        var overlay = Ui.V(6, _banner, _toast);
        overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop, LayoutPresetMode.Minsize, 60);
        overlay.GrowHorizontal = GrowDirection.Both;
        overlay.MouseFilter = MouseFilterEnum.Ignore;
        _banner.Visible = false;
        _toast.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toast.CustomMinimumSize = new Vector2(560, 0);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(overlay);

        Log("Игра началась. ЛКМ — выбрать, ПКМ — идти/атаковать, колесо — масштаб, WASD — камера.", Ui.Dim);
    }

    public override void _Ready() => Map.ApplyCamera();

    // ---------------------------------------------------------- messages
    public void OnNetState(NetState s) => _banner.Visible = s == NetState.Reconnecting;

    public void OnDelta(DeltaMsg d)
    {
        Map.OnDelta(d);
        Minimap?.MarkDirty();
        foreach (var e in d.Events)
        {
            string? text = W.Describe(e);
            if (text == null) continue;
            bool bad = e.Type is EventKind.Protest or EventKind.Secession or EventKind.Bankrupt or EventKind.WarDeclared;
            Log(text, bad ? Ui.Bad : e.Type == EventKind.TechDone ? Ui.Science : Colors.White);
            if (e.Type == EventKind.Proposal) Toggle(Win.Diplomacy, true);
        }
        if (d.TimerLeft >= 0) { _timerBase = d.TimerLeft; _timerShownAt = Time.GetTicksMsec() / 1000.0; }
        else if (W.TimerLeft < 0) _timerBase = -1;

        if (W.Turn != _lastTurn)
        {
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
        if (!ok) Toast(error, true);
    }

    public void Toast(string text, bool bad)
    {
        _toast.Text = text;
        _toast.AddThemeColorOverride("font_color", bad ? Ui.Bad : Colors.White);
        _toastUntil = Time.GetTicksMsec() / 1000.0 + 3.5;
    }

    public void Log(string text, Color color)
    {
        var l = Ui.Lbl(text, 13, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(366, 0);
        _log.AddChild(l);
        while (_log.GetChildCount() > 9) { var first = _log.GetChild(0); _log.RemoveChild(first); first.QueueFree(); }
    }

    public override void _Process(double delta)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (_toast.Text.Length > 0 && now > _toastUntil) _toast.Text = "";
        _ping.Text = Net.PingMs >= 0 ? $"пинг {Net.PingMs} мс" : "";
        if (_timerBase >= 0)
        {
            int left = Math.Max(0, _timerBase - (int)(now - _timerShownAt));
            _timer.Text = $"⏱ {left / 60}:{left % 60:00}";
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
            case Key.Escape: if (_win != Win.None) Toggle(Win.None); else { Map.Select(-1); Refresh(); } break;
            case Key.Space: SkipUnit(); break;
            case Key.N: SelectNext(true); break;
            case Key.B: UnitCommand(CmdType.FoundCity); break;
            case Key.F: UnitCommand(CmdType.Fortify); break;
            case Key.C: if (Selected is { } s) Map.CenterOn(s.Tile); break;
            case Key.T: _chat.GrabFocus(); break;
            case Key.F2: Toggle(Win.Economy); break;
            case Key.F4: Toggle(Win.Diplomacy); break;
            case Key.F6: Toggle(Win.Science); break;
            case Key.F9: Toggle(Win.Demographics); break;
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

    public void TileClicked(int tile)
    {
        if (tile < 0) return;
        _confirmDisband = -1;
        var own = W.UnitsAt(tile).Where(u => u.Owner == W.MyId).OrderBy(u => u.Id).ToList();
        var city = W.CityAt(tile);
        bool selectedHere = own.Any(u => u.Id == Map.SelectedUnit);
        if (city != null && city.Owner == W.MyId && (own.Count == 0 || selectedHere)) { OpenCity(city.Id); return; }
        if (own.Count > 0)
        {
            var next = own.FirstOrDefault(u => u.Id > Map.SelectedUnit && selectedHere) ?? own[0];
            Map.Select(next.Id);
        }
        else Map.Select(-1);
        if (_win == Win.City) Toggle(Win.None);
        Refresh();
    }

    public void TileOrdered(int tile)
    {
        if (tile < 0 || Selected is not { } u || u.Owner != W.MyId) return;
        if (!W.MyTurn) { Toast("Сейчас не ваш ход", true); return; }
        Map.OrderMove(u, tile);
        _advancePending = true;
    }

    public void HoverChanged(int tile)
    {
        if (tile < 0 || W.Vis[tile] == 0) { _hover.Text = ""; return; }
        var t = W.Tiles[tile];
        var (f, p, c) = Rules.TileYield(t);
        string terrain = t.Relief == Relief.Mountain ? "Горы" : t.Terrain switch
        {
            Terrain.Ocean => "Океан", Terrain.Coast => "Побережье", Terrain.Grassland => "Луга", Terrain.Plains => "Равнина",
            Terrain.Desert => "Пустыня", Terrain.Tundra => "Тундра", _ => "Снега",
        };
        if (t.Relief == Relief.Hills) terrain += ", холмы";
        if (t.Forest) terrain += ", лес";
        string res = t.Resource switch
        {
            Resource.Wheat => " · пшеница", Resource.Cattle => " · скот", Resource.Fish => " · рыба", Resource.Iron => " · железо",
            Resource.Horses => " · лошади", Resource.Gold => " · золото", Resource.Stone => " · камень", Resource.Oil => " · нефть",
            Resource.Uranium => " · уран", _ => "",
        };
        string owner = t.Owner >= 0 ? $" · {W.PlayerName(t.Owner)}" : "";
        _hover.Text = $"{terrain}{res} · {f}🌾 {p}⚒ {c}💰{owner}";
    }

    private void EndTurn()
    {
        if (!W.MyTurn) return;
        Net.SendCommand(CmdType.EndTurn);
    }

    // --------------------------------------------------------------- HUD
    private void Refresh()
    {
        var me = W.Me;
        _turn.Text = $"Ход {W.Turn} · {Rules.EraName(Rules.EraOf(me.Known, me.Future))}";
        _gold.Text = $"Золото {me.Gold} ({Ui.Signed(me.GoldRate)})";
        var tech = Rules.Tech(me.Research);
        if (tech == null) _science.Text = $"Наука +{me.SciRate}: исследование не выбрано!";
        else
        {
            int cost = me.Available.FirstOrDefault(a => a.Id == tech.Id)?.Cost ?? tech.Cost;
            _science.Text = $"Наука +{me.SciRate}: {tech.Name} {me.ResearchProgress}/{cost} ({Turns(cost - me.ResearchProgress, me.SciRate)})";
        }
        if (me.Pressure > 0.005f)
        {
            _itr.Text = $"▲ Лидер: давление {me.Pressure * 100:0}%";
            _itr.AddThemeColorOverride("font_color", Ui.Bad);
        }
        else if (me.Lag > 0.005f)
        {
            _itr.Text = $"▼ Догоняющий: бонус {me.Lag * 100:0}%";
            _itr.AddThemeColorOverride("font_color", Ui.Good);
        }
        else
        {
            _itr.Text = "● Паритет";
            _itr.AddThemeColorOverride("font_color", Ui.Dim);
        }
        _itr.TooltipText = $"Индекс технологического разрыва (ITR): {me.Itr:+0.00;-0.00}\nНаука ×{me.SciMult:0.00}, культура ×{me.CulMult:0.00}, содержание ×{me.UpkeepMult:0.00}\nПодробности — в окне «Экономика»";

        // end turn button
        var waiting = W.Players.Values.Where(p => p.Alive && !p.Neutral && !p.AI && !p.EndedTurn && p.Id != W.MyId).Select(p => p.Name).ToList();
        if (!W.Players.TryGetValue(W.MyId, out var self) || !self.Alive) { _endTurn.Text = "Вы выбыли — режим наблюдателя"; _endTurn.Disabled = true; }
        else if (W.MyTurn)
        {
            int idle = W.Units.Values.Count(IsReady);
            bool noResearch = me.Research.Length == 0;
            _endTurn.Text = noResearch ? "Завершить ход\n(не выбрана наука)" : idle > 0 ? $"Завершить ход\n(юнитов без приказа: {idle})" : "Завершить ход";
            _endTurn.Disabled = false;
        }
        else
        {
            _endTurn.Disabled = true;
            _endTurn.Text = W.Settings.Simultaneous
                ? "Ожидание: " + (waiting.Count > 0 ? string.Join(", ", waiting) : "сервер")
                : $"Ходит: {W.PlayerName(W.CurrentPlayer)}";
        }

        // unit panel
        Ui.Clear(_unitBox);
        if (Selected is not { } u)
        {
            _unitBox.AddChild(Ui.Lbl("Юнит не выбран. N — следующий юнит, клик по городу — экран города.", 13, Ui.Dim));
            return;
        }
        var def = u.Def;
        string state = u.Work != 0 ? $"строит: {Rules.WorkName((WorkKind)u.Work)} ({u.WorkTurns} х.)" : u.Fortified ? "укреплён" : u.Goto >= 0 ? "в пути" : "";
        _unitBox.AddChild(Ui.Lbl($"{def.Name}  {(def.Str > 0 ? $"сила {def.Str}  " : "")}HP {u.Hp}  ходы {u.Moves / (float)Rules.MovePoint:0.#}/{def.Moves}  {state}", 15));
        var row = new HFlowContainer();
        _unitBox.AddChild(row);
        if (u.Owner != W.MyId) return;
        if (def.Role == UnitRole.Settler) row.AddChild(Ui.Btn("Основать город (B)", () => UnitCommand(CmdType.FoundCity)));
        if (def.Role == UnitRole.Worker && u.Work == 0)
            foreach (var kind in new[] { WorkKind.Farm, WorkKind.Mine, WorkKind.Cottage, WorkKind.Road })
            {
                var tile = W.Tiles[u.Tile];
                if (!Rules.WorkFits(tile, kind) || !W.Knows(Rules.WorkTech(kind))) continue;
                if (tile.Owner != W.MyId && !(kind == WorkKind.Road && tile.Owner < 0)) continue;
                var k = kind;
                row.AddChild(Ui.Btn($"{Rules.WorkName(kind)} ({Rules.WorkTurns(kind)} х.)", () => UnitCommand(CmdType.Work, (int)k)));
            }
        if (u.Fortified || u.Work != 0 || u.Goto >= 0) row.AddChild(Ui.Btn("Отменить приказ", () => UnitCommand(CmdType.Wake)));
        else row.AddChild(Ui.Btn(def.Str > 0 ? "Укрепиться (F)" : "Спать (F)", () => UnitCommand(CmdType.Fortify)));
        int upTo = Rules.UpgradeTarget(u.Type, W.Knows);
        if (upTo >= 0 && W.Tiles[u.Tile].Owner == W.MyId)
        {
            int price = Rules.UpgradePrice(u.Type, upTo);
            var up = Ui.Btn($"Улучшить: {Rules.Units[upTo].Name} ({price} зол.)", () => UnitCommand(CmdType.Upgrade),
                "Юнит заменяется современным аналогом; тратит ход. Только на своей территории");
            up.Disabled = W.Me.Gold < price || u.Moves <= 0;
            row.AddChild(up);
        }
        row.AddChild(Ui.Btn("Пропустить (Пробел)", SkipUnit));
        int id = u.Id;
        row.AddChild(Ui.Btn(_confirmDisband == id ? "Точно распустить?" : "Распустить", () =>
        {
            if (_confirmDisband == id) { Net.SendCommand(CmdType.Disband, id); _confirmDisband = -1; }
            else { _confirmDisband = id; Refresh(); }
        }));
        var others = W.UnitsAt(u.Tile).Where(o => o.Owner == W.MyId && o.Id != u.Id).OrderBy(o => o.Id).ToList();
        if (others.Count > 0)
        {
            var stack = new HFlowContainer();
            stack.AddChild(Ui.Lbl("Здесь же:", 12, Ui.Dim));
            foreach (var o in others.Take(8))
            {
                int oid = o.Id;
                stack.AddChild(Ui.Btn(o.Def.Name, () => { Map.Select(oid); Refresh(); }, size: 12));
            }
            _unitBox.AddChild(stack);
        }
        if (W.CityAt(u.Tile) is { } here && here.Owner == W.MyId)
        {
            int cid = here.Id;
            row.AddChild(Ui.Btn($"Город {here.Name}", () => OpenCity(cid)));
        }
    }

    private static string Cost(int v) => v > 0 ? $"−{v}" : "0";
    private static string Turns(int remaining, int rate) => rate <= 0 ? "∞" : $"{Math.Max(1, (remaining + rate - 1) / rate)} х.";

    // ----------------------------------------------------------- windows
    public void OpenCity(int id)
    {
        _cityId = id;
        Toggle(Win.City, true);
    }

    public void Toggle(Win w, bool forceOpen = false)
    {
        _win = w == _win && !forceOpen ? Win.None : w;
        _windowScroll.ScrollVertical = 0;
        RebuildWindow();
    }

    private void RebuildWindow()
    {
        _window.Visible = _win != Win.None;
        Map.Highlight = Array.Empty<int>();
        Map.Invalidate();
        if (_win == Win.None) return;
        int scroll = _windowScroll.ScrollVertical;
        Ui.Clear(_windowBody);
        switch (_win)
        {
            case Win.City: BuildCity(); break;
            case Win.Science: BuildScience(); break;
            case Win.Economy: BuildEconomy(); break;
            case Win.Diplomacy: BuildDiplomacy(); break;
            case Win.Demographics: BuildDemographics(); break;
        }
        _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
    }

    private void Add(Control c) => _windowBody.AddChild(c);
    private void Heading(string text) { Add(new HSeparator()); Add(Ui.Lbl(text, 16, Ui.Gold)); }

    private void BuildCity()
    {
        if (!W.Cities.TryGetValue(_cityId, out var c) || c.Detail == null) { _win = Win.None; _window.Visible = false; return; }
        var d = c.Detail;
        Map.Highlight = d.Worked;
        _windowTitle.Text = $"{(c.Capital ? "★ " : "")}{c.Name} — население {c.Pop}";
        if (d.DisorderTurns > 0) Add(Ui.Lbl($"В ГОРОДЕ БЕСПОРЯДКИ: производство остановлено ещё на {d.DisorderTurns} х.", 15, Ui.Bad));
        string growth = d.FoodRate > 0 ? $"рост через {Turns(d.FoodNeed - d.Food, d.FoodRate)}" : d.FoodRate < 0 ? "ГОЛОД" : "застой";
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 28);
        void Stat(string name, string value, Color? col = null) { grid.AddChild(Ui.Lbl(name, 14, Ui.Dim)); grid.AddChild(Ui.Lbl(value, 14, col)); }
        Stat("Пища", $"{d.Food}/{d.FoodNeed} ({Ui.Signed(d.FoodRate)} за ход, {growth})", d.FoodRate < 0 ? Ui.Bad : null);
        Stat("Производство", $"+{d.ProdRate} за ход");
        Stat("Коммерция", $"{d.Commerce} → наука +{d.Science}, золото +{d.Gold}");
        Stat("Культура", $"{d.Culture} (+{d.CultureRate} за ход), радиус границ {c.Radius}" +
                         (c.Radius < 4 ? $", расширение при {Rules.CultureForRadius(c.Radius + 1)}" : ""));
        Stat("Довольство", $"лимит {d.Happy}, недовольных {d.Unhappy}", d.Unhappy > 0 ? Ui.Bad : null);
        Stat("Социальное брожение", $"{d.Unrest}/100" + (d.Unrest >= 60 ? " — близко к протестам!" : ""), d.Unrest >= 60 ? Ui.Bad : d.Unrest >= 30 ? Ui.Gold : null);
        Stat("Содержание", $"{d.Maint} золота за ход");
        Stat("Постройки", d.Buildings.Length == 0 ? "нет" : string.Join(", ", d.Buildings.Select(b => Rules.Building(b)?.Name ?? b)));
        Add(grid);

        Heading("Очередь производства");
        if (d.Queue.Length == 0) Add(Ui.Lbl("Пусто — город простаивает. Выберите, что строить.", 14, Ui.Bad));
        for (int i = 0; i < d.Queue.Length; i++)
        {
            string item = d.Queue[i];
            int cost = Rules.ItemCost(item), idx = i;
            string text = item == Rules.Wealth ? "Богатство (производство → золото)"
                : i == 0 ? $"{Rules.ItemName(item)} — {Math.Min(d.Prod, cost)}/{cost} ({Turns(cost - d.Prod, d.ProdRate)})"
                : $"{Rules.ItemName(item)} — {cost}";
            var row = Ui.H(8, Ui.Lbl($"{i + 1}. {text}", 14));
            if (i == 0 && item != Rules.Wealth && Rules.BuyPrice(cost, d.Prod) > 0)
            {
                int price = Rules.BuyPrice(cost, d.Prod);
                var buy = Ui.Btn($"Купить за {price}", () => Net.SendCommand(CmdType.Buy, c.Id));
                buy.Disabled = W.Me.Gold < price;
                row.AddChild(buy);
            }
            row.AddChild(Ui.Btn("✕", () => Net.SendCommand(CmdType.RemoveQueue, c.Id, idx), "Убрать из очереди"));
            Add(row);
        }

        Heading("Построить");
        var append = new CheckBox { Text = "Добавлять в конец очереди (иначе заменяет текущий проект)", ButtonPressed = _appendQueue, FocusMode = FocusModeEnum.None };
        append.Toggled += on => _appendQueue = on;
        Add(append);
        var cols = Ui.H(24);
        var units = Ui.V(3, Ui.Lbl("Юниты", 14, Ui.Dim));
        var builds = Ui.V(3, Ui.Lbl("Здания", 14, Ui.Dim));
        cols.AddChild(units); cols.AddChild(builds);
        Add(cols);
        void Option(VBoxContainer col, string item, string name, int cost, string tip)
        {
            string label = cost > 0 ? $"{name} — {cost} ({Turns(cost, d.ProdRate)})" : name;
            var b = Ui.Btn(label, () => Net.SendCommand(CmdType.SetProduction, c.Id, _appendQueue ? 1 : 0, 0, item), tip);
            b.Alignment = HorizontalAlignment.Left;
            col.AddChild(b);
        }
        foreach (var ud in Rules.Units.Where(x => W.Knows(x.Tech)).OrderByDescending(x => x.Cost).Take(9).Reverse())
            Option(units, "u:" + ud.Id, ud.Name, ud.Cost, ud.Role switch
            {
                UnitRole.Settler => "Основывает новый город (город теряет 1 жителя)",
                UnitRole.Worker => "Строит фермы, шахты, посёлки и дороги",
                _ => $"Сила {ud.Str}, ходы {ud.Moves}" + (ud.CityDefPct > 0 ? $", +{ud.CityDefPct}% при обороне города" : ""),
            });
        Option(units, Rules.Wealth, "Богатство (производство → золото)", 0, "Город не строит ничего, а приносит золото");
        foreach (var bd in Rules.Buildings.Where(x => x.Id != "palace" && W.Knows(x.Tech) && !d.Buildings.Contains(x.Id) && !d.Queue.Contains("b:" + x.Id)))
            Option(builds, "b:" + bd.Id, bd.Name, bd.Cost, bd.Describe());
    }

    private static string Unlocks(TechDef t)
    {
        var parts = new List<string>();
        parts.AddRange(Rules.Units.Where(u => u.Tech == t.Id).Select(u => u.Name));
        parts.AddRange(Rules.Buildings.Where(b => b.Tech == t.Id).Select(b => b.Name));
        foreach (var k in new[] { WorkKind.Farm, WorkKind.Mine, WorkKind.Cottage, WorkKind.Road })
            if (Rules.WorkTech(k) == t.Id) parts.Add(Rules.WorkName(k).ToLower() + " (рабочие)");
        if (t.Branch >= 0) parts.Add(Rules.FutureBranchEffects[t.Branch]);
        return parts.Count == 0 ? "путь к следующим технологиям" : string.Join(", ", parts);
    }

    private void BuildScience()
    {
        var me = W.Me;
        _windowTitle.Text = "Советник по науке";
        var cur = Rules.Tech(me.Research);
        Add(Ui.Lbl(cur == null ? "Исследование не выбрано — очки науки копятся впустую." : $"Сейчас изучается: {cur.Name}", 15, cur == null ? Ui.Bad : Ui.Science));
        Add(Ui.Lbl($"Наука за ход: +{me.SciRate}" + (me.EchoBonus > 0 ? $" (из них «научное эхо» от контактов: +{me.EchoBonus})" : "") +
                   $" · изучено технологий: {me.Known.Length + me.Future.Sum()}", 14));
        if (me.Future.Any(f => f > 0))
            Add(Ui.Lbl("Технологии будущего: " + string.Join(", ", me.Future.Select((lv, b) => $"{Rules.FutureBranchNames[b]} {lv}")), 13, Ui.Dim));
        Heading("Доступно для изучения (дерево бесконечно)");
        foreach (var a in me.Available.OrderBy(a => a.Cost))
        {
            var t = Rules.Tech(a.Id);
            if (t == null) continue;
            string id = a.Id;
            int left = a.Cost - (a.Id == me.Research ? me.ResearchProgress : Math.Min(me.ResearchProgress, a.Cost));
            var b = Ui.Btn($"{(a.Id == me.Research ? "▶ " : "")}{t.Name} [{Rules.EraName(t.Era)}] — {a.Cost} ({Turns(left, me.SciRate)})",
                () => Net.SendCommand(CmdType.SetResearch, s: id));
            b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(430, 0);
            var info = Ui.V(0, Ui.Lbl("Открывает: " + Unlocks(t), 12, Ui.Dim));
            if (a.Cost < a.BaseCost)
                info.AddChild(Ui.Lbl($"Эффект низкой базы: −{100 - a.Cost * 100 / a.BaseCost}% (обычная цена {a.BaseCost})", 12, Ui.Good));
            Add(Ui.H(12, b, info));
        }
    }

    private void BuildEconomy()
    {
        var me = W.Me;
        _windowTitle.Text = "Советник по экономике";
        Add(Ui.H(10,
            Ui.Btn("−10%", () => Net.SendCommand(CmdType.SetSlider, Math.Max(0, me.Slider - 10))),
            Ui.Lbl($"Наука {me.Slider}%  /  Золото {100 - me.Slider}%", 16),
            Ui.Btn("+10%", () => Net.SendCommand(CmdType.SetSlider, Math.Min(100, me.Slider + 10)))));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 28);
        void Line(string name, string value, Color? col = null) { grid.AddChild(Ui.Lbl(name, 14, Ui.Dim)); grid.AddChild(Ui.Lbl(value, 14, col)); }
        Line("Доход городов", $"+{me.Income}", Ui.Good);
        Line("Содержание юнитов", Cost(me.UnitUpkeep));
        Line("Содержание зданий", Cost(me.BuildingUpkeep));
        Line("Содержание городов (бюрократия)", Cost(me.CityMaint));
        Line("Содержание дорог", Cost(me.RoadUpkeep));
        Line("«Логистический надрыв» (штраф лидера)", Cost(me.StrainCost), me.StrainCost > 0 ? Ui.Bad : null);
        Line("Итого за ход", Ui.Signed(me.GoldRate), me.GoldRate < 0 ? Ui.Bad : Ui.Gold);
        Line("В казне", me.Gold.ToString(), Ui.Gold);
        Add(grid);

        Heading("Анти-Сноуболл: ваше положение в мире");
        Add(Ui.Lbl($"Индекс технологического разрыва (ITR): {me.Itr:+0.00;-0.00}", 15));
        if (me.Pressure > 0.005f)
        {
            Add(Ui.Lbl($"Вы оторвались от остального мира. Давление лидера: {me.Pressure * 100:0}%", 14, Ui.Bad));
            Add(Ui.Lbl($"• Информационный вакуум: наука ×{me.SciMult:0.00}, культура ×{me.CulMult:0.00}", 14));
            Add(Ui.Lbl($"• Логистический надрыв: содержание ×{me.UpkeepMult:0.00}", 14));
            Add(Ui.Lbl("• Социальное брожение: в городах, особенно далёких от столицы, растёт недовольство — возможны протесты и сепаратизм", 14));
        }
        else if (me.Lag > 0.005f)
        {
            Add(Ui.Lbl($"Вы отстаёте от мира — действуют бонусы догоняющего ({me.Lag * 100:0}%).", 14, Ui.Good));
            Add(Ui.Lbl("• Эффект низкой базы: технологии, уже открытые другими, стоят дешевле (см. окно науки)", 14));
            Add(Ui.Lbl($"• Научное эхо от мирных контактов с развитыми державами: +{me.EchoBonus} науки за ход (больше при открытых границах)", 14));
        }
        else Add(Ui.Lbl("Вы идёте вровень с миром: ни штрафов лидера, ни бонусов догоняющего.", 14, Ui.Dim));
        if (me.Sprawl > 0.01f)
            Add(Ui.Lbl($"• Раздутая империя: городов заметно больше, чем у соседей — содержание дополнительно растёт (индекс {me.Sprawl:0.00})", 14, Ui.Gold));

        Heading("Города");
        var cities = new GridContainer { Columns = 7 };
        cities.AddThemeConstantOverride("h_separation", 20);
        foreach (string h in new[] { "Город", "Нас.", "Пища", "Произв.", "Наука", "Золото", "Строит" }) cities.AddChild(Ui.Lbl(h, 13, Ui.Dim));
        foreach (var c in W.Cities.Values.Where(c => c.Owner == W.MyId && c.Detail != null).OrderBy(c => c.Id))
        {
            int id = c.Id;
            var d = c.Detail!;
            cities.AddChild(Ui.Btn(c.Name, () => OpenCity(id), size: 13));
            cities.AddChild(Ui.Lbl(c.Pop.ToString(), 13));
            cities.AddChild(Ui.Lbl(Ui.Signed(d.FoodRate), 13, d.FoodRate < 0 ? Ui.Bad : null));
            cities.AddChild(Ui.Lbl($"+{d.ProdRate}", 13));
            cities.AddChild(Ui.Lbl($"+{d.Science}", 13, Ui.Science));
            cities.AddChild(Ui.Lbl($"+{d.Gold}", 13, Ui.Gold));
            cities.AddChild(Ui.Lbl(d.DisorderTurns > 0 ? "беспорядки" : d.Queue.Length > 0 ? Rules.ItemName(d.Queue[0]) : "— простаивает —", 13,
                d.DisorderTurns > 0 || d.Queue.Length == 0 ? Ui.Bad : null));
        }
        Add(cities);
    }

    private void BuildDiplomacy()
    {
        _windowTitle.Text = "Дипломатия";
        var me = W.Me;
        foreach (var pr in me.Proposals.Where(p => p.To == W.MyId))
        {
            int id = pr.Id;
            string what = pr.Kind == (byte)ProposalKind.Peace ? "заключить мир" : "открыть границы";
            Add(Ui.H(10, Ui.Lbl($"«{W.PlayerName(pr.From)}» предлагает {what}", 15, Ui.Gold),
                Ui.Btn("Принять", () => Net.SendCommand(CmdType.Respond, id, 1)),
                Ui.Btn("Отклонить", () => Net.SendCommand(CmdType.Respond, id, 0))));
        }
        foreach (var pr in me.Proposals.Where(p => p.From == W.MyId))
            Add(Ui.Lbl($"Ваше предложение «{W.PlayerName(pr.To)}» ({(pr.Kind == (byte)ProposalKind.Peace ? "мир" : "открытые границы")}) ожидает ответа", 13, Ui.Dim));

        Add(Ui.Lbl("Мирный контакт с более развитой державой даёт «научное эхо»; открытые границы усиливают его и разрешают проход.", 12, Ui.Dim));
        foreach (var p in W.Players.Values.Where(p => p.Id != W.MyId && !p.Neutral).OrderBy(p => p.Id))
        {
            var name = Ui.Lbl(p.Met ? p.Name : "Неизвестная цивилизация", 16, p.Met ? Ui.Col(p.Color) : Ui.Dim);
            name.CustomMinimumSize = new Vector2(190, 0);
            if (!p.Alive) { Add(Ui.H(10, name, Ui.Lbl("уничтожена", 14, Ui.Dim))); continue; }
            if (!p.Met) { Add(Ui.H(10, name, Ui.Lbl("контакт не установлен — исследуйте карту", 13, Ui.Dim))); continue; }
            string tags = (p.AI ? "ИИ" : p.Connected ? "игрок" : "игрок, не в сети") + $" · счёт {p.Score} · {Rules.EraName(p.Era)} · городов {p.Cities} · технологий {p.Techs}";
            var status = Ui.Lbl(p.AtWar ? "ВОЙНА" : p.OpenBorders ? "мир, открытые границы" : "мир", 14, p.AtWar ? Ui.Bad : Ui.Good);
            status.CustomMinimumSize = new Vector2(170, 0);
            var row = Ui.H(10, name, status);
            int pid = p.Id;
            if (p.AtWar) row.AddChild(Ui.Btn("Предложить мир", () => Net.SendCommand(CmdType.Propose, pid, (int)ProposalKind.Peace)));
            else
            {
                row.AddChild(p.OpenBorders
                    ? Ui.Btn("Закрыть границы", () => Net.SendCommand(CmdType.CancelBorders, pid))
                    : Ui.Btn("Открыть границы", () => Net.SendCommand(CmdType.Propose, pid, (int)ProposalKind.OpenBorders)));
                row.AddChild(Ui.Btn("Объявить войну", () => Net.SendCommand(CmdType.DeclareWar, pid)));
            }
            Add(Ui.V(0, row, Ui.Lbl(tags, 12, Ui.Dim)));
        }
        var free = W.Players.Values.FirstOrDefault(p => p.Neutral);
        if (free != null && W.Cities.Values.Any(c => c.Owner == free.Id))
            Add(Ui.Lbl("Вольные города — отделившиеся провинции сверхдержав. Они ни с кем не договариваются; их можно захватить.", 12, Ui.Dim));
    }

    private void BuildDemographics()
    {
        _windowTitle.Text = "Демография";
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 30);
        foreach (string h in new[] { "Показатель", "Вы", "Место", "Лучший", "Средний", "Худший" }) grid.AddChild(Ui.Lbl(h, 14, Ui.Dim));
        foreach (var d in W.Me.Demo)
        {
            grid.AddChild(Ui.Lbl(d.Key, 14));
            grid.AddChild(Ui.Lbl(d.Mine.ToString(), 14, Ui.Gold));
            grid.AddChild(Ui.Lbl(d.Rank.ToString(), 14, d.Rank == 1 ? Ui.Good : null));
            grid.AddChild(Ui.Lbl(d.Best.ToString(), 14));
            grid.AddChild(Ui.Lbl(d.Avg.ToString(), 14));
            grid.AddChild(Ui.Lbl(d.Worst.ToString(), 14));
        }
        Add(grid);
        Heading("Известные цивилизации");
        foreach (var p in W.Players.Values.Where(p => !p.Neutral && p.Met).OrderByDescending(p => p.Score))
            Add(Ui.Lbl($"{p.Name}{(p.Id == W.MyId ? " (вы)" : "")} — счёт {p.Score}, {Rules.EraName(p.Era)}, городов {p.Cities}{(p.Alive ? "" : " — уничтожена")}", 14, Ui.Col(p.Color)));
    }
}

/// <summary>Overview map rendered into a tiny texture (2 px per tile); click or drag to move the camera.</summary>
public partial class Minimap : Control
{
    private readonly GameScreen _screen;
    private readonly Image _img;
    private readonly ImageTexture _tex;
    private bool _dirty = true, _drag;

    public Minimap(GameScreen screen)
    {
        _screen = screen;
        var g = screen.W.Grid;
        _img = Image.CreateEmpty(g.W * 2 + 1, g.H, false, Image.Format.Rgb8);
        _tex = ImageTexture.CreateFromImage(_img);
        TextureFilter = TextureFilterEnum.Nearest;
        float w = 250f;
        CustomMinimumSize = new Vector2(w, Mathf.Clamp(w * g.H * 1.5f / (g.W * 1.732f), 110, 220));
        MouseFilter = MouseFilterEnum.Stop;
    }

    public void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty) return;
        _dirty = false;
        var w = _screen.W;
        var unknown = new Color(0.03f, 0.04f, 0.07f);
        for (int i = 0; i < w.Tiles.Length; i++)
        {
            int x = i % w.Grid.W, y = i / w.Grid.W;
            Color c = unknown;
            if (w.Vis[i] > 0)
            {
                var t = w.Tiles[i];
                c = MapView.TerrainColor(t);
                if (t.Owner >= 0) c = c.Lerp(_screen.Map.OwnerColor(t.Owner), 0.6f);
                if (t.CityId >= 0) c = Colors.White;
                if (w.Vis[i] == 1) c = c.Darkened(0.4f);
            }
            int px = x * 2 + (y & 1);
            _img.SetPixel(px, y, c);
            _img.SetPixel(px + 1, y, c);
        }
        foreach (var u in w.Units.Values)
            if (u.Owner == w.MyId) { int px = u.Tile % w.Grid.W * 2 + (u.Tile / w.Grid.W & 1); _img.SetPixel(px, u.Tile / w.Grid.W, Colors.Yellow); }
        _tex.Update(_img);
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawTextureRect(_tex, new Rect2(Vector2.Zero, Size), false);
        var map = _screen.Map;
        var total = map.MapPixelSize;
        var view = GetViewportRect().Size / map.Zoom;
        var topLeft = (map.Cam - view / 2) / total * Size;
        var rect = new Rect2(topLeft, view / total * Size).Intersection(new Rect2(Vector2.Zero, Size));
        if (rect.Size.X > 1 && rect.Size.Y > 1) DrawRect(rect, Colors.White, false, 1.5f);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { _drag = mb.Pressed; if (mb.Pressed) Jump(mb.Position); AcceptEvent(); }
        else if (e is InputEventMouseMotion mm && _drag) { Jump(mm.Position); AcceptEvent(); }
    }

    private void Jump(Vector2 local)
    {
        var map = _screen.Map;
        map.Cam = local / Size * map.MapPixelSize;
        map.ApplyCamera();
    }
}
