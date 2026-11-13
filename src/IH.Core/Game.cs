using IH.Shared;

namespace IH.Core;

/// <summary>
/// Authoritative game simulation. Has no knowledge of networking or rendering:
/// it validates and executes player commands and advances turns.
/// </summary>
public sealed partial class Game
{
    public readonly GameState S;
    public readonly HexGrid Grid;
    public readonly List<GameEvent> Events = new();
    private readonly List<Unit>[] _at;
    private static readonly List<Unit> NoUnits = new();

    public BalanceConfig Bal => S.Balance;

    public Game(GameState state)
    {
        S = state;
        Grid = new HexGrid(S.W, S.H);
        _at = new List<Unit>[Grid.Count];
        foreach (var u in S.Units.Values) (_at[u.Tile] ??= new List<Unit>()).Add(u);
    }

    public static Game Create(GameSettings settings, IList<(string name, bool ai)> players, BalanceConfig? balance = null)
    {
        if (players.Count < 1 || players.Count > 12) throw new ArgumentException("1..12 players required");
        settings.MapW = Math.Clamp(settings.MapW, 24, 160);
        settings.MapH = Math.Clamp(settings.MapH, 16, 120);
        if (settings.Seed == 0) settings.Seed = Random.Shared.Next(1, int.MaxValue);

        var s = new GameState { Settings = settings, W = settings.MapW, H = settings.MapH, Balance = balance ?? new BalanceConfig() };
        s.Rng = (ulong)settings.Seed * 0x9E3779B97F4A7C15UL | 1;
        var grid = new HexGrid(s.W, s.H);
        List<int>? starts = null;
        for (int attempt = 0; attempt < 20 && starts == null; attempt++)
        {
            s.Tiles = MapGen.Generate(s.W, s.H, settings.Seed + attempt * 7919, grid);
            try
            {
                var st = MapGen.StartPositions(s.Tiles, grid, players.Count, settings.Seed + attempt);
                bool spread = true;
                for (int i = 0; i < st.Count && spread; i++)
                    for (int j = i + 1; j < st.Count; j++)
                        if (grid.Distance(st[i], st[j]) < 6) { spread = false; break; }
                if (spread) starts = st;
            }
            catch (InvalidOperationException) { }
        }
        if (starts == null) throw new InvalidOperationException("Не удалось сгенерировать карту: слишком мало суши для такого числа игроков");

        for (int i = 0; i < players.Count; i++)
            s.Players.Add(new Player
            {
                Id = i, Name = players[i].name, IsAI = players[i].ai,
                Color = Rules.PlayerColors[i % Rules.PlayerColors.Length], KnownTiles = new ulong[grid.Count],
            });
        s.Players.Add(new Player
        {
            Id = players.Count, Name = "Вольные города", Neutral = true, IsAI = true,
            Color = Rules.NeutralColor, KnownTiles = new ulong[grid.Count],
        });

        var g = new Game(s);
        for (int i = 0; i < players.Count; i++)
        {
            g.AddUnit(i, Rules.UnitIndex("settler"), starts[i]);
            g.AddUnit(i, Rules.UnitIndex("warrior"), starts[i]);
        }
        AntiSnowball.Recompute(g);
        g.StartFirstTurn();
        return g;
    }

    // ------------------------------------------------------------ helpers
    public Player P(int id) => S.Players[id];
    public IEnumerable<Player> Active => S.Players.Where(p => p.Alive && !p.Neutral);
    public Player Neutral => S.Players[^1];
    public IEnumerable<Unit> UnitsOf(int pid) => S.Units.Values.Where(u => u.Owner == pid);
    public IEnumerable<City> CitiesOf(int pid) => S.Cities.Values.Where(c => c.Owner == pid);
    public List<Unit> At(int tile) => _at[tile] ?? NoUnits;
    public City? CityAt(int tile) => S.Tiles[tile].CityId >= 0 ? S.Cities[S.Tiles[tile].CityId] : null;

    public int Rand(int n)
    {
        ulong x = S.Rng;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        S.Rng = x;
        return (int)(((x * 2685821657736338717UL) >> 33) % (ulong)Math.Max(1, n));
    }

    public Relation Rel(int a, int b)
    {
        int key = Math.Min(a, b) * 128 + Math.Max(a, b);
        if (!S.Relations.TryGetValue(key, out var r)) S.Relations[key] = r = new Relation();
        return r;
    }

    public bool AtWar(int a, int b) => a != b && (P(a).Neutral || P(b).Neutral || Rel(a, b).AtWar);

    public bool CanEnterTerritory(int pid, int tile)
    {
        int owner = S.Tiles[tile].Owner;
        return owner < 0 || owner == pid || AtWar(pid, owner) || Rel(pid, owner).OpenBorders;
    }

    public double UnitStr(Unit u) => u.Def.Str * (1 + 0.05 * P(u.Owner).Future[Rules.BranchNano]);
    public bool IsMilitary(Unit u) => u.Def.Role == UnitRole.Military;
    public double Power(int pid) => UnitsOf(pid).Where(IsMilitary).Sum(u => UnitStr(u) * u.Hp / 100.0);

    public Unit AddUnit(int owner, int type, int tile)
    {
        var u = new Unit { Id = S.NextId++, Owner = owner, Type = type, Tile = tile };
        u.Moves = u.MaxMoves;
        S.Units[u.Id] = u;
        (_at[tile] ??= new List<Unit>()).Add(u);
        return u;
    }

    public void RemoveUnit(Unit u)
    {
        S.Units.Remove(u.Id);
        _at[u.Tile]?.Remove(u);
    }

    private void Relocate(Unit u, int to)
    {
        _at[u.Tile]?.Remove(u);
        u.Tile = to;
        (_at[to] ??= new List<Unit>()).Add(u);
    }

    private void Emit(EventKind kind, int a = 0, int b = 0, int c = 0, int d = 0, string s = "", int[]? tiles = null, int only = -1)
        => Events.Add(new GameEvent { Dto = new EventDto { Kind = (byte)kind, A = a, B = b, C = c, D = d, S = s }, Tiles = tiles, OnlyPlayer = only });

    // ----------------------------------------------------------- commands
    /// <summary>Validates and executes a command. Returns null on success or a user-facing error.</summary>
    public string? Execute(int pid, GameCommand c)
    {
        if (pid < 0 || pid >= S.Players.Count) return "Неизвестный игрок";
        var p = S.Players[pid];
        if (!p.Alive || p.Neutral) return "Ваша цивилизация уничтожена";
        var kind = c.Kind;
        if (kind != CmdType.Respond)
        {
            if (!S.Settings.Simultaneous && S.CurrentPlayer != pid) return "Сейчас не ваш ход";
            if (S.Settings.Simultaneous && p.EndedTurn) return "Вы уже завершили ход";
        }
        switch (kind)
        {
            case CmdType.MoveUnit: return CmdMove(p, c.A, c.B);
            case CmdType.FoundCity: return CmdFound(p, c.A, c.S);
            case CmdType.Work: return CmdWork(p, c.A, (WorkKind)c.B);
            case CmdType.Fortify:
            {
                var u = Own(p, c.A, out var err); if (u == null) return err;
                u.Fortified = true; u.Goto = -1; return null;
            }
            case CmdType.Wake:
            {
                var u = Own(p, c.A, out var err); if (u == null) return err;
                u.Fortified = false; u.Goto = -1; u.Work = WorkKind.None; u.WorkTurns = 0; return null;
            }
            case CmdType.Disband:
            {
                var u = Own(p, c.A, out var err); if (u == null) return err;
                RemoveUnit(u); CheckDefeat(pid); return null;
            }
            case CmdType.Upgrade: return CmdUpgrade(p, c.A);
            case CmdType.SetProduction: return CmdProduction(p, c.A, c.S, c.B);
            case CmdType.RemoveQueue:
            {
                var city = OwnCity(p, c.A, out var err); if (city == null) return err;
                if (c.B < 0 || c.B >= city.Queue.Count) return "Нет такой позиции в очереди";
                city.Queue.RemoveAt(c.B); return null;
            }
            case CmdType.Buy: return CmdBuy(p, c.A);
            case CmdType.SetResearch:
            {
                var avail = Rules.AvailableTechs(p.Known, p.Future);
                if (!avail.Any(t => t.Id == c.S)) return "Эта технология сейчас недоступна";
                p.Research = c.S; return null;
            }
            case CmdType.SetSlider:
                if (c.A < 0 || c.A > 100) return "Недопустимое значение";
                p.Slider = c.A / 10 * 10; return null;
            case CmdType.DeclareWar: return CmdDeclareWar(p, c.A);
            case CmdType.Propose: return CmdPropose(p, c.A, (ProposalKind)c.B);
            case CmdType.Respond: return CmdRespond(p, c.A, c.B != 0);
            case CmdType.CancelBorders:
            {
                if (c.A < 0 || c.A >= S.Players.Count || c.A == pid) return "Неизвестный игрок";
                Rel(pid, c.A).OpenBorders = false; return null;
            }
            case CmdType.EndTurn: DoEndTurn(p); return null;
            default: return "Неизвестная команда";
        }
    }

    private Unit? Own(Player p, int unitId, out string? err)
    {
        err = null;
        if (S.Units.TryGetValue(unitId, out var u) && u.Owner == p.Id) return u;
        err = "Это не ваш юнит";
        return null;
    }

    private City? OwnCity(Player p, int cityId, out string? err)
    {
        err = null;
        if (S.Cities.TryGetValue(cityId, out var c) && c.Owner == p.Id) return c;
        err = "Это не ваш город";
        return null;
    }

    // ----------------------------------------------------------- movement
    public List<int>? PathFor(Unit u, int target)
    {
        return Grid.FindPath(u.Tile, target, (from, to) =>
        {
            int c = Rules.MoveCost(S.Tiles[from], S.Tiles[to]);
            if (c < 0 || !CanEnterTerritory(u.Owner, to)) return -1;
            if (to != target)
            {
                var city = CityAt(to);
                if (city != null && city.Owner != u.Owner) return -1;
                var units = At(to);
                for (int i = 0; i < units.Count; i++) if (units[i].Owner != u.Owner) return -1;
            }
            return c;
        });
    }

    private string? CmdMove(Player p, int unitId, int target)
    {
        var u = Own(p, unitId, out var err); if (u == null) return err;
        if (!Grid.Valid(target)) return "Неверная клетка";
        if (u.Tile == target) { u.Goto = -1; return null; }
        if (PathFor(u, target) == null) return "Туда нет пути";
        u.Goto = target; u.Fortified = false; u.Work = WorkKind.None; u.WorkTurns = 0;
        ContinueGoto(u);
        return null;
    }

    private void ContinueGoto(Unit u)
    {
        int guard = 0;
        while (u.Goto >= 0 && u.Moves > 0 && S.Units.ContainsKey(u.Id) && guard++ < 64)
        {
            if (u.Tile == u.Goto) break;
            var path = PathFor(u, u.Goto);
            if (path == null || path.Count == 0 || !Step(u, path[0])) { u.Goto = -1; break; }
        }
        if (u.Tile == u.Goto) u.Goto = -1;
    }

    /// <summary>Moves one tile (or attacks). Returns true if the unit may keep following its route.</summary>
    private bool Step(Unit u, int to)
    {
        int from = u.Tile;
        int cost = Rules.MoveCost(S.Tiles[from], S.Tiles[to]);
        if (cost < 0 || !CanEnterTerritory(u.Owner, to)) return false;
        var others = At(to).Where(x => x.Owner != u.Owner).ToList();
        var city = CityAt(to);
        bool military = IsMilitary(u);

        if (others.Count > 0)
        {
            if (!military || !AtWar(u.Owner, others[0].Owner)) return false;
            var defenders = others.Where(x => x.Def.Str > 0).ToList();
            if (defenders.Count > 0) { Attack(u, to, defenders); return false; }
        }
        if (city != null && city.Owner != u.Owner && (!military || !AtWar(u.Owner, city.Owner))) return false;

        Advance(u, to, cost);
        return city == null || city.Owner == u.Owner;
    }

    /// <summary>Enters a tile that has no enemy defenders: captures civilians and an undefended city.</summary>
    private void Advance(Unit u, int to, int cost)
    {
        int from = u.Tile;
        foreach (var civ in At(to).Where(x => x.Owner != u.Owner).ToList())
        {
            int oldOwner = civ.Owner;
            if (civ.Def.Role == UnitRole.Worker || civ.Def.Role == UnitRole.Settler)
            {
                civ.Owner = u.Owner; civ.Type = Rules.UnitIndex("worker");
                civ.Moves = 0; civ.Goto = -1; civ.Work = WorkKind.None; civ.WorkTurns = 0;
            }
            else RemoveUnit(civ);
            CheckDefeat(oldOwner);
        }
        Relocate(u, to);
        u.Moves = Math.Max(0, u.Moves - cost);
        u.Fortified = false;
        Emit(EventKind.UnitMoved, u.Id, from, to, tiles: new[] { from, to });
        var city = CityAt(to);
        if (city != null && city.Owner != u.Owner) { u.Moves = 0; TransferCity(city, u.Owner, true); }
    }

    public double DefenseStr(Unit d)
    {
        double mod = 1;
        var t = S.Tiles[d.Tile];
        if (t.Relief == Relief.Hills) mod += 0.25;
        if (t.Forest) mod += 0.25;
        if (d.Fortified) mod += 0.25;
        var city = CityAt(d.Tile);
        if (city != null)
        {
            mod += d.Def.CityDefPct / 100.0;
            foreach (var b in city.Buildings) mod += (Rules.Building(b)?.DefPct ?? 0) / 100.0;
        }
        return UnitStr(d) * mod * d.Hp / 100.0;
    }

    private void Attack(Unit att, int tile, List<Unit> defenders)
    {
        var def = defenders.OrderByDescending(DefenseStr).ThenBy(x => x.Id).First();
        double a = UnitStr(att) * att.Hp / 100.0, d = DefenseStr(def);
        int pA = (int)(1000 * a / (a + d));
        int dmgDef = (int)Math.Clamp(20 * (3 * a + d) / (3 * d + a), 6, 60);
        int dmgAtt = (int)Math.Clamp(20 * (3 * d + a) / (3 * a + d), 6, 60);
        while (att.Hp > 0 && def.Hp > 0)
        {
            if (Rand(1000) < pA) def.Hp -= dmgDef; else att.Hp -= dmgAtt;
        }
        bool won = att.Hp > 0;
        int from = att.Tile, attOwner = att.Owner, defOwner = def.Owner;
        Emit(EventKind.Combat, from, tile, attOwner, defOwner, won ? "1" : "0", new[] { from, tile });
        att.Moves = 0; att.Goto = -1; att.Fortified = false;
        if (!won) { RemoveUnit(att); CheckDefeat(attOwner); return; }
        RemoveUnit(def);
        if (!At(tile).Any(x => x.Owner != attOwner && x.Def.Str > 0)) Advance(att, tile, 0);
        CheckDefeat(defOwner);
    }

    // ------------------------------------------------------------- cities
    private static readonly string[] CityNames =
    {
        "Аркона", "Белогорье", "Вышгород", "Гардарика", "Дальний", "Ельник", "Железногорск", "Заречье", "Искра", "Каменец",
        "Лукоморье", "Медвежий", "Новоград", "Озёрск", "Порог", "Рассвет", "Светлояр", "Тихая Гавань", "Усть-Камень", "Форпост",
        "Хольмгард", "Царьград", "Чернолесье", "Широкий Дол", "Щит", "Эдем", "Юрьев", "Ясный", "Альтаир", "Борей",
        "Вега", "Гелиос", "Денеб", "Зенит", "Ирий", "Китеж", "Лада", "Меридиан", "Норд", "Орион",
    };

    public bool CanFoundAt(int pid, int tile)
    {
        var t = S.Tiles[tile];
        if (!t.Passable || t.CityId >= 0) return false;
        if (t.Owner >= 0 && t.Owner != pid) return false;
        foreach (var c in S.Cities.Values) if (Grid.Distance(c.Tile, tile) < Rules.MinCityDistance) return false;
        return true;
    }

    private string? CmdFound(Player p, int unitId, string name)
    {
        var u = Own(p, unitId, out var err); if (u == null) return err;
        if (u.Def.Role != UnitRole.Settler) return "Основать город может только поселенец";
        if (!CanFoundAt(p.Id, u.Tile)) return "Здесь нельзя основать город (слишком близко к другому городу или чужая земля)";
        name = (name ?? "").Trim();
        if (name.Length > 24) name = name[..24];
        if (name.Length == 0 || S.Cities.Values.Any(c => c.Name == name))
        {
            int start = S.Cities.Count * 7 + p.Id * 13;
            name = "";
            for (int i = 0; i < CityNames.Length && name.Length == 0; i++)
            {
                string cand = CityNames[(start + i) % CityNames.Length];
                if (!S.Cities.Values.Any(c => c.Name == cand)) name = cand;
            }
            if (name.Length == 0) name = $"Колония {S.NextId}";
        }
        var city = new City { Id = S.NextId++, Owner = p.Id, Tile = u.Tile, Name = name, FoundedTurn = S.Turn };
        if (!CitiesOf(p.Id).Any()) { city.Capital = true; city.Buildings.Add("palace"); }
        S.Cities[city.Id] = city;
        S.Tiles[u.Tile].CityId = city.Id;
        S.Tiles[u.Tile].Forest = false;
        S.Tiles[u.Tile].Road = true;
        RemoveUnit(u);
        RecomputeBorders();
        Emit(EventKind.CityFounded, city.Id, p.Id, s: name, tiles: new[] { city.Tile });
        return null;
    }

    public bool CanBuild(Player p, City city, string item)
    {
        if (item == Rules.Wealth) return true;
        if (item.StartsWith("u:"))
        {
            int i = Rules.UnitIndex(item[2..]);
            return i >= 0 && p.Has(Rules.Units[i].Tech);
        }
        if (item.StartsWith("b:"))
        {
            var b = Rules.Building(item[2..]);
            return b != null && b.Id != "palace" && p.Has(b.Tech) && !city.Buildings.Contains(b.Id);
        }
        return false;
    }

    private string? CmdProduction(Player p, int cityId, string item, int mode)
    {
        var city = OwnCity(p, cityId, out var err); if (city == null) return err;
        if (!CanBuild(p, city, item)) return "Это нельзя построить в данном городе";
        if (item.StartsWith("b:") && city.Queue.Contains(item)) return "Уже в очереди";
        if (mode == 0)
        {
            if (city.Queue.Count > 0) city.Queue[0] = item; else city.Queue.Add(item);
        }
        else
        {
            if (city.Queue.Count >= Rules.MaxQueue) return "Очередь заполнена";
            city.Queue.Add(item);
        }
        return null;
    }

    private string? CmdBuy(Player p, int cityId)
    {
        var city = OwnCity(p, cityId, out var err); if (city == null) return err;
        if (city.DisorderTurns > 0) return "В городе беспорядки";
        if (city.Queue.Count == 0 || city.Queue[0] == Rules.Wealth) return "Нечего покупать";
        int cost = Rules.ItemCost(city.Queue[0]);
        int price = Rules.BuyPrice(cost, city.Prod);
        if (price <= 0) return "Уже оплачено";
        if (p.Gold < price) return $"Недостаточно золота (нужно {price})";
        p.Gold -= price;
        city.Prod = cost;
        return null;
    }

    private string? CmdWork(Player p, int unitId, WorkKind kind)
    {
        var u = Own(p, unitId, out var err); if (u == null) return err;
        if (u.Def.Role != UnitRole.Worker) return "Улучшать землю могут только рабочие";
        if (kind == WorkKind.None || kind > WorkKind.Road) return "Неизвестное улучшение";
        var t = S.Tiles[u.Tile];
        if (!Rules.WorkFits(t, kind)) return "Здесь это построить нельзя";
        var tech = Rules.WorkTech(kind);
        if (!p.Has(tech)) return $"Нужна технология: {Rules.Tech(tech)?.Name}";
        if (t.Owner != p.Id && !(kind == WorkKind.Road && t.Owner < 0)) return "Улучшать можно только свою территорию";
        u.Work = kind; u.WorkTurns = Rules.WorkTurns(kind); u.Moves = 0; u.Goto = -1; u.Fortified = false;
        return null;
    }

    private string? CmdUpgrade(Player p, int unitId)
    {
        var u = Own(p, unitId, out var err); if (u == null) return err;
        int to = Rules.UpgradeTarget(u.Type, p.Has);
        if (to < 0) return "Этот юнит пока нельзя улучшить";
        if (S.Tiles[u.Tile].Owner != p.Id) return "Улучшать юниты можно только на своей территории";
        if (u.Moves <= 0) return "У юнита не осталось очков движения";
        int price = Rules.UpgradePrice(u.Type, to);
        if (p.Gold < price) return $"Недостаточно золота (нужно {price})";
        p.Gold -= price;
        u.Type = to; u.Moves = 0; u.Goto = -1;
        return null;
    }

    /// <summary>Changes the owner of a city: conquest, or secession to the neutral "free cities".</summary>
    public void TransferCity(City city, int newOwner, bool conquest)
    {
        int old = city.Owner;
        bool wasCapital = city.Capital;
        city.Owner = newOwner; city.Capital = false;
        city.Queue.Clear(); city.Prod = 0; city.Unrest = 0;
        city.Buildings.Remove("palace");
        if (conquest) { city.Pop = Math.Max(1, city.Pop - 1); city.DisorderTurns = 2; city.Culture /= 2; }
        else
        {
            foreach (var u in At(city.Tile).Where(x => x.Owner == old).ToList()) RemoveUnit(u);
            city.DisorderTurns = 0;
        }
        if (!P(newOwner).Neutral && !CitiesOf(newOwner).Any(c => c.Capital)) { city.Capital = true; city.Buildings.Add("palace"); }
        if (wasCapital)
        {
            var next = CitiesOf(old).OrderByDescending(c => c.Pop).ThenBy(c => c.Id).FirstOrDefault();
            if (next != null) { next.Capital = true; next.Buildings.Add("palace"); }
        }
        P(old).KnownCities[city.Id] = CityPublicDto(city);
        RecomputeBorders();
        if (conquest) Emit(EventKind.CityCaptured, city.Id, newOwner, old, s: city.Name);
        else Emit(EventKind.Secession, city.Id, old, s: city.Name);
        CheckDefeat(old);
    }

    private void CheckDefeat(int pid)
    {
        var p = P(pid);
        if (!p.Alive || p.Neutral) return;
        if (CitiesOf(pid).Any() || UnitsOf(pid).Any(u => u.Def.Role == UnitRole.Settler)) return;
        p.Alive = false;
        foreach (var u in UnitsOf(pid).ToList()) RemoveUnit(u);
        S.Proposals.RemoveAll(x => x.From == pid || x.To == pid);
        Emit(EventKind.PlayerDefeated, pid);
    }

    /// <summary>Tiles belong to the nearest city whose cultural radius covers them (ties: more culture).</summary>
    public void RecomputeBorders()
    {
        var bestDist = new int[S.Tiles.Length];
        var bestCul = new int[S.Tiles.Length];
        for (int i = 0; i < S.Tiles.Length; i++) { S.Tiles[i].Owner = -1; bestDist[i] = int.MaxValue; }
        foreach (var c in S.Cities.Values.OrderBy(c => c.Id))
            foreach (int t in Grid.Range(c.Tile, c.Radius))
            {
                int d = Grid.Distance(c.Tile, t);
                if (d < bestDist[t] || (d == bestDist[t] && c.Culture > bestCul[t]))
                {
                    bestDist[t] = d; bestCul[t] = c.Culture; S.Tiles[t].Owner = c.Owner;
                }
            }
    }

    // ---------------------------------------------------------- diplomacy
    private string? CmdDeclareWar(Player p, int target)
    {
        if (target < 0 || target >= S.Players.Count || target == p.Id) return "Неизвестный игрок";
        var q = P(target);
        if (!q.Alive || q.Neutral) return "Этой цивилизации нельзя объявить войну";
        var rel = Rel(p.Id, target);
        if (!rel.Met) return "Вы ещё не встретили эту цивилизацию";
        if (rel.AtWar) return "Вы уже воюете";
        rel.AtWar = true; rel.OpenBorders = false; rel.SinceTurn = S.Turn;
        S.Proposals.RemoveAll(x => (x.From == p.Id && x.To == target) || (x.From == target && x.To == p.Id));
        Emit(EventKind.WarDeclared, p.Id, target);
        return null;
    }

    private string? CmdPropose(Player p, int target, ProposalKind kind)
    {
        if (target < 0 || target >= S.Players.Count || target == p.Id) return "Неизвестный игрок";
        var q = P(target);
        if (!q.Alive || q.Neutral) return "С этой цивилизацией нельзя договориться";
        var rel = Rel(p.Id, target);
        if (!rel.Met) return "Вы ещё не встретили эту цивилизацию";
        if (kind == ProposalKind.Peace && !rel.AtWar) return "Вы не воюете";
        if (kind == ProposalKind.OpenBorders && (rel.AtWar || rel.OpenBorders)) return "Сейчас это невозможно";
        if (kind > ProposalKind.OpenBorders) return "Неизвестное предложение";
        if (S.Proposals.Any(x => x.From == p.Id && x.To == target && x.Kind == kind)) return "Предложение уже отправлено";
        var pr = new Proposal { Id = S.NextId++, From = p.Id, To = target, Kind = kind };
        if (q.IsAI)
        {
            if (SimpleAI.Accepts(this, q, pr)) ApplyProposal(pr);
            else Emit(EventKind.Info, s: $"«{q.Name}» отклоняет ваше предложение", only: p.Id);
            return null;
        }
        S.Proposals.Add(pr);
        Emit(EventKind.Proposal, p.Id, (int)kind, only: target);
        return null;
    }

    private string? CmdRespond(Player p, int proposalId, bool accept)
    {
        var pr = S.Proposals.FirstOrDefault(x => x.Id == proposalId && x.To == p.Id);
        if (pr == null) return "Предложение уже неактуально";
        S.Proposals.Remove(pr);
        if (accept) ApplyProposal(pr);
        else Emit(EventKind.Info, s: $"«{p.Name}» отклоняет ваше предложение", only: pr.From);
        return null;
    }

    private void ApplyProposal(Proposal pr)
    {
        var rel = Rel(pr.From, pr.To);
        if (pr.Kind == ProposalKind.Peace)
        {
            if (!rel.AtWar) return;
            rel.AtWar = false; rel.SinceTurn = S.Turn;
            Emit(EventKind.PeaceMade, pr.From, pr.To);
        }
        else if (!rel.AtWar && !rel.OpenBorders)
        {
            rel.OpenBorders = true;
            Emit(EventKind.BordersOpened, pr.From, pr.To);
        }
    }
}
