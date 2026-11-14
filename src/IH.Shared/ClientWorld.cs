namespace IH.Shared;

/// <summary>
/// Client-side mirror of what the server has revealed to this player. It holds no game logic:
/// it only applies deltas. Used by the Godot client and by the headless bot.
/// </summary>
public sealed class ClientWorld
{
    public readonly HexGrid Grid;
    public readonly int MyId;
    public readonly GameSettings Settings;
    public readonly TileData[] Tiles;
    /// <summary>0 = unexplored, 1 = explored (fogged), 2 = currently visible.</summary>
    public readonly byte[] Vis;
    public readonly Dictionary<int, UnitDto> Units = new();
    public readonly Dictionary<int, CityDto> Cities = new();
    public readonly Dictionary<int, PlayerDto> Players = new();
    public MeDto Me = new();
    public int Turn, CurrentPlayer = -1, TimerLeft = -1;
    public long Revision;

    public ClientWorld(GameStartMsg start)
    {
        Rules.SetBranches(start.Branches);
        Grid = new HexGrid(start.W, start.H);
        MyId = start.YourId;
        Settings = start.Settings;
        Tiles = new TileData[Grid.Count];
        Vis = new byte[Grid.Count];
        for (int i = 0; i < Tiles.Length; i++) { Tiles[i].Owner = -1; Tiles[i].CityId = -1; }
    }

    public void Apply(DeltaMsg d)
    {
        if (d.Full)
        {
            Units.Clear(); Cities.Clear(); Players.Clear();
            Array.Clear(Vis);
        }
        Turn = d.Turn; CurrentPlayer = d.CurrentPlayer; TimerLeft = d.TimerLeft;
        foreach (var t in d.Tiles)
        {
            if (!Grid.Valid(t.Idx)) continue;
            Tiles[t.Idx] = TileData.Unpack(t.Pack);
            Vis[t.Idx] = (t.Pack & TileData.VisibleBit) != 0 ? (byte)2 : (byte)1;
        }
        foreach (int id in d.RemovedUnits) Units.Remove(id);
        foreach (var u in d.Units) Units[u.Id] = u;
        foreach (var c in d.Cities) Cities[c.Id] = c;
        foreach (var p in d.Players) Players[p.Id] = p;
        if (d.Me != null) Me = d.Me;
        Revision++;
    }

    public bool MyTurn => Players.TryGetValue(MyId, out var me) && me.Alive && !me.EndedTurn
        && (Settings.Simultaneous || CurrentPlayer == MyId);

    public IEnumerable<UnitDto> UnitsAt(int tile) => Units.Values.Where(u => u.Tile == tile);
    public CityDto? CityAt(int tile) => Tiles[tile].CityId >= 0 && Cities.TryGetValue(Tiles[tile].CityId, out var c) ? c : null;
    /// <summary>Technology, social institution or custom branch node.</summary>
    public bool Knows(string? tech) => tech == null || Rules.Satisfied(tech, Me.Known, Me.Future, Me.Civics, Me.CivicLevel, Me.Nodes);

    /// <summary>Local path prediction with the same cost rules the server uses, limited to what the player can see.</summary>
    public List<int>? PredictPath(UnitDto u, int target)
    {
        if (!Grid.Valid(target) || Vis[target] == 0) return null;
        var mover = Rules.MoverFor(u.Def, Knows);
        return Grid.FindPath(u.Tile, target, (from, to) =>
        {
            if (Vis[to] == 0) return -1;
            int c = Rules.MoveCost(Tiles[from], Tiles[to], mover);
            if (c < 0) return -1;
            int owner = Tiles[to].Owner;
            if (owner >= 0 && owner != MyId && Players.TryGetValue(owner, out var p) && !p.AtWar && !p.OpenBorders && !p.Neutral)
                return -1;
            return c;
        }, 6000);
    }

    /// <summary>What the player can foresee about an attack: the server's formulas fed with what this client sees.</summary>
    public sealed record CombatOdds(UnitDto Defender, bool Ranged, double Attack, double Defense, int DmgToDefender, int DmgToAttacker,
        double Win, List<string> AttackNotes, List<string> DefenseNotes);

    private bool Hostile(int owner) => owner != MyId && Players.TryGetValue(owner, out var p) && p.AtWar;

    /// <summary>
    /// Forecast of the selected unit attacking a tile, or null if there is nobody to fight there. It is an estimate:
    /// enemy institutions and the walls of enemy cities are not visible to the client, so the real numbers may differ.
    /// </summary>
    public CombatOdds? EstimateCombat(UnitDto att, int tile)
    {
        if (att.Owner != MyId || !Grid.Valid(tile) || Vis[tile] < 2 || tile == att.Tile) return null;
        var foes = UnitsAt(tile).Where(u => Hostile(u.Owner)).ToList();
        if (foes.Count == 0) return null;
        bool ranged = att.Def.X.Ranged > 0 && Grid.Distance(att.Tile, tile) <= att.Def.X.Ranged;
        if (!ranged && att.Def.Str <= 0) return null;

        bool Embarked(UnitDto u) => !u.Def.Sea && !u.Def.X.Flying && Tiles[u.Tile].IsWater;
        double Fear(UnitDto u, List<string> notes)
        {
            if (u.Def.X.Construct) return 1;
            int fear = 0;
            foreach (int t in Grid.Range(u.Tile, 1))
                foreach (var o in UnitsAt(t))
                    if (o.Def.X.FearPct > fear && (u.Owner == MyId ? Hostile(o.Owner) : o.Owner == MyId)) fear = o.Def.X.FearPct;
            if (fear > 0) notes.Add($"страх −{Math.Min(80, fear)}%");
            return 1 - Math.Min(80, fear) / 100.0;
        }
        double Field(UnitDto u, List<string> notes)
        {
            double s = u.Def.Str;
            if (u.Owner == MyId)
            {
                var fx = Rules.CivicBonus(Me.Civics, Me.CivicLevel);
                fx.Add(Rules.NodeBonus(Me.Nodes));
                double mult = 1 + 0.05 * Me.Future[Rules.BranchNano] + fx.StrPct / 100.0;
                if (mult > 1.001) notes.Add($"развитие +{(mult - 1) * 100:0}%");
                s *= mult;
            }
            if (Tiles[u.Tile].Forest && u.Def.X.ForestPct != 0) { s *= 1 + u.Def.X.ForestPct / 100.0; notes.Add($"в лесу +{u.Def.X.ForestPct}%"); }
            return s * Fear(u, notes);
        }
        double Defense(UnitDto d, List<string> notes)
        {
            if (Embarked(d)) { notes.Add("на транспорте −50%"); return d.Def.Str * 0.5 * d.Hp / 100.0; }
            double mod = 1;
            var t = Tiles[d.Tile];
            if (t.Relief == Relief.Hills) { mod += 0.25; notes.Add("холмы +25%"); }
            if (t.Forest) { mod += 0.25; notes.Add("лес +25%"); }
            if (d.Fortified) { mod += 0.25; notes.Add("укреплён +25%"); }
            if (CityAt(d.Tile) != null)
            {
                if (d.Def.CityDefPct > 0) { mod += d.Def.CityDefPct / 100.0; notes.Add($"в городе +{d.Def.CityDefPct}%"); }
                notes.Add("стены города неизвестны");
            }
            if (d.Shield > 0) { mod += d.Shield / 100.0; notes.Add($"щит +{d.Shield}%"); }
            return Field(d, notes) * mod * d.Hp / 100.0;
        }

        var none = new List<string>();
        var def = foes.OrderByDescending(f => Defense(f, none)).ThenBy(f => f.Id).First();
        var an = new List<string>();
        var dn = new List<string>();
        double dv = Math.Max(0.1, Defense(def, dn));
        if (def.Hp < 100) dn.Add($"ранен: {def.Hp}%");
        double field = Field(att, an);
        if (att.Hp < 100) an.Add($"ранен: {att.Hp}%");
        if (ranged)
        {
            double scale = att.Def.Str > 0 ? field / att.Def.Str : 1;
            double ra = att.Def.X.RangedStr * scale * att.Hp / 100.0;
            int dmg = Rules.RangedDamage(ra, dv);
            return new CombatOdds(def, true, ra, dv, dmg, 0, dmg >= def.Hp ? 1 : 0, an, dn);
        }
        double a = field * att.Hp / 100.0;
        if (Embarked(att)) { a *= 0.5; an.Add("с воды −50%"); }
        var (toDef, toAtt, win) = Rules.Duel(a, dv, att.Hp, def.Hp);
        return new CombatOdds(def, false, a, dv, toDef, toAtt, win, an, dn);
    }

    public string PlayerName(int id) => Players.TryGetValue(id, out var p) ? p.Name : id < 0 ? "—" : $"Игрок {id}";

    /// <summary>Human-readable (Russian) text for a server event; null for purely visual events.</summary>
    public string? Describe(EventDto e)
    {
        switch (e.Type)
        {
            case EventKind.Combat:
                bool won = e.S == "1";
                if (e.C == MyId) return won ? $"Победа в бою против «{PlayerName(e.D)}»" : $"Наш юнит погиб, атакуя «{PlayerName(e.D)}»";
                if (e.D == MyId) return won ? $"Мы потеряли юнит в обороне от «{PlayerName(e.C)}»" : $"Атака «{PlayerName(e.C)}» отбита";
                return null;
            case EventKind.CityFounded: return $"«{PlayerName(e.B)}» основывает город {e.S}";
            case EventKind.CityCaptured: return $"«{PlayerName(e.B)}» захватывает город {e.S} у «{PlayerName(e.C)}»";
            case EventKind.TechDone: return $"Изучено: {Rules.Tech(e.S)?.Name ?? e.S}";
            case EventKind.BuildDone:
                return $"{(Cities.TryGetValue(e.A, out var c) ? c.Name : "Город")}: построено — {Rules.ItemName(e.S)}";
            case EventKind.WarDeclared: return $"«{PlayerName(e.A)}» объявляет войну «{PlayerName(e.B)}»!";
            case EventKind.PeaceMade: return $"«{PlayerName(e.A)}» и «{PlayerName(e.B)}» заключили мир";
            case EventKind.BordersOpened: return $"«{PlayerName(e.A)}» и «{PlayerName(e.B)}» открыли границы";
            case EventKind.Protest: return $"Социальное брожение: протесты в городе {e.S}!";
            case EventKind.Secession: return $"Сепаратизм: город {e.S} отделился от «{PlayerName(e.B)}»!";
            case EventKind.PlayerDefeated: return $"Цивилизация «{PlayerName(e.A)}» уничтожена";
            case EventKind.Bankrupt: return $"Казна пуста: {e.S}";
            case EventKind.IdleCity: return $"Город {e.S} простаивает — выберите производство";
            case EventKind.Proposal:
                return $"«{PlayerName(e.A)}» предлагает: {(e.B == (int)ProposalKind.Peace ? "мир" : "открытые границы")}";
            case EventKind.Info: return e.S;
            case EventKind.Ranged:
                bool dead = e.S == "1";
                if (e.C == MyId) return dead ? $"Наш обстрел уничтожил юнит «{PlayerName(e.D)}»" : null;
                if (e.D == MyId) return dead ? $"Мы потеряли юнит под обстрелом «{PlayerName(e.C)}»" : $"«{PlayerName(e.C)}» обстреливает наши войска";
                return null;
            case EventKind.NodeDone: return $"{Rules.BranchOf(e.S)?.Name ?? "Ветка"}: освоено — {Rules.Node(e.S)?.Name ?? e.S}";
            case EventKind.CivicDone: return $"Принят институт: {Rules.Civic(e.S)?.Name ?? e.S}";
            case EventKind.ReligionFounded: return $"«{PlayerName(e.A)}» основывает религию «{e.S}»";
            case EventKind.CityConverted:
            {
                var rel = Me.Religions.FirstOrDefault(r => r.Id == e.B);
                return rel == null ? $"Город {e.S} больше не следует ни одной религии" : $"Город {e.S} принял религию «{rel.Name}»";
            }
            case EventKind.Milestone:
                return $"Мировое достижение: «{PlayerName(e.A)}» — {Array.Find(Rules.Milestones, m => m.Id == e.S)?.Name ?? e.S} (+{Rules.MilestoneFavor} одобрения)";
            case EventKind.Resolution:
            {
                var r = Rules.Resolution(e.S);
                return e.A == 1 ? $"Конгресс принял резолюцию «{r?.Name}» ({e.B} за, {e.C} против): {r?.Effect}"
                    : $"Конгресс отклонил резолюцию «{r?.Name}» ({e.B} за, {e.C} против)";
            }
            case EventKind.Congress:
                return e.A < 0 ? "Всемирный конгресс: голосов не подано, председатель не избран"
                    : $"Всемирный конгресс: председателем избран «{PlayerName(e.A)}» ({e.B} голосов) — +10% золота и культуры на {Rules.CongressPeriod} ходов";
            default: return null;
        }
    }
}
