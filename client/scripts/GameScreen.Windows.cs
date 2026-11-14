using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

public partial class GameScreen
{
    // ----------------------------------------------------------- windows
    private float _wrapW = 760;

    public void OpenCity(int id)
    {
        _cityId = id;
        Toggle(Win.City, true);
        // keep the city and its fields in the part of the map the panel leaves free
        if (W.Cities.TryGetValue(id, out var c))
        {
            float free = Size.X - CityW - 40;
            if (!Map.Project(c.Tile, 0, out var at) || at.X > free - 220 || at.X < 240 || at.Y < 160 || at.Y > Size.Y - 200)
                Map.CenterOn(c.Tile, new Vector2(Size.X / 2 - (free + 226) / 2, 0));
        }
    }

    private void CycleCity(int dir)
    {
        var mine = W.Cities.Values.Where(c => c.Owner == W.MyId).OrderBy(c => c.Id).ToList();
        if (mine.Count == 0) return;
        int i = Math.Max(0, mine.FindIndex(c => c.Id == _cityId));
        var next = mine[(i + dir + mine.Count) % mine.Count];
        Map.CenterOn(next.Tile);
        OpenCity(next.Id);
    }

    public void Toggle(Win w, bool forceOpen = false)
    {
        _win = w == _win && !forceOpen ? Win.None : w;
        _windowScroll.ScrollVertical = 0;
        _windowScroll.ScrollHorizontal = 0;
        _cityScroll.ScrollVertical = 0;
        _scrollToFrontier = true;
        RebuildWindow();
        Refresh();
    }

    private void RebuildWindow()
    {
        bool city = _win == Win.City, advisor = _win != Win.None && !city;
        _window.Visible = advisor;
        _backdrop.Visible = advisor;
        _cityDock.Visible = city;
        if (_aiming == null) Map.Highlight = Array.Empty<int>(); // keep the spell's range markers while a target is being picked
        Map.Invalidate();
        if (_win == Win.None) return;
        if (city)
        {
            int at = _cityScroll.ScrollVertical;
            _body = _cityBody;
            _wrapW = CityW - 14;
            Ui.Clear(_cityBody);
            BuildCity();
            _cityScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, at);
            return;
        }
        _body = _windowBody;
        _wrapW = _bodyW - 28;
        Ui.Clear(_windowTabs);
        foreach (var a in Advisors)
        {
            var win = a.win;
            _windowTabs.AddChild(Ui.Btn(a.title, () => Toggle(win, true), $"{a.tip} ({a.key})", 13, a.win == _win ? "TabOn" : "Tab"));
            if (a.win == _win) _windowIcon.Set(a.icon, a.color);
        }
        int scroll = _windowScroll.ScrollVertical, scrollX = _windowScroll.ScrollHorizontal;
        Ui.Clear(_windowBody);
        switch (_win)
        {
            case Win.Science: BuildScience(); break;
            case Win.Economy: BuildEconomy(); break;
            case Win.Diplomacy: BuildDiplomacy(); break;
            case Win.Demographics: BuildDemographics(); break;
            case Win.Civics: BuildCivics(); break;
            case Win.Religion: BuildReligion(); break;
            case Win.Branches: BuildBranches(); break;
            case Win.Help: BuildHelp(); break;
        }
        if (!_scrollToFrontier || (_win != Win.Science && _win != Win.Civics)) _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, scrollX);
        _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
    }

    private void Add(Control c)
    {
        // long explanations wrap instead of stretching the window sideways
        if (c is Label { Text.Length: > 60 } l && l.AutowrapMode == TextServer.AutowrapMode.Off)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(Math.Min(_wrapW, 900), 0);
            l.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        }
        _body.AddChild(c);
    }
    private void Heading(string text) => Add(Ui.Section(text));
    private void Note(string text) => Add(Ui.Wrap(text, Math.Min(_wrapW, 900), 12, Ui.Dim));

    private void BuildCity()
    {
        if (!W.Cities.TryGetValue(_cityId, out var c) || c.Detail == null) { _win = Win.None; _cityDock.Visible = false; return; }
        var d = c.Detail;
        var me = W.Me;
        Map.Highlight = d.Worked;
        _cityTitle.Text = (c.Capital ? "★ " : "") + c.Name;
        float w = _wrapW;
        if (d.DisorderTurns > 0)
            Add(Ui.Card(Ui.Wrap($"Беспорядки: производство остановлено ещё на {d.DisorderTurns} х.", w - 20, 13, Ui.Col(0xFFB3AC)), 8, Ui.Col(0x3A141A), Ui.Bad));

        // ---- what the city yields every turn
        Add(Ui.Card(Ui.Flow(16,
            new Chip(Ico.Food, Ui.Food, "Пища за ход: излишек идёт на рост населения").Set(Ui.Signed(d.FoodRate)),
            new Chip(Ico.Prod, Ui.Prod, "Производство за ход: строит юнитов и здания").Set($"+{d.ProdRate}"),
            new Chip(Ico.Science, Ui.Science, $"Наука за ход. Коммерция города: {d.Commerce}, делится ползунком в «Экономике»").Set($"+{d.Science}"),
            new Chip(Ico.Gold, Ui.Gold, $"Золото за ход. Содержание города: −{d.Maint}").Set($"+{d.Gold}", d.Maint > 0 ? $"−{d.Maint}" : ""),
            new Chip(Ico.Culture, Ui.Culture, "Культура за ход: расширяет границы и двигает институты").Set($"+{d.CultureRate}")), 9));

        // ---- growth, borders, mood
        string growth = d.FoodRate > 0 ? $"рост через {Turns(d.FoodNeed - d.Food, d.FoodRate)}" : d.FoodRate < 0 ? "голод!" : "застой";
        var grow = Ui.V(4,
            Ui.H(6, new Icon(Ico.Pop, Ui.Food, 14), Ui.Lbl($"Население {c.Pop}", 13, null, true), Ui.Spacer(), Ui.Lbl($"{d.Food}/{d.FoodNeed} · {growth}", 12, d.FoodRate < 0 ? Ui.Bad : Ui.Dim)),
            new Bar(d.FoodRate < 0 ? Ui.Bad : Ui.Food, 6).Set(d.Food / (float)Math.Max(1, d.FoodNeed), Math.Max(0, d.FoodRate) / (float)Math.Max(1, d.FoodNeed)));
        int from = Rules.CultureForRadius(c.Radius), to = Rules.CultureForRadius(c.Radius + 1);
        bool maxed = c.Radius >= 4;
        var borders = Ui.V(4,
            Ui.H(6, new Icon(Ico.Culture, Ui.Culture, 14), Ui.Lbl($"Границы: радиус {c.Radius}", 13, null, true), Ui.Spacer(),
                Ui.Lbl(maxed ? "наибольший" : $"{d.Culture}/{to} · {Turns(to - d.Culture, d.CultureRate)}", 12, Ui.Dim)),
            new Bar(Ui.Culture, 6).Set(maxed ? 1 : (d.Culture - from) / (float)Math.Max(1, to - from), maxed ? 0 : d.CultureRate / (float)Math.Max(1, to - from)));
        var unrestColor = d.Unrest >= 60 ? Ui.Bad : d.Unrest >= 30 ? Ui.Gold : Ui.Good;
        var mood = Ui.V(4,
            Ui.H(6, new Icon(Ico.Happy, d.Unhappy > 0 ? Ui.Bad : Ui.Gold, 14), Ui.Lbl("Довольство", 13, null, true), Ui.Spacer(),
                Ui.Lbl($"предел {d.Happy} · недовольных {d.Unhappy}", 12, d.Unhappy > 0 ? Ui.Bad : Ui.Dim)),
            Ui.H(6, new Icon(Ico.Unrest, unrestColor, 14), Ui.Lbl("Брожение", 13, null, true), new Bar(unrestColor, 6).Set(d.Unrest / 100f),
                Ui.Lbl($"{d.Unrest}/100" + (d.Unrest >= 60 ? " — близко к протестам" : ""), 12, d.Unrest >= 60 ? Ui.Bad : Ui.Dim)));
        mood.TooltipText = "Жители сверх предела довольства не работают. Брожение растёт у лидеров мира, в дальних городах, от усталости от войны и религиозной розни; при 100 начинаются протесты";
        Add(Ui.Card(Ui.V(9, grow, borders, mood), 9));

        // ---- faith and blessings
        var creed = me.Religions.FirstOrDefault(r => r.Id == c.Religion);
        string pressure = string.Join(", ", Enumerable.Range(0, d.Pressure.Length / 2)
            .Select(i => $"{me.Religions.FirstOrDefault(r => r.Id == d.Pressure[i * 2])?.Name ?? "?"} {d.Pressure[i * 2 + 1]}"));
        var facts = Ui.V(3);
        facts.AddChild(Ui.Wrap(creed == null ? "Религия: нет" + (pressure.Length > 0 ? $" (давление: {pressure})" : "")
            : $"Религия: {creed.Name} — {Rules.Beliefs[creed.Belief].Name}: {Rules.Beliefs[creed.Belief].Effect.Describe()}", w - 4, 12, creed == null ? Ui.Dim : Ui.Faith));
        if (me.StateReligion != 0 && c.Religion != 0)
        {
            bool harmony = c.Religion == me.StateReligion;
            facts.AddChild(Ui.Wrap(harmony ? "Одна вера со столицей: −1 брожения за ход" : "Религиозная рознь со столицей: +2 брожения за ход", w - 4, 12, harmony ? Ui.Good : Ui.Bad));
        }
        if (d.Buffs.Length > 0) facts.AddChild(Ui.Wrap("Благословения: " + string.Join(", ", d.Buffs), w - 4, 12, Ui.Good));
        Add(facts);

        Heading($"Постройки · {d.Buildings.Length}");
        var built = Ui.Flow(5);
        foreach (string b in d.Buildings)
            built.AddChild(Ui.Pill(Rules.Building(b)?.Name ?? b, Ui.Col(0xC8D0E0), 12, Rules.Building(b)?.Describe() ?? ""));
        Add(built);

        // ---- production queue
        Heading("Производство");
        if (d.Queue.Length == 0) Add(Ui.Card(Ui.Lbl("Город простаивает — выберите, что строить", 13, Ui.Col(0xFFB3AC)), 8, Ui.Col(0x3A141A, 0.8f), Ui.Col(0x8A3B40)));
        for (int i = 0; i < d.Queue.Length; i++)
        {
            string item = d.Queue[i];
            int cost = Rules.ItemCost(item), idx = i;
            var remove = Ui.Btn("✕", () => Net.SendCommand(CmdType.RemoveQueue, c.Id, idx), "Убрать из очереди", 12, "Flat");
            if (i > 0 || item == Rules.Wealth)
            {
                Add(Ui.H(6, Ui.Lbl($"{i + 1}.", 12, Ui.Dim), Ui.Lbl(item == Rules.Wealth ? "Богатство: производство → золото" : Rules.ItemName(item), 13),
                    Ui.Spacer(), Ui.Lbl(item == Rules.Wealth ? "" : $"{cost}", 12, Ui.Dim), remove));
                continue;
            }
            var now = Ui.V(5,
                Ui.H(6, new Icon(Ico.Prod, Ui.Prod, 15), Ui.Lbl(Rules.ItemName(item), 14, null, true), Ui.Spacer(),
                    Ui.Lbl($"{Math.Min(d.Prod, cost)}/{cost} · {Turns(cost - d.Prod, d.ProdRate)}", 12, Ui.Dim), remove),
                new Bar(Ui.Prod, 7).Set(d.Prod / (float)Math.Max(1, cost), d.ProdRate / (float)Math.Max(1, cost)));
            int price = Rules.BuyPrice(cost, d.Prod);
            if (price > 0)
            {
                var buyRow = Ui.H(6);
                var buy = Ui.Btn($"Купить · {price} зол.", () => Net.SendCommand(CmdType.Buy, c.Id), "Достроить немедленно за золото", 13);
                buy.Disabled = me.Gold < price;
                buyRow.AddChild(buy);
                bool peaceful = item.StartsWith("b:") || Rules.Units[Rules.UnitIndex(item[2..])].Role != UnitRole.Military;
                if (peaceful)
                {
                    int fp = Rules.FaithPrice(cost, d.Prod);
                    var buyF = Ui.Btn($"За веру · {fp}", () => Net.SendCommand(CmdType.BuyFaith, c.Id), "Вера оплачивает здания и мирных юнитов", 13);
                    buyF.Disabled = me.Faith < fp;
                    buyRow.AddChild(buyF);
                }
                now.AddChild(buyRow);
            }
            Add(Ui.Card(now, 9));
        }

        // ---- what can be built
        Heading("Построить");
        var append = new CheckBox { Text = "В конец очереди (иначе заменяет текущий проект)", ButtonPressed = _appendQueue, FocusMode = FocusModeEnum.None };
        append.AddThemeFontSizeOverride("font_size", 12);
        append.Toggled += on => _appendQueue = on;
        Add(append);
        float colW = (w - 8) / 2;
        var units = Ui.V(3, Ui.Lbl("ЮНИТЫ", 11, Ui.Dim, true));
        var builds = Ui.V(3, Ui.Lbl("ЗДАНИЯ", 11, Ui.Dim, true));
        units.CustomMinimumSize = builds.CustomMinimumSize = new Vector2(colW, 0);
        Add(Ui.H(8, units, builds));
        void Option(VBoxContainer col, string item, string name, int cost, string tip, Ico icon, Color color)
        {
            var b = Ui.RowBtn(name, cost > 0 ? $"{cost} · {Turns(cost, d.ProdRate)}" : "", () => Net.SendCommand(CmdType.SetProduction, c.Id, _appendQueue ? 1 : 0, 0, item),
                name + (cost > 0 ? $" — {cost} производства" : "") + "\n" + tip, icon, color, 12);
            col.AddChild(b);
        }
        bool coastal = CoastalCity(c);
        var buildable = Rules.Units.Where(x => x.Role != UnitRole.Missionary && !x.X.SummonOnly && W.Knows(x.Tech) && HasRes(x.Needs) && (!x.Sea || coastal)).ToList();
        // settlers, workers, scouts and branch specialists are always offered; of the regular troops only the best few,
        // otherwise the list grows without end and obsolete units crowd it
        var always = buildable.Where(x => x.Role != UnitRole.Military || x.X != Rules.ModUnit.None).OrderBy(x => x.Sea).ThenBy(x => x.Cost);
        var land = buildable.Where(x => !x.Sea && x.Role == UnitRole.Military && x.X == Rules.ModUnit.None).OrderByDescending(x => x.Str).ThenByDescending(x => x.Cost).Take(5).Reverse();
        var navy = buildable.Where(x => x.Sea && x.Role == UnitRole.Military).OrderByDescending(x => x.Str).Take(3).Reverse();
        foreach (var ud in always.Concat(land).Concat(navy))
            Option(units, "u:" + ud.Id, ud.Name, ud.Cost, ud.Role switch
            {
                UnitRole.Settler => "Основывает новый город (город теряет 1 жителя)",
                UnitRole.Worker => ud.Sea ? "Строит рыбацкие суда и нефтяные платформы на морских ресурсах" : "Строит улучшения на суше и дороги",
                _ => $"Сила {ud.Str}, ходы {ud.Moves}" + (ud.CityDefPct > 0 ? $", +{ud.CityDefPct}% при обороне города" : "")
                     + (ud.Needs != Resource.None ? $", требует ресурс: {Rules.Res(ud.Needs).Name}" : "")
                     + (ud.X.Abilities().Length > 0 ? "\n" + ud.X.Abilities() : ""),
            }, UnitIcon(ud), ud.X != Rules.ModUnit.None ? Ui.Power : ud.Role == UnitRole.Military ? Ui.Col(0xC8D0E0) : Ui.Good);
        Option(units, Rules.Wealth, "Богатство", 0, "Город не строит ничего, а превращает производство в золото", Ico.Gold, Ui.Gold);
        var missing = Rules.Units.Where(x => x.Role != UnitRole.Missionary && W.Knows(x.Tech) && !HasRes(x.Needs) && (!x.Sea || coastal)).ToList();
        if (missing.Count > 0)
            units.AddChild(Ui.Wrap("Нет ресурса: " + string.Join(", ", missing.Select(x => $"{x.Name} ({Rules.Res(x.Needs).Name})")), colW - 4, 11, Ui.Dim));
        foreach (var bd in Rules.Buildings.Where(x => x.Id != "palace" && W.Knows(x.Tech) && (!x.Coastal || coastal) && !d.Buildings.Contains(x.Id) && !d.Queue.Contains("b:" + x.Id)))
            Option(builds, "b:" + bd.Id, bd.Name, bd.Cost, bd.Describe(), Ico.City, bd.X != Rules.ModBuilding.None ? Ui.Power : Ui.Col(0xC8D0E0));
        Note("Правый клик по свободной клетке у границы покупает её для города, пока открыта эта панель.");
    }

    internal static string Unlocks(TechDef t)
    {
        var parts = new List<string>();
        parts.AddRange(Rules.Units.Where(u => u.Tech == t.Id).Select(u => u.Name));
        parts.AddRange(Rules.Buildings.Where(b => b.Tech == t.Id).Select(b => b.Name));
        foreach (var k in Rules.AllWork)
            if (Rules.WorkTech(k) == t.Id) parts.Add(Rules.WorkName(k).ToLower() + " (улучшение)");
        parts.AddRange(Rules.Resources.Where(r => r.Reveal == t.Id).Select(r => "ресурс " + r.Name.ToLower()));
        if (t.Id == "sailing") parts.Add("посадка на суда у берега");
        if (t.Id == "astronomy") parts.Add("плавание через океан");
        if (t.Branch >= 0) parts.Add(Rules.FutureBranchEffects[t.Branch]);
        return parts.Count == 0 ? "путь к следующим технологиям" : string.Join(", ", parts);
    }

    private void BuildScience()
    {
        var me = W.Me;
        _windowTitle.Text = "Наука и дерево технологий";
        var cur = Rules.Tech(me.Research);
        Add(Ui.Lbl(cur == null ? "Исследование не выбрано — очки науки копятся впустую." : $"Сейчас изучается: {cur.Name}", 15, cur == null ? Ui.Bad : Ui.Science));
        Add(Ui.Lbl($"Наука за ход: +{me.SciRate}" + (me.EchoBonus > 0 ? $" (из них «научное эхо» от контактов: +{me.EchoBonus})" : "") +
                   $" · изучено технологий: {me.Known.Length + me.Future.Sum()}", 14));
        if (me.Future.Any(f => f > 0))
            Add(Ui.Lbl("Технологии будущего: " + string.Join(", ", me.Future.Select((lv, b) => $"{Rules.FutureBranchNames[b]} {lv}")), 13, Ui.Dim));
        BuildTechTree();
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
            b.ThemeTypeVariation = a.Id == me.Research ? "TabOn" : "Flat";
            b.CustomMinimumSize = new Vector2(430, 0);
            var info = Ui.V(0, Ui.Lbl("Открывает: " + Unlocks(t), 12, Ui.Dim));
            if (a.Cost < a.BaseCost)
                info.AddChild(Ui.Lbl($"Эффект низкой базы: −{100 - a.Cost * 100 / a.BaseCost}% (обычная цена {a.BaseCost})", 12, Ui.Good));
            Add(Ui.H(12, b, info));
        }
    }

    private void BuildTechTree()
    {
        Heading("Дерево технологий");
        Add(Ui.Lbl("Слева направо — от ранних к поздним; линии ведут от нужных предшественников.\nЗелёные изучены, светлые доступны (нажмите), серые закрыты. ▼ — скидка догоняющего. Прокрутка — полосой внизу или Shift+колесо.", 12, Ui.Dim));
        var graph = TechGraph.ForScience(W.Me, id => Net.SendCommand(CmdType.SetResearch, s: id));
        Add(graph);
        if (_scrollToFrontier)
        {
            // on opening, jump to the part of the tree the player is working on
            _scrollToFrontier = false;
            _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, (int)graph.FrontierX);
        }
    }

    private void BuildCivics()
    {
        var me = W.Me;
        _windowTitle.Text = "Социальные институты";
        var cur = Rules.Civic(me.Civic);
        Add(Ui.Lbl(cur == null ? "Институт не выбран — культура копится впустую." : $"Сейчас принимается: {cur.Name} ({me.CivicProgress}/{cur.Cost})",
            15, cur == null ? Ui.Bad : Ui.Gold));
        Add(Ui.Lbl($"Культура империи: +{me.CultureRate} за ход (её дают города: монументы, храмы, театры). Принято институтов: {me.Civics.Length + me.CivicLevel}.", 13));
        var total = Rules.CivicBonus(me.Civics, me.CivicLevel).Describe();
        Add(Ui.Lbl("Действующие бонусы: " + (total.Length > 0 ? total : "пока нет"), 13, Ui.Good));
        Add(Ui.Lbl($"Каждый институт даёт постоянный бонус всей империи. Нажмите на доступный узел, чтобы принять его. Наука и институты подпитывают друг друга: каждая изученная технология добавляет {Rules.Inspiration * 100:0}% своей цены к текущему институту, и наоборот.", 12, Ui.Dim));
        var graph = TechGraph.ForCivics(me, id => Net.SendCommand(CmdType.SetCivic, s: id));
        graph.Accent = Ui.Culture;
        Add(graph);
        if (_scrollToFrontier)
        {
            _scrollToFrontier = false;
            _windowScroll.SetDeferred(ScrollContainer.PropertyName.ScrollHorizontal, (int)graph.FrontierX);
        }
        Heading("Доступно сейчас");
        foreach (var c in Rules.AvailableCivics(me.Civics, me.CivicLevel).OrderBy(c => c.Cost))
        {
            string id = c.Id;
            int cost = me.CivicCosts.FirstOrDefault(x => x.Id == id)?.Cost ?? c.Cost;
            var b = Ui.Btn($"{(id == me.Civic ? "▶ " : "")}{c.Name} — {cost} ({Turns(cost - Math.Min(me.CivicProgress, cost), me.CultureRate)})",
                () => Net.SendCommand(CmdType.SetCivic, s: id));
            b.Alignment = HorizontalAlignment.Left;
            b.ThemeTypeVariation = id == me.Civic ? "TabOn" : "Flat";
            b.CustomMinimumSize = new Vector2(380, 0);
            var info = Ui.V(0, Ui.Lbl(c.Effect.Describe(), 12, Ui.Dim));
            if (cost < c.Cost) info.AddChild(Ui.Lbl($"Эффект низкой базы: −{100 - cost * 100 / c.Cost}% (обычная цена {c.Cost})", 12, Ui.Good));
            Add(Ui.H(12, b, info));
        }
    }

    private int _branchTab;

    private void BuildBranches()
    {
        var me = W.Me;
        _windowTitle.Text = "Ветки развития";
        if (Rules.Branches.Count == 0)
        {
            Add(Ui.Lbl("В этой игре нет особых веток. Хост может добавить их файлами в папке модов (кнопка «Папка модов» в главном меню).", 14, Ui.Dim));
            return;
        }
        _branchTab = Math.Clamp(_branchTab, 0, Rules.Branches.Count - 1);
        var tabs = Ui.Flow(6);
        for (int i = 0; i < Rules.Branches.Count; i++)
        {
            int idx = i;
            var b = Rules.Branches[i];
            int done = b.Nodes.Count(n => me.Nodes.Contains(n.Id));
            var tb = Ui.Btn($"{b.Name} · {done}/{b.Nodes.Length}{(BranchCanPick(i) ? " · выберите узел" : "")}",
                () => { _branchTab = idx; _windowScroll.ScrollHorizontal = 0; RebuildWindow(); }, b.Description, 14, i == _branchTab ? "TabOn" : null);
            if (i != _branchTab) tb.AddThemeColorOverride("font_color", PowerColor(i));
            tabs.AddChild(tb);
        }
        Add(tabs);
        var br = Rules.Branches[_branchTab];
        var color = PowerColor(_branchTab);
        float w = Math.Min(_wrapW, 900);
        int rate = _branchTab < me.BranchRate.Length ? me.BranchRate[_branchTab] : 0;
        int progress = _branchTab < me.BranchProgress.Length ? me.BranchProgress[_branchTab] : 0;
        var cur = Rules.Node(_branchTab < me.BranchCurrent.Length ? me.BranchCurrent[_branchTab] : "");
        int pool = _branchTab < me.Power.Length ? me.Power[_branchTab] : 0, poolRate = _branchTab < me.PowerRate.Length ? me.PowerRate[_branchTab] : 0;
        if (br.Description.Length > 0) Note(br.Description);

        var summary = Ui.V(7);
        summary.AddChild(Ui.Flow(22,
            new Chip(Ico.Power, color, $"{br.PowerName} — ресурс ветки для её действий; его дают узлы и здания ветки и ресурсы карты. Не больше {Rules.PowerCap}")
                .Set($"{br.PowerName}: {pool}/{Rules.PowerCap}", $"+{poolRate} за ход"),
            new Chip(Ico.Branch, color, "Очки развития ветки. Ветки идут параллельно с наукой и институтами и ничего у них не отнимают")
                .Set($"+{rate} очк. за ход", $"от: {br.Fuel()}")));
        if (cur != null)
        {
            int cost = me.NodeCosts.FirstOrDefault(c => c.Id == cur.Id)?.Cost ?? cur.Cost;
            summary.AddChild(Ui.H(8, Ui.Lbl($"Осваивается: {cur.Name}", 14, null, true), Ui.Spacer(), Ui.Lbl($"{progress}/{cost} · {Turns(cost - progress, rate)}", 12, Ui.Dim)));
            summary.AddChild(new Bar(color, 6).Set(progress / (float)Math.Max(1, cost), rate / (float)Math.Max(1, cost)));
        }
        else summary.AddChild(Ui.Lbl(BranchCanPick(_branchTab) ? $"Узел не выбран — очки копятся ({progress}). Нажмите на светлый узел ниже." : $"Доступных узлов сейчас нет — очки копятся ({progress}).",
            13, BranchCanPick(_branchTab) ? Ui.Col(0xFFB3AC) : Ui.Dim));
        var total = Rules.NodeBonus(br.Nodes.Where(n => me.Nodes.Contains(n.Id)).Select(n => n.Id)).Describe();
        if (total.Length > 0) summary.AddChild(Ui.Wrap("Уже даёт: " + total, w - 30, 13, Ui.Good));
        if (br.PowerResources.Length > 0)
        {
            var have = br.PowerResources.Select(Rules.ParseResource).Where(r => r != Resource.None).ToList();
            summary.AddChild(Ui.Wrap("Ресурсы карты, питающие ветку (+1 за каждый подключённый): " + string.Join(", ", have.Select(r => (HasRes(r) ? "✔ " : "") + Rules.Res(r).Name)), w - 30, 12, Ui.Dim));
        }
        var card = Ui.Card(summary, 10);
        card.CustomMinimumSize = new Vector2(w, 0);
        card.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        Add(card);

        var graph = TechGraph.ForBranch(me, _branchTab, id => Net.SendCommand(CmdType.SetNode, s: id));
        graph.Accent = color;
        Add(graph);
        Note("Серый узел с подписью «нужно: …» ждёт технологию, институт или узел другой ветки. Наведите курсор, чтобы увидеть все требования и бонус.");

        if (br.Actions.Length > 0)
        {
            Heading("Действия");
            Note("Действия империи запускаются отсюда. Заклинания юнитов (✦) творит сам юнит: выберите его на карте — кнопки появятся в его панели.");
            foreach (var a in br.Actions)
            {
                var act = a;
                int idx = Rules.Actions.ToList().FindIndex(x => x.Id == a.Id);
                int wait = idx >= 0 && idx < me.Cooldowns.Length ? me.Cooldowns[idx] : 0;
                bool open = W.Knows(a.Requires);
                bool byUnit = a.Caster.Length > 0;
                string casterName = byUnit ? Rules.Units.FirstOrDefault(x => x.Id == a.Caster)?.Name ?? a.Caster : "";
                var cast = Ui.Btn($"{(byUnit ? "✦ " : "")}{a.Name} · {a.Price(br.PowerName)}", () => BeginAction(act, 0), a.Description, 13);
                cast.CustomMinimumSize = new Vector2(300, 0);
                cast.Alignment = HorizontalAlignment.Left;
                cast.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                cast.Disabled = byUnit || !open || wait > 0 || pool < a.CostPower || me.Faith < a.CostFaith || me.Gold < a.CostGold;
                string state = !open ? $"нужно: {Rules.RequirementName(a.Requires)}" : byUnit ? $"творит юнит «{casterName}»"
                    : wait > 0 ? $"перезарядка: {wait} х." : a.Target == "City" ? "после нажатия выберите свой город" : $"повтор через {a.Cooldown} х.";
                var row = Ui.Card(Ui.H(12, cast, Ui.V(1, Ui.Wrap(a.Effect(), w - 350, 13), Ui.Lbl(state, 12, !open ? Ui.Dim : wait > 0 ? Ui.Gold : Ui.Good))), 7);
                row.CustomMinimumSize = new Vector2(w, 0);
                row.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
                Add(row);
            }
        }
        if (br.Units.Length + br.Buildings.Length > 0)
        {
            Heading("Юниты и здания ветки (строятся в городах)");
            foreach (var u in br.Units)
            {
                bool known = W.Knows(u.Requires);
                Add(Ui.H(8, new Icon(known ? Ico.Check : Ico.Close, known ? Ui.Good : new Color(Ui.Dim, 0.5f), 13),
                    Ui.Wrap($"Юнит «{u.Name}» — сила {u.Str}, ходы {u.Moves}" + (u.CityDefPct > 0 ? $", +{u.CityDefPct}% в обороне города" : "")
                            + $", цена {u.Cost}. Открывает: {Rules.RequirementName(u.Requires)}" + (u.Abilities().Length > 0 ? $". {u.Abilities()}" : ""), w - 30, 13, known ? Ui.Text : Ui.Dim)));
            }
            foreach (var m in br.Buildings)
            {
                bool known = W.Knows(m.Requires);
                Add(Ui.H(8, new Icon(known ? Ico.Check : Ico.Close, known ? Ui.Good : new Color(Ui.Dim, 0.5f), 13),
                    Ui.Wrap($"Здание «{m.Name}» — {Rules.Building(m.Id)?.Describe()}, цена {m.Cost}. Открывает: {Rules.RequirementName(m.Requires)}", w - 30, 13, known ? Ui.Text : Ui.Dim)));
            }
        }
    }

    private int _relName, _relBelief;

    private void BuildReligion()
    {
        var me = W.Me;
        _windowTitle.Text = "Религия";
        Add(Ui.Lbl($"Вера: {me.Faith} ({Ui.Signed(me.FaithRate)} за ход). Её дают дворец, святилища, храмы и соборы; институты и реформация умножают её.", 13));
        var mine = me.Religions.FirstOrDefault(r => r.Id == me.MyReligion);
        if (mine != null)
        {
            Add(Ui.Lbl($"Вы основали религию «{mine.Name}». Верование: {Rules.Beliefs[mine.Belief].Name} — {Rules.Beliefs[mine.Belief].Effect.Describe()}", 15, Ui.Gold));
            Add(Ui.Lbl($"Ей следуют городов: {mine.Cities}. Каждый такой город даёт вам +1 веры, чужой — ещё и +1 золота за ход.", 13));
        }
        else if (me.Religions.Length >= Rules.Beliefs.Length) Add(Ui.Lbl("Все верования уже заняты — основать новую религию нельзя.", 14, Ui.Dim));
        else
        {
            int price = Rules.ReligionPrice(me.Religions.Length);
            Heading($"Основать религию — {price} веры");
            var freeNames = Enumerable.Range(0, Rules.ReligionNames.Length).Where(i => me.Religions.All(r => r.Name != Rules.ReligionNames[i])).ToList();
            var freeBeliefs = Enumerable.Range(0, Rules.Beliefs.Length).Where(i => me.Religions.All(r => r.Belief != i)).ToList();
            if (!freeNames.Contains(_relName)) _relName = freeNames[0];
            if (!freeBeliefs.Contains(_relBelief)) _relBelief = freeBeliefs[0];
            var names = Ui.Flow(5);
            foreach (int i in freeNames)
            {
                int idx = i;
                names.AddChild(Ui.Btn(Rules.ReligionNames[i], () => { _relName = idx; RebuildWindow(); }, size: 13, variation: i == _relName ? "TabOn" : null));
            }
            Add(Ui.Lbl("Название:", 13, Ui.Dim)); Add(names);
            Add(Ui.Lbl("Верование (действует в каждом городе, который следует религии, чей бы он ни был):", 13, Ui.Dim));
            foreach (int i in freeBeliefs)
            {
                int idx = i;
                var bb = Ui.Btn($"{Rules.Beliefs[i].Name} — {Rules.Beliefs[i].Effect.Describe()}", () => { _relBelief = idx; RebuildWindow(); }, size: 13, variation: i == _relBelief ? "TabOn" : "Flat");
                bb.Alignment = HorizontalAlignment.Left;
                Add(bb);
            }
            var found = Ui.Btn($"Основать «{Rules.ReligionNames[_relName]}» со священным городом в столице", () => Net.SendCommand(CmdType.FoundReligion, _relName, _relBelief), size: 15, variation: "Primary");
            found.Disabled = me.Faith < price;
            Add(found);
        }

        Heading("Религии мира");
        if (me.Religions.Length == 0) Add(Ui.Lbl("Пока не основано ни одной религии.", 13, Ui.Dim));
        foreach (var r in me.Religions)
            Add(Ui.Lbl($"«{r.Name}» — основатель: {W.PlayerName(r.Founder)}; {Rules.Beliefs[r.Belief].Name} ({Rules.Beliefs[r.Belief].Effect.Describe()}); городов: {r.Cities}", 14,
                r.Id == me.MyReligion ? Ui.Gold : Colors.White));

        Heading("Ваши города");
        Add(Ui.Lbl($"Религия сама расходится на соседние города (до {Rules.ReligionRange} клеток); священный город влияет сильнее. Миссионер ({Rules.MissionaryFaith} веры) разом добавляет городу {Rules.PreachPressure} давления и проходит через любые границы.", 12, Ui.Dim));
        foreach (var c in W.Cities.Values.Where(c => c.Owner == W.MyId).OrderBy(c => c.Id))
        {
            int cid = c.Id;
            var rel = me.Religions.FirstOrDefault(r => r.Id == c.Religion);
            var name = Ui.Lbl($"{c.Name}: {(rel == null ? "без религии" : rel.Name)}", 14);
            name.CustomMinimumSize = new Vector2(300, 0);
            var recruit = Ui.Btn($"Нанять миссионера ({Rules.MissionaryFaith} веры)", () => Net.SendCommand(CmdType.Recruit, cid), size: 13);
            recruit.Disabled = me.Faith < Rules.MissionaryFaith || (mine == null && rel == null);
            Add(Ui.H(10, name, recruit));
        }
    }

    private void BuildEconomy()
    {
        var me = W.Me;
        _windowTitle.Text = "Экономика";
        Heading("Казна");
        var split = new Bar(Ui.Science, 10, 240).WithBack(Ui.Col(0x9A8440)).Set(me.Slider / 100f);
        split.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        Add(Ui.H(10,
            Ui.Btn("−10%", () => Net.SendCommand(CmdType.SetSlider, Math.Max(0, me.Slider - 10)), "Меньше науки, больше золота", 13),
            new Chip(Ico.Science, Ui.Science).Set($"Наука {me.Slider}%"), split, new Chip(Ico.Gold, Ui.Gold).Set($"Золото {100 - me.Slider}%"),
            Ui.Btn("+10%", () => Net.SendCommand(CmdType.SetSlider, Math.Min(100, me.Slider + 10)), "Больше науки, меньше золота", 13),
            Ui.Lbl("— так делится коммерция ваших городов", 12, Ui.Dim)));
        GridContainer Table()
        {
            var g = new GridContainer { Columns = 2 };
            g.AddThemeConstantOverride("h_separation", 28);
            g.AddThemeConstantOverride("v_separation", 3);
            return g;
        }
        var grid = Table();
        void Line(GridContainer g, string name, string value, Color? col = null, bool bold = false) { g.AddChild(Ui.Lbl(name, 13, Ui.Dim)); g.AddChild(Ui.Lbl(value, 13, col, bold)); }
        Line(grid, "Доход городов", $"+{me.Income}", Ui.Good);
        Line(grid, "Содержание юнитов", Cost(me.UnitUpkeep));
        Line(grid, "Содержание зданий", Cost(me.BuildingUpkeep));
        Line(grid, "Содержание городов (бюрократия)", Cost(me.CityMaint));
        Line(grid, "Содержание дорог", Cost(me.RoadUpkeep));
        Line(grid, "«Логистический надрыв» (штраф лидера)", Cost(me.StrainCost), me.StrainCost > 0 ? Ui.Bad : null);
        Line(grid, "Итого за ход", Ui.Signed(me.GoldRate), me.GoldRate < 0 ? Ui.Bad : Ui.Gold, true);
        var stock = Table();
        Line(stock, "В казне", me.Gold.ToString(), Ui.Gold, true);
        Line(stock, "Вера", $"{me.Faith} ({Ui.Signed(me.FaithRate)} за ход)", Ui.Faith);
        Line(stock, "Одобрение", $"{me.Favor} ({Ui.Signed(me.FavorRate)} за ход)", Ui.Favor);
        Line(stock, "Роскошь", me.Luxuries == 0 ? "нет" : $"{me.Luxuries} вид(ов): +{me.Luxuries} довольства в городах");
        var have = me.Resources.Select(r => Rules.Res((Resource)r)).ToList();
        var resources = Ui.Wrap(have.Count == 0 ? "Подключённых ресурсов нет — рабочие подключают их улучшениями" : "Ресурсы: " + string.Join(", ", have.Select(r => r.Name)), 360, 12, Ui.Dim);
        var left = Ui.Card(Ui.V(6, Ui.Lbl("ЗА ХОД", 11, Ui.Dim, true), grid), 10);
        var right = Ui.Card(Ui.V(6, Ui.Lbl("ЗАПАСЫ", 11, Ui.Dim, true), stock, resources), 10);
        left.SizeFlagsVertical = right.SizeFlagsVertical = SizeFlags.Fill;
        Add(Ui.H(10, left, right));

        Heading("Анти-Сноуболл: ваше положение в мире");
        Add(Ui.Lbl($"Индекс технологического разрыва (ITR): {me.Itr:+0.00;−0.00}", 15, null, true));
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
            Add(Ui.Lbl("• Эффект низкой базы: технологии, институты и узлы веток, уже открытые другими, стоят дешевле; ваши голоса в конгрессе весят больше", 14));
            Add(Ui.Lbl($"• Научное эхо от мирных контактов с развитыми державами: +{me.EchoBonus} науки за ход (больше при открытых границах)", 14));
        }
        else Add(Ui.Lbl("Вы идёте вровень с миром: ни штрафов лидера, ни бонусов догоняющего.", 14, Ui.Dim));
        if (me.Sprawl > 0.01f)
            Add(Ui.Lbl($"• Раздутая империя: городов заметно больше, чем у соседей — содержание дополнительно растёт (индекс {me.Sprawl:0.00})", 14, Ui.Gold));

        Heading("Связи с миром");
        Add(Ui.Lbl(me.TradeLuxuries > 0 ? $"• Торговля роскошью через открытые границы: +{me.TradeLuxuries} довольства в каждом городе (не больше {Rules.MaxTradeLuxuries})"
            : "• Торговли роскошью нет: откройте границы с державой, у которой есть роскошь, которой нет у вас", 14, me.TradeLuxuries > 0 ? Ui.Good : Ui.Dim));
        Add(Ui.Lbl(me.WarWeariness > 0 ? $"• Усталость от войны: +{me.WarWeariness} брожения за ход в каждом городе (войны дольше {Rules.WarWearinessAfter} ходов)"
            : "• Усталости от войны нет", 14, me.WarWeariness > 0 ? Ui.Bad : Ui.Dim));
        var state = me.Religions.FirstOrDefault(r => r.Id == me.StateReligion);
        Add(Ui.Lbl(state == null ? "• Государственной религии нет (ею становится религия столицы)"
            : $"• Государственная религия — «{state.Name}». Единоверцев среди известных держав: {me.Brethren} (+1 одобрения за каждого, сильнее научное эхо, ИИ охотнее идёт на мир и открытые границы)", 14,
            me.Brethren > 0 ? Ui.Good : Ui.Dim));
        Add(Ui.Btn("Демография и рейтинг держав (F9)", () => Toggle(Win.Demographics, true), size: 13));
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
        _windowTitle.Text = "Дипломатия и Всемирный конгресс";
        var me = W.Me;
        // ---- offers come first: they wait for an answer
        foreach (var pr in me.Proposals.Where(p => p.To == W.MyId))
        {
            int id = pr.Id;
            string what = pr.Kind == (byte)ProposalKind.Peace ? "заключить мир" : "открыть границы";
            Add(Ui.Card(Ui.H(10, new Icon(Ico.Favor, Ui.Gold, 18), Ui.Lbl($"«{W.PlayerName(pr.From)}» предлагает {what}", 15, Ui.Gold, true), Ui.Spacer(),
                Ui.Btn("Принять", () => Net.SendCommand(CmdType.Respond, id, 1), variation: "Primary"),
                Ui.Btn("Отклонить", () => Net.SendCommand(CmdType.Respond, id, 0))), 10, Ui.Col(0x2A2410), Ui.PanelEdge));
        }
        foreach (var pr in me.Proposals.Where(p => p.From == W.MyId))
            Add(Ui.Lbl($"Ваше предложение «{W.PlayerName(pr.To)}» ({(pr.Kind == (byte)ProposalKind.Peace ? "мир" : "открытые границы")}) ожидает ответа", 13, Ui.Dim));

        // ---- world congress
        Heading("Всемирный конгресс");
        var chairName = me.Chair >= 0 ? Ui.Lbl($"Председатель: «{W.PlayerName(me.Chair)}»", 15, Ui.Gold, true) : Ui.Lbl("Председатель не избран", 15, Ui.Dim, true);
        Add(Ui.H(14, chairName, Ui.Pill($"заседание через {me.CongressIn} х.", Ui.Favor), Ui.Spacer(),
            new Chip(Ico.Favor, Ui.Favor, "Ваше дипломатическое одобрение и его прирост за ход").Set(me.Favor.ToString(), Ui.Signed(me.FavorRate))));
        Note($"Каждое очко одобрения — один голос; голоса отстающих держав весят больше. Раз в {Rules.CongressPeriod} ходов конгресс выбирает председателя (+10% золота и культуры) и решает судьбу резолюции. Одобрение дают мир, единоверцы и мировые достижения.");
        var voteRow = Ui.Flow(6);
        voteRow.AddChild(Ui.Lbl("В председатели:", 13, Ui.Dim));
        foreach (var cand in W.Players.Values.Where(x => x.Alive && !x.Neutral && (x.Met || x.Id == W.MyId)).OrderBy(x => x.Id))
        {
            int cid = cand.Id;
            int votes = cid < me.Votes.Length ? me.Votes[cid] : 0;
            var vb = Ui.Btn($"{cand.Name} · {votes} гол.", () => Net.SendCommand(CmdType.Vote, cid, 10), "Отдать 10 одобрения за этого кандидата", 13);
            vb.AddThemeColorOverride("font_color", Ui.Col(cand.Color).Lightened(0.35f));
            vb.Disabled = me.Favor < 10;
            voteRow.AddChild(vb);
        }
        Add(voteRow);

        var active = Rules.Resolution(me.Resolution);
        var proposed = Rules.Resolution(me.Proposed);
        var res = Ui.V(6);
        res.AddChild(active != null
            ? Ui.H(8, Ui.Pill("действует", Ui.Good), Ui.Lbl($"«{active.Name}»: {active.Effect}", 13))
            : Ui.H(8, Ui.Pill("действующей резолюции нет", Ui.Dim)));
        if (proposed != null)
        {
            int total = me.VotesFor + me.VotesAgainst;
            var yes = Ui.Btn("За · 10 одобр.", () => Net.SendCommand(CmdType.VoteResolution, 1, 10), "Резолюция принимается, если голосов «за» больше, чем «против»", 13);
            var no = Ui.Btn("Против · 10 одобр.", () => Net.SendCommand(CmdType.VoteResolution, 0, 10), size: 13);
            yes.Disabled = no.Disabled = me.Favor < 10;
            res.AddChild(Ui.H(8, Ui.Pill("на голосовании", Ui.Gold), Ui.Lbl($"«{proposed.Name}»: {proposed.Effect}", 13)));
            var tally = new Bar(Ui.Good, 8, 220).WithBack(total > 0 ? Ui.Col(0x8A3B40) : Ui.Col(0x0A0E18)).Set(total > 0 ? me.VotesFor / (float)total : 0);
            tally.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            res.AddChild(Ui.H(10, Ui.Lbl($"за {me.VotesFor}", 13, Ui.Good), tally, Ui.Lbl($"против {me.VotesAgainst}", 13, Ui.Bad), Ui.Spacer(), yes, no));
        }
        Add(Ui.Card(res, 10));

        // ---- the powers
        Heading("Державы");
        Note("Мирный контакт с более развитой державой даёт «научное эхо»; открытые границы усиливают его, разрешают проход и позволяют торговать роскошью.");
        foreach (var p in W.Players.Values.Where(p => p.Id != W.MyId && !p.Neutral).OrderByDescending(p => p.Met).ThenBy(p => p.Id))
        {
            var color = p.Met ? Ui.Col(p.Color) : Ui.Dim;
            var stripe = new ColorRect { Color = color, CustomMinimumSize = new Vector2(4, 34) };
            var name = Ui.Lbl(p.Met ? p.Name : "Неизвестная цивилизация", 15, p.Met ? color.Lightened(0.3f) : Ui.Dim, true);
            name.CustomMinimumSize = new Vector2(200, 0);
            if (!p.Alive) { Add(Ui.Card(Ui.H(10, stripe, name, Ui.Lbl("уничтожена", 13, Ui.Dim)), 7)); continue; }
            if (!p.Met) { Add(Ui.Card(Ui.H(10, stripe, name, Ui.Lbl("контакт не установлен — исследуйте карту", 13, Ui.Dim)), 7)); continue; }
            string tags = (p.AI ? "ИИ" : p.Connected ? "игрок" : "игрок, не в сети") + $" · счёт {p.Score} · {Rules.EraName(p.Era)} · городов {p.Cities} · технологий {p.Techs}";
            var status = p.AtWar ? Ui.Pill("война", Ui.Bad) : p.OpenBorders ? Ui.Pill("мир · открытые границы", Ui.Good) : Ui.Pill("мир", Ui.Good);
            var row = Ui.H(8, stripe, Ui.V(1, Ui.H(10, name, status), Ui.Lbl(tags, 12, Ui.Dim)), Ui.Spacer());
            int pid = p.Id;
            if (p.AtWar) row.AddChild(Ui.Btn("Предложить мир", () => Net.SendCommand(CmdType.Propose, pid, (int)ProposalKind.Peace), size: 13));
            else
            {
                row.AddChild(p.OpenBorders
                    ? Ui.Btn("Закрыть границы", () => Net.SendCommand(CmdType.CancelBorders, pid), size: 13)
                    : Ui.Btn("Открыть границы", () => Net.SendCommand(CmdType.Propose, pid, (int)ProposalKind.OpenBorders), size: 13));
                row.AddChild(Ui.Btn("Объявить войну", () => Net.SendCommand(CmdType.DeclareWar, pid), size: 13, variation: "Danger"));
            }
            var giftGold = Ui.Btn("Дар: 50 зол.", () => Net.SendCommand(CmdType.Gift, pid, 0, 50), size: 12, variation: "Flat");
            giftGold.Disabled = me.Gold < 50;
            var giftFavor = Ui.Btn("Дар: 10 одобр.", () => Net.SendCommand(CmdType.Gift, pid, 1, 10), size: 12, variation: "Flat");
            giftFavor.Disabled = me.Favor < 10;
            row.AddChild(giftGold); row.AddChild(giftFavor);
            foreach (var k in row.GetChildren()) if (k is Button btn) btn.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            Add(Ui.Card(row, 7));
        }
        var free = W.Players.Values.FirstOrDefault(p => p.Neutral);
        if (free != null && W.Cities.Values.Any(c => c.Owner == free.Id))
            Note("Вольные города — отделившиеся провинции сверхдержав. Они ни с кем не договариваются; их можно захватить.");
    }

    private void BuildDemographics()
    {
        _windowTitle.Text = "Рейтинг держав и мировые достижения";
        var me = W.Me;
        Heading("Мировые достижения");
        Note($"У бесконечной партии нет финала, но есть за что соревноваться: первая держава, достигшая цели, получает +{Rules.MilestoneFavor} дипломатического одобрения. Каждое достижение достаётся только одному.");
        var miles = new GridContainer { Columns = 2 };
        miles.AddThemeConstantOverride("h_separation", 10);
        miles.AddThemeConstantOverride("v_separation", 5);
        for (int i = 0; i < Rules.Milestones.Length; i++)
        {
            int who = i < me.Milestones.Length ? me.Milestones[i] : -1;
            bool mine = who == W.MyId;
            var holder = who < 0 ? Ui.Lbl("свободно", 12, Ui.Dim) : Ui.Lbl(mine ? "ваше" : W.PlayerName(who), 12, mine ? Ui.Good : W.Players.TryGetValue(who, out var hp) && hp.Met ? Map.OwnerColor(who).Lightened(0.3f) : Ui.Dim);
            var row = Ui.H(8, new Icon(who < 0 ? Ico.Star : Ico.Check, who < 0 ? new Color(Ui.Dim, 0.5f) : mine ? Ui.Good : Ui.Gold, 15),
                Ui.Lbl(Rules.Milestones[i].Name, 13, who < 0 ? Ui.Text : mine ? Ui.Good : Ui.Dim), Ui.Spacer(), holder);
            var card = Ui.Card(row, 7, mine ? Ui.Col(0x16301F, 0.9f) : null);
            card.CustomMinimumSize = new Vector2(Math.Min(440, (_wrapW - 20) / 2), 0);
            miles.AddChild(card);
        }
        Add(miles);

        Heading("Демография");
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 34);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (string h in new[] { "Показатель", "Вы", "Место", "Лучший", "Средний", "Худший" }) grid.AddChild(Ui.Lbl(h.ToUpperInvariant(), 11, Ui.Dim, true));
        foreach (var d in me.Demo)
        {
            grid.AddChild(Ui.Lbl(d.Key, 14));
            grid.AddChild(Ui.Lbl(d.Mine.ToString(), 14, Ui.Gold, true));
            grid.AddChild(Ui.Lbl(d.Rank.ToString(), 14, d.Rank == 1 ? Ui.Good : null));
            grid.AddChild(Ui.Lbl(d.Best.ToString(), 14));
            grid.AddChild(Ui.Lbl(d.Avg.ToString(), 14));
            grid.AddChild(Ui.Lbl(d.Worst.ToString(), 14));
        }
        Add(grid);

        Heading("Известные цивилизации");
        var known = W.Players.Values.Where(p => !p.Neutral && (p.Met || p.Id == W.MyId)).OrderByDescending(p => p.Score).ToList();
        int best = Math.Max(1, known.Count > 0 ? known.Max(p => p.Score) : 1);
        foreach (var p in known)
        {
            var name = Ui.Lbl(p.Name + (p.Id == W.MyId ? " (вы)" : ""), 14, Ui.Col(p.Color).Lightened(0.3f), true);
            name.CustomMinimumSize = new Vector2(220, 0);
            var bar = new Bar(Ui.Col(p.Color), 8, 260).Set(p.Score / (float)best);
            bar.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            Add(Ui.H(12, name, bar, Ui.Lbl($"{p.Score}", 14, null, true), Ui.Lbl($"{Rules.EraName(p.Era)} · городов {p.Cities}{(p.Alive ? "" : " · уничтожена")}", 12, Ui.Dim)));
        }
    }

    private void BuildHelp()
    {
        _windowTitle.Text = "Справка";
        Heading("Управление");
        var keys = new GridContainer { Columns = 4 };
        keys.AddThemeConstantOverride("h_separation", 14);
        keys.AddThemeConstantOverride("v_separation", 5);
        void Key(string key, string what)
        {
            var k = Ui.Pill(key, Ui.Gold, 12);
            k.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            keys.AddChild(k);
            keys.AddChild(Ui.Wrap(what, Math.Min(360, (_wrapW - 420) / 2), 13));
        }
        Key("ЛКМ", "выбрать юнит; ещё раз — следующий на клетке; город — его панель");
        Key("ПКМ", "идти или атаковать; при открытом городе — купить клетку");
        Key("Колесо", "масштаб карты");
        Key("WASD / перетаскивание", "движение камеры");
        Key("Q / E, Shift + мышь", "поворот камеры");
        Key("Enter", "завершить ход");
        Key("N", "следующий юнит без приказа");
        Key("Пробел", "пропустить юнит до следующего хода");
        Key("F", "укрепиться / спать");
        Key("B", "основать город (поселенец)");
        Key("P", "проповедовать (миссионер в городе)");
        Key("C", "камера на выбранный юнит");
        Key("T", "чат");
        Key("M", "звук вкл/выкл");
        Key("Esc", "закрыть окно, отменить заклинание, снять выбор");
        foreach (var a in Advisors) Key(a.key, a.title + " — " + a.tip.ToLower());
        Add(keys);

        Heading("Как устроена игра");
        void Topic(Ico icon, Color color, string title, string text)
            => Add(Ui.Card(Ui.H(10, new Icon(icon, color, 20), Ui.V(2, Ui.Lbl(title, 14, color, true), Ui.Wrap(text, Math.Min(_wrapW, 900) - 70, 13))), 9));
        Topic(Ico.City, Ui.Prod, "Города", "Жители обрабатывают клетки вокруг города: пища растит население, производство строит, коммерция делится на науку и золото. Рабочие улучшают клетки и подключают ресурсы: бонусные усиливают клетку, стратегические нужны для юнитов, роскошь поднимает довольство.");
        Topic(Ico.Science, Ui.Science, "Наука и культура", $"Наука открывает технологии, культура — социальные институты; оба дерева бесконечны. Они подпитывают друг друга: каждая технология добавляет {Rules.Inspiration * 100:0}% своей цены к текущему институту, и наоборот.");
        Topic(Ico.Branch, Ui.Power, "Ветки развития", "Магия, шаманство и ветки из модов растут параллельно. Узлы дают бонусы, особых юнитов, здания и действия; действия тратят силу ветки (ману, духов), которая копится от узлов, зданий и ресурсов карты.");
        Topic(Ico.Faith, Ui.Faith, "Религия", "Вера покупает мирные проекты и основывает религию. Верование действует в каждом городе, который ей следует; религия расходится сама и с миссионерами. Единая вера успокаивает города и сближает державы.");
        Topic(Ico.Favor, Ui.Favor, "Дипломатия и конгресс", "Мир и открытые границы дают научное эхо и торговлю роскошью. Одобрение — это голоса: за председателя конгресса и за резолюции, которые меняют правила для всех до следующего заседания.");
        Topic(Ico.Sword, Ui.Bad, "Война", "Перед атакой наведите курсор на врага: панель покажет шанс победы и урон. Холмы, лес, укрепление и стены помогают обороне. Долгая война утомляет ваши города.");
        Topic(Ico.Chart, Ui.Good, "Анти-Сноуболл", "Игра сама сдерживает отрыв: лидер получает штрафы к науке, культуре и содержанию и рискует протестами и сепаратизмом, а отстающие — скидки на уже открытое миром и научное эхо. Положение показано в правом углу верхней панели.");
    }
}
