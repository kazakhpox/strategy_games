using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

/// <summary>The always-visible part of the interface: resources, trackers, "needs attention", unit and tile panels.</summary>
public partial class GameScreen
{
    private void Refresh()
    {
        RefreshTopBar();
        RefreshTrackers();
        RefreshAttention();
        RefreshUnit();
        RefreshOdds();
        // the city panel takes the right edge, so the log and the reminders step aside
        bool city = _win == Win.City;
        _logPanel.Visible = !city;
        if (city) _attentionPanel.Visible = false;
    }

    // ----------------------------------------------------------- top bar
    private void RefreshTopBar()
    {
        var me = W.Me;
        _turn.Text = $"Ход {W.Turn}";
        _era.Text = Rules.EraName(Rules.EraOf(me.Known, me.Future));
        _gold.Set(me.Gold.ToString(), Ui.Signed(me.GoldRate), me.GoldRate < 0 ? Ui.Bad : null);
        _science.Set($"+{me.SciRate}");
        _culture.Set($"+{me.CultureRate}");
        _faith.Set(me.Faith.ToString(), Ui.Signed(me.FaithRate));
        _favor.Set(me.Favor.ToString(), Ui.Signed(me.FavorRate));
        for (int i = 0; i < _power.Count; i++)
        {
            int have = i < me.Power.Length ? me.Power[i] : 0, rate = i < me.PowerRate.Length ? me.PowerRate[i] : 0;
            // a branch the player has not touched yet stays out of the bar
            bool started = have > 0 || rate > 0 || Rules.Branches[i].Nodes.Any(n => me.Nodes.Contains(n.Id));
            _power[i].Visible = started;
            _power[i].Set(have.ToString(), rate > 0 ? $"+{rate}" : "");
        }

        Color tint;
        if (me.Pressure > 0.005f) { _itrText.Text = $"▲ Лидер · давление {me.Pressure * 100:0}%"; tint = Ui.Bad; }
        else if (me.Lag > 0.005f) { _itrText.Text = $"▼ Догоняющий · бонус {me.Lag * 100:0}%"; tint = Ui.Good; }
        else { _itrText.Text = "● Паритет"; tint = Ui.Dim; }
        _itrText.AddThemeColorOverride("font_color", tint);
        _itr.AddThemeStyleboxOverride("panel", Ui.Box(new Color(tint, 0.14f), new Color(tint, 0.5f), 10, 10, 3));
        _itr.TooltipText = $"Анти-Сноуболл. Индекс технологического разрыва (ITR): {me.Itr:+0.00;−0.00}\n" +
                           $"Наука ×{me.SciMult:0.00}, культура ×{me.CulMult:0.00}, содержание ×{me.UpkeepMult:0.00}\n" +
                           "Лидер платит штрафы, отстающий получает скидки и «научное эхо». Нажмите — подробности в «Экономике»";
    }

    // ---------------------------------------------------------- trackers
    private Control Tracker(Ico icon, Color color, string caption, string key, string title, string detail, float value, float ghost, bool alert, Action open, string tip, bool compact = false)
    {
        var name = Ui.Lbl(title, compact ? 13 : 14, alert ? Ui.Bad : Ui.Text, !compact);
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var bar = new Bar(color, compact ? 4 : 6).Set(value, ghost);
        Control body;
        if (compact)
            body = Ui.V(4, Ui.H(6, new Icon(icon, color, 14), name, Ui.Lbl(detail, 12, Ui.Dim)), bar);
        else
            body = Ui.V(3,
                Ui.H(6, new Icon(icon, color, 14), Ui.Lbl(caption, 11, new Color(color, 0.9f), true), Ui.Spacer(), Ui.Lbl(key, 11, new Color(Ui.Dim, 0.7f))),
                Ui.H(6, name, Ui.Lbl(detail, 12, Ui.Dim)), bar);
        var card = Ui.Clickable(body, open, tip, alert ? Ui.Col(0x8A3B40) : null, compact ? 5 : 7);
        card.CustomMinimumSize = new Vector2(226, 0);
        return card;
    }

    private void RefreshTrackers()
    {
        var me = W.Me;
        Ui.Clear(_trackers);
        var tech = Rules.Tech(me.Research);
        if (tech == null)
            _trackers.AddChild(Tracker(Ico.Science, Ui.Science, "НАУКА", "F6", me.Available.Length > 0 ? "Выберите исследование" : "Всё изучено", "", 0, 0,
                me.Available.Length > 0, () => Toggle(Win.Science, true), "Очки науки копятся впустую, пока исследование не выбрано"));
        else
        {
            int cost = me.Available.FirstOrDefault(a => a.Id == tech.Id)?.Cost ?? tech.Cost;
            _trackers.AddChild(Tracker(Ico.Science, Ui.Science, "НАУКА", "F6", tech.Name, Turns(cost - me.ResearchProgress, me.SciRate),
                me.ResearchProgress / (float)Math.Max(1, cost), me.SciRate / (float)Math.Max(1, cost), false, () => Toggle(Win.Science, true),
                $"{tech.Name}: {me.ResearchProgress}/{cost}, +{me.SciRate} за ход\nОткрывает: {Unlocks(tech)}"));
        }
        var civic = Rules.Civic(me.Civic);
        if (civic == null)
            _trackers.AddChild(Tracker(Ico.Culture, Ui.Culture, "КУЛЬТУРА", "F7", "Выберите институт", "", 0, 0, true, () => Toggle(Win.Civics, true),
                "Культура копится впустую, пока институт не выбран"));
        else
        {
            int cost = me.CivicCosts.FirstOrDefault(c => c.Id == civic.Id)?.Cost ?? civic.Cost;
            _trackers.AddChild(Tracker(Ico.Culture, Ui.Culture, "КУЛЬТУРА", "F7", civic.Name, Turns(cost - me.CivicProgress, me.CultureRate),
                me.CivicProgress / (float)Math.Max(1, cost), me.CultureRate / (float)Math.Max(1, cost), false, () => Toggle(Win.Civics, true),
                $"{civic.Name}: {me.CivicProgress}/{cost}, +{me.CultureRate} за ход\n{civic.Effect.Describe()}"));
        }
        // custom branches: one slim line each, the ones waiting for a choice first
        var lines = new List<(bool idle, Control card)>();
        for (int i = 0; i < Rules.Branches.Count; i++)
        {
            int idx = i;
            var b = Rules.Branches[i];
            var node = Rules.Node(i < me.BranchCurrent.Length ? me.BranchCurrent[i] : "");
            int rate = i < me.BranchRate.Length ? me.BranchRate[i] : 0, progress = i < me.BranchProgress.Length ? me.BranchProgress[i] : 0;
            bool canPick = BranchCanPick(i);
            if (node == null && !canPick && !b.Nodes.Any(n => me.Nodes.Contains(n.Id))) continue; // not reachable yet
            void Open() { _branchTab = idx; Toggle(Win.Branches, true); }
            if (node != null)
            {
                int cost = me.NodeCosts.FirstOrDefault(c => c.Id == node.Id)?.Cost ?? node.Cost;
                lines.Add((false, Tracker(Ico.Branch, PowerColor(i), "", "", $"{b.Name}: {node.Name}", Turns(cost - progress, rate),
                    progress / (float)Math.Max(1, cost), rate / (float)Math.Max(1, cost), false, Open, $"{node.Name}: {progress}/{cost}, +{rate} за ход\n{node.Effect.Describe()}", true)));
            }
            else if (canPick)
                lines.Add((true, Tracker(Ico.Branch, PowerColor(i), "", "", $"{b.Name}: выберите узел", "", 0, 0, true, Open, "Очки ветки копятся, пока узел не выбран", true)));
            else
            {
                int done = b.Nodes.Count(n => me.Nodes.Contains(n.Id));
                lines.Add((false, Tracker(Ico.Branch, PowerColor(i), "", "", $"{b.Name}: {(done == b.Nodes.Length ? "освоена" : "ждёт условий")}", $"{done}/{b.Nodes.Length}",
                    done / (float)Math.Max(1, b.Nodes.Length), 0, false, Open, "Следующие узлы требуют технологий, институтов или узлов другой ветки", true)));
            }
        }
        foreach (var (_, card) in lines.OrderByDescending(l => l.idle).Take(3)) _trackers.AddChild(card);
    }

    private bool BranchCanPick(int i)
    {
        var me = W.Me;
        if (i < me.BranchCurrent.Length && me.BranchCurrent[i].Length > 0) return false;
        var have = new HashSet<string>(me.Nodes);
        return Rules.Branches[i].Nodes.Any(n => !have.Contains(n.Id) && n.Requires.All(r => Rules.Satisfied(r, me.Known, me.Future, me.Civics, me.CivicLevel, have)));
    }

    // ------------------------------------------- attention list, end turn
    private void RefreshAttention()
    {
        var me = W.Me;
        Ui.Clear(_attention);
        bool alive = W.Players.TryGetValue(W.MyId, out var self) && self.Alive;
        int blocking = 0, shown = 0;
        void Item(Ico icon, Color color, string text, Action act, string tip, bool blocks = true)
        {
            if (blocks) blocking++;
            if (shown++ >= 6) return;
            var b = Ui.RowBtn(text, "", act, tip, icon, color);
            b.CustomMinimumSize = new Vector2(262, 27);
            _attention.AddChild(b);
        }
        if (alive)
        {
            foreach (var pr in me.Proposals.Where(p => p.To == W.MyId).Take(2))
                Item(Ico.Favor, Ui.Favor, $"«{W.PlayerName(pr.From)}» предлагает {(pr.Kind == (byte)ProposalKind.Peace ? "мир" : "открыть границы")}",
                    () => Toggle(Win.Diplomacy, true), "Ответить можно в окне дипломатии", false);
            if (me.Research.Length == 0 && me.Available.Length > 0)
                Item(Ico.Science, Ui.Science, "Выберите исследование", () => Toggle(Win.Science, true), "Наука копится впустую");
            if (me.Civic.Length == 0)
                Item(Ico.Culture, Ui.Culture, "Выберите социальный институт", () => Toggle(Win.Civics, true), "Культура копится впустую");
            for (int i = 0; i < Rules.Branches.Count; i++)
            {
                if (!BranchCanPick(i)) continue;
                int idx = i;
                Item(Ico.Branch, PowerColor(i), $"{Rules.Branches[i].Name}: выберите узел", () => { _branchTab = idx; Toggle(Win.Branches, true); }, "Очки ветки копятся, пока узел не выбран");
            }
            var idleCities = W.Cities.Values.Where(c => c.Owner == W.MyId && c.Detail is { Queue.Length: 0, DisorderTurns: 0 }).OrderBy(c => c.Id).ToList();
            foreach (var c in idleCities.Take(2))
            {
                int cid = c.Id, tile = c.Tile;
                Item(Ico.City, Ui.Prod, $"{c.Name} простаивает", () => { Map.CenterOn(tile); OpenCity(cid); }, "Город ничего не строит — выберите производство");
            }
            if (idleCities.Count > 2) Item(Ico.City, Ui.Prod, $"…и ещё городов без дела: {idleCities.Count - 2}", () => Toggle(Win.Economy, true), "Список городов — в окне экономики");
            int idle = W.MyTurn ? W.Units.Values.Count(IsReady) : 0;
            if (idle > 0) Item(Ico.Sword, Ui.Text, $"Юниты без приказа: {idle}", () => SelectNext(true), "N — следующий юнит, Пробел — пропустить, F — укрепиться");
            if (me.MyReligion == 0 && me.Religions.Length < Rules.Beliefs.Length && me.Faith >= Rules.ReligionPrice(me.Religions.Length))
                Item(Ico.Faith, Ui.Faith, "Можно основать религию", () => Toggle(Win.Religion, true), "Веры хватает на собственную религию", false);
            if (me.CongressIn <= 5 && me.Favor >= 10)
                Item(Ico.Favor, Ui.Favor, $"Конгресс через {me.CongressIn} х. — голосуйте", () => Toggle(Win.Diplomacy, true), "Выборы председателя и резолюция: голоса — ваше одобрение", false);
            foreach (var c in W.Cities.Values.Where(c => c.Owner == W.MyId && c.Detail is { } d && (d.DisorderTurns > 0 || d.Unrest >= 60)).Take(2))
            {
                int cid = c.Id, tile = c.Tile;
                Item(Ico.Unrest, Ui.Bad, c.Detail!.DisorderTurns > 0 ? $"{c.Name}: беспорядки" : $"{c.Name}: брожение {c.Detail.Unrest}/100",
                    () => { Map.CenterOn(tile); OpenCity(cid); }, "Довольство, храмы, единая вера и гарнизон успокаивают город", false);
            }
            if (me.GoldRate < 0 && me.Gold + me.GoldRate * 5 < 0)
                Item(Ico.Gold, Ui.Bad, $"Казна тает: {Ui.Signed(me.GoldRate)} за ход", () => Toggle(Win.Economy, true), "При пустой казне распускаются юниты и продаются здания", false);
        }
        _attentionPanel.Visible = shown > 0 && _win != Win.City;

        var waiting = W.Players.Values.Where(p => p.Alive && !p.Neutral && !p.AI && !p.EndedTurn && p.Id != W.MyId).Select(p => p.Name).ToList();
        if (!alive) { _endTurn.Text = "Вы выбыли — режим наблюдателя"; _endTurn.Disabled = true; _endTurn.ThemeTypeVariation = ""; }
        else if (W.MyTurn)
        {
            _endTurn.Disabled = false;
            _endTurn.Text = blocking > 0 ? $"Завершить ход\nостались дела: {blocking}" : "Завершить ход";
            _endTurn.AddThemeFontSizeOverride("font_size", blocking > 0 ? 14 : 17);
            _endTurn.ThemeTypeVariation = blocking > 0 ? "" : "Primary";
            _endTurn.TooltipText = blocking > 0 ? "Enter — завершить ход, не дожидаясь решений из списка выше" : "Enter";
        }
        else
        {
            _endTurn.Disabled = true;
            _endTurn.ThemeTypeVariation = "";
            _endTurn.AddThemeFontSizeOverride("font_size", 14);
            _endTurn.Text = W.Settings.Simultaneous
                ? "Ожидание: " + (waiting.Count > 0 ? string.Join(", ", waiting) : "сервер")
                : $"Ходит: {W.PlayerName(W.CurrentPlayer)}";
        }
    }

    // -------------------------------------------------------- unit panel
    private static Ico UnitIcon(UnitDef def) => def.Role switch
    {
        UnitRole.Settler => Ico.City,
        UnitRole.Worker => Ico.Prod,
        UnitRole.Missionary => Ico.Faith,
        _ => def.Sea ? Ico.Ship : def.X.Ranged > 0 ? Ico.Bow : Rules.Actions.Any(a => a.Caster == def.Id) ? Ico.Power : Ico.Sword,
    };

    private void RefreshUnit()
    {
        Ui.Clear(_unitBox);
        if (Selected is not { } u || _win == Win.City) { _unitPanel.Visible = false; return; }
        _unitPanel.Visible = true;
        var def = u.Def;
        bool mine = u.Owner == W.MyId;

        string state = u.Work != 0 ? $"{Rules.WorkName((WorkKind)u.Work)}: ещё {u.WorkTurns} х." : u.Fortified ? (def.Str > 0 ? "Укреплён" : "Спит") : u.Goto >= 0 ? "В пути" : "";
        var title = Ui.H(8, Ui.Lbl(def.Name, 17, null, true));
        if (state.Length > 0) title.AddChild(Ui.Pill(state, Ui.Gold, 11));
        if (u.Expires > 0) title.AddChild(Ui.Pill($"исчезнет через {u.Expires} х.", Ui.Power, 11));
        if (u.Shield > 0) title.AddChild(Ui.Pill($"щит +{u.Shield}%", Ui.Science, 11));
        var hpColor = u.Hp >= 67 ? Ui.Good : u.Hp >= 34 ? Ui.Gold : Ui.Bad;
        var hp = Ui.H(6, new Icon(Ico.Heart, hpColor, 13), new Bar(hpColor, 7, 150).Set(u.Hp / 100f), Ui.Lbl($"{u.Hp}", 12, Ui.Dim));
        var stats = Ui.H(14);
        if (def.Str > 0) stats.AddChild(new Chip(Ico.Sword, Ui.Text, "Сила в ближнем бою", 14).Set(def.Str.ToString()));
        if (def.X.Ranged > 0) stats.AddChild(new Chip(Ico.Bow, Ui.Text, "Обстрел: сила и дальность. Стрелок не получает урона в ответ", 14).Set(def.X.RangedStr.ToString(), $"дальн. {def.X.Ranged}"));
        stats.AddChild(new Chip(Ico.Moves, u.Moves > 0 ? Ui.Good : Ui.Dim, "Очки движения: осталось / всего", 14).Set($"{u.Moves / (float)Rules.MovePoint:0.#}/{def.Moves}"));
        var badge = new Badge(UnitIcon(def), Map.OwnerColor(u.Owner), 46);
        var head = Ui.H(10, badge, Ui.V(4, title, hp), Ui.Spacer(), stats);
        _unitBox.AddChild(head);

        string abilities = def.X.Abilities();
        if (abilities.Length > 0) _unitBox.AddChild(Ui.Wrap(abilities, 450, 12, Ui.Col(0xB9C6E8)));
        if (!mine) return;

        var row = Ui.Flow(5);
        _unitBox.AddChild(row);
        // spells and rites this unit performs
        foreach (var a in Rules.Actions.Where(a => a.Caster == def.Id))
        {
            var act = a;
            int uid = u.Id;
            var branch = Rules.BranchOfAction(a.Id);
            int bi = branch == null ? -1 : Rules.Branches.ToList().FindIndex(b => b.Id == branch.Id);
            int have = bi >= 0 && bi < W.Me.Power.Length ? W.Me.Power[bi] : 0;
            int ai = Rules.Actions.ToList().FindIndex(x => x.Id == a.Id);
            int wait = ai >= 0 && ai < W.Me.Cooldowns.Length ? W.Me.Cooldowns[ai] : 0;
            bool known = W.Knows(a.Requires);
            string why = !known ? $"Нужно: {Rules.RequirementName(a.Requires)}" : wait > 0 ? $"Перезарядка: {wait} х."
                : have < a.CostPower ? $"Не хватает: {branch?.PowerName} {have}/{a.CostPower}" : u.Moves <= 0 ? "У юнита нет очков движения" : "";
            var cast = Ui.Btn($"✦ {a.Name} · {a.Price(branch?.PowerName ?? "силы")}", () => BeginAction(act, uid), a.Effect() + (why.Length > 0 ? "\n" + why : ""), 13);
            cast.AddThemeColorOverride("font_color", PowerColor(Math.Max(0, bi)));
            cast.Disabled = why.Length > 0 || !W.MyTurn;
            row.AddChild(cast);
        }
        if (def.Role == UnitRole.Settler) row.AddChild(Ui.Btn("Основать город  B", () => UnitCommand(CmdType.FoundCity), "Поселенец становится новым городом на этой клетке", 13, "Primary"));
        if (def.Role == UnitRole.Missionary)
        {
            var target = W.CityAt(u.Tile);
            var preach = Ui.Btn(target != null ? $"Проповедовать в {target.Name}  P" : "Проповедовать  P — встаньте в город",
                () => UnitCommand(CmdType.Preach), "Миссионер тратится и добавляет городу давление своей религии. Проходит через любые границы", 13, target != null ? "Primary" : null);
            preach.Disabled = target == null;
            row.AddChild(preach);
        }
        if (def.Role == UnitRole.Worker && u.Work == 0)
            foreach (var kind in Rules.AllWork)
            {
                var tile = W.Tiles[u.Tile];
                if (tile.IsWater != def.Sea) continue;
                if (!Rules.WorkFits(tile, kind) || !W.Knows(Rules.WorkTech(kind))) continue;
                if (tile.Owner != W.MyId && !(kind == WorkKind.Road && tile.Owner < 0)) continue;
                var k = kind;
                // the improvement the tile's resource asks for is the obvious choice
                bool wanted = tile.Resource != Resource.None && Rules.Res(tile.Resource).Imp == kind && (int)tile.Improvement != (int)kind;
                row.AddChild(Ui.Btn($"{Rules.WorkName(kind)} · {Rules.WorkTurns(kind)} х.", () => UnitCommand(CmdType.Work, (int)k),
                    wanted ? $"Подключит ресурс: {Rules.Res(tile.Resource).Name}" : null, 13, wanted ? "Primary" : null));
            }
        if (u.Fortified || u.Work != 0 || u.Goto >= 0) row.AddChild(Ui.Btn("Отменить приказ", () => UnitCommand(CmdType.Wake), size: 13));
        else row.AddChild(Ui.Btn(def.Str > 0 ? "Укрепиться  F" : "Спать  F", () => UnitCommand(CmdType.Fortify),
            def.Str > 0 ? "+25% к обороне; юнит не просит приказов, пока его не разбудят" : "Юнит не просит приказов, пока его не разбудят", 13));
        int upTo = Rules.UpgradeTarget(u.Type, W.Knows, HasRes);
        if (upTo >= 0 && W.Tiles[u.Tile].Owner == W.MyId)
        {
            int price = Rules.UpgradePrice(u.Type, upTo);
            var up = Ui.Btn($"Улучшить до «{Rules.Units[upTo].Name}» · {price} зол.", () => UnitCommand(CmdType.Upgrade),
                "Юнит заменяется современным аналогом; тратит ход. Только на своей территории", 13);
            up.Disabled = W.Me.Gold < price || u.Moves <= 0;
            row.AddChild(up);
        }
        row.AddChild(Ui.Btn("Пропустить  Пробел", SkipUnit, "До следующего хода юнит не будет просить приказов", 13));
        if (W.CityAt(u.Tile) is { } here && here.Owner == W.MyId)
        {
            int cid = here.Id;
            row.AddChild(Ui.Btn($"Город {here.Name}", () => OpenCity(cid), size: 13));
        }
        int id = u.Id;
        row.AddChild(Ui.Btn(_confirmDisband == id ? "Точно распустить?" : "Распустить", () =>
        {
            if (_confirmDisband == id) { Net.SendCommand(CmdType.Disband, id); _confirmDisband = -1; }
            else { _confirmDisband = id; Refresh(); }
        }, "Юнит исчезает навсегда; содержание за него больше не платится", 13, _confirmDisband == id ? "Danger" : null));

        var others = W.UnitsAt(u.Tile).Where(o => o.Owner == W.MyId && o.Id != u.Id).OrderBy(o => o.Id).ToList();
        if (others.Count > 0)
        {
            var stack = Ui.Flow(4);
            stack.AddChild(Ui.Lbl("На этой клетке:", 12, Ui.Dim));
            foreach (var o in others.Take(8))
            {
                int oid = o.Id;
                stack.AddChild(Ui.Btn(o.Def.Name, () => { Map.Select(oid); Refresh(); }, size: 12, variation: "Flat"));
            }
            _unitBox.AddChild(stack);
        }
    }

    // ------------------------------------------------- tile under cursor
    public void HoverChanged(int tile)
    {
        Ui.Clear(_tileBox);
        RefreshOdds();
        if (tile < 0 || W.Vis[tile] == 0) { _tileBox.AddChild(Ui.Lbl("Наведите курсор на клетку", 12, new Color(Ui.Dim, 0.6f))); return; }
        var t = W.Tiles[tile];
        var (f, p, c) = Rules.TileYield(t);
        string terrain = t.Relief == Relief.Mountain ? "Горы" : t.Terrain switch
        {
            Terrain.Ocean => "Океан", Terrain.Coast => "Побережье", Terrain.Grassland => "Луга", Terrain.Plains => "Равнина",
            Terrain.Desert => "Пустыня", Terrain.Tundra => "Тундра", _ => "Снега",
        };
        if (t.Relief == Relief.Hills) terrain += ", холмы";
        if (t.Forest) terrain += ", лес";
        if (t.Improvement != Improvement.None) terrain += " · " + Rules.WorkName((WorkKind)(int)t.Improvement).ToLower();
        if (t.Road) terrain += " · дорога";
        var first = Ui.H(8, Ui.Lbl(terrain, 12));
        first.AddChild(Ui.Spacer());
        first.AddChild(new Chip(Ico.Food, Ui.Food, "", 12).Set(f.ToString()));
        first.AddChild(new Chip(Ico.Prod, Ui.Prod, "", 12).Set(p.ToString()));
        first.AddChild(new Chip(Ico.Gold, Ui.Gold, "", 12).Set(c.ToString()));
        _tileBox.AddChild(first);
        var second = Ui.H(8);
        if (t.Resource != Resource.None)
        {
            var rd = Rules.Res(t.Resource);
            string cls = rd.Class switch { ResClass.Bonus => "бонус", ResClass.Strategic => "стратегический", _ => "роскошь" };
            bool worked = (int)t.Improvement == (int)rd.Imp;
            var res = Ui.Lbl($"◆ {rd.Name} ({cls}{(worked ? "" : ", нужно: " + Rules.WorkName(rd.Imp).ToLower())})", 12, MapView.ResourceColor(t.Resource).Lightened(0.25f));
            res.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            res.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            second.AddChild(res);
        }
        else second.AddChild(Ui.Spacer());
        if (t.Owner >= 0) second.AddChild(Ui.Lbl(W.PlayerName(t.Owner), 12, Map.OwnerColor(t.Owner).Lightened(0.3f)));
        else if (W.Vis[tile] == 1) second.AddChild(Ui.Lbl("туман войны", 12, Ui.Dim));
        _tileBox.AddChild(second);
    }

    // ------------------------------------------------------ combat odds
    private void RefreshOdds() => ShowOdds(Selected, Selected is { } u && _aiming == null && _win == Win.None ? W.EstimateCombat(u, Map.HoverTile) : null);

    /// <summary>Fills the forecast panel; separate from the hover logic so the smoke tour can show it without a war.</summary>
    internal void ShowOdds(UnitDto? att, ClientWorld.CombatOdds? odds)
    {
        Ui.Clear(_oddsBox);
        _oddsPanel.Visible = odds != null && att != null;
        if (odds == null || att == null) return;
        var foe = odds.Defender;
        var foeColor = Map.OwnerColor(foe.Owner).Lightened(0.3f);
        _oddsBox.AddChild(Ui.H(8,
            Ui.Lbl(att.Def.Name, 14, null, true), Ui.Lbl($"{odds.Attack:0.#}", 13, Ui.Dim),
            new Icon(odds.Ranged ? Ico.Bow : Ico.Sword, Ui.Gold, 15),
            Ui.Lbl($"{odds.Defense:0.#}", 13, Ui.Dim), Ui.Lbl(foe.Def.Name, 14, foeColor, true), Ui.Lbl($"«{W.PlayerName(foe.Owner)}»", 12, Ui.Dim)));
        if (odds.Ranged)
        {
            bool kill = odds.DmgToDefender >= foe.Hp;
            _oddsBox.AddChild(Ui.Lbl(kill ? "Обстрел уничтожит цель" : $"Обстрел: ≈ −{odds.DmgToDefender} здоровья цели (останется {foe.Hp - odds.DmgToDefender}), без ответного урона", 13, kill ? Ui.Good : Ui.Text));
        }
        else
        {
            var verdict = odds.Win >= 0.75 ? ("уверенная победа", Ui.Good) : odds.Win >= 0.55 ? ("перевес", Ui.Good)
                : odds.Win >= 0.4 ? ("бой на равных", Ui.Gold) : odds.Win >= 0.2 ? ("риск", Ui.Bad) : ("почти верная гибель", Ui.Bad);
            var bar = new Bar(Ui.Good, 8, 200).WithBack(Ui.Col(0x8A3B40)).Set((float)odds.Win);
            _oddsBox.AddChild(Ui.H(10, Ui.Lbl($"≈ {odds.Win * 100:0}%", 16, verdict.Item2, true), bar, Ui.Lbl(verdict.Item1, 13, verdict.Item2)));
            _oddsBox.AddChild(Ui.Lbl($"Бой до гибели одного из юнитов. Урон за удар: врагу −{odds.DmgToDefender}, нам −{odds.DmgToAttacker}", 12, Ui.Dim));
        }
        var notes = odds.AttackNotes.Select(n => "мы: " + n).Concat(odds.DefenseNotes.Select(n => "враг: " + n)).ToList();
        if (notes.Count > 0) _oddsBox.AddChild(Ui.Wrap(string.Join(" · ", notes), 440, 12, Ui.Col(0xB9C6E8)));
        if (W.UnitsAt(foe.Tile).Count(x => x.Owner == foe.Owner) > 1) _oddsBox.AddChild(Ui.Lbl("На клетке несколько юнитов — отвечает сильнейший в обороне", 11, Ui.Dim));
    }
}

/// <summary>Round portrait of a unit: the owner's colour with an icon of what the unit does.</summary>
public partial class Badge : Control
{
    private readonly Ico _icon;
    private readonly Color _color;

    public Badge(Ico icon, Color color, float size)
    {
        _icon = icon; _color = color;
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var c = Size / 2;
        float r = Math.Min(Size.X, Size.Y) / 2;
        DrawCircle(c, r, _color.Darkened(0.45f));
        DrawCircle(c, r - 2.5f, _color);
        bool light = _color.Luminance > 0.55f;
        float s = r * 1.05f;
        Icon.Paint(this, _icon, new Rect2(c - new Vector2(s, s) / 2, new Vector2(s, s)), light ? new Color(0.08f, 0.1f, 0.15f) : Colors.White);
    }
}
