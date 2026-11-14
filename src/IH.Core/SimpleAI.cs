using IH.Shared;

namespace IH.Core;

/// <summary>
/// A deliberately simple computer opponent. It plays through the same command interface as humans,
/// so it can never perform an action a player could not.
/// </summary>
public static class SimpleAI
{
    public static void TakeTurn(Game g, int pid)
    {
        var p = g.P(pid);
        if (!p.Alive || p.Neutral) return;
        g.UpdateVisibility(p);
        g.RefreshEconomy(p);
        Research(g, p);
        Finance(g, p);
        if (p.Civic == null)
        {
            var civic = Rules.AvailableCivics(p.Civics, p.CivicLevel).OrderBy(c => c.Cost).ThenBy(c => c.Id, StringComparer.Ordinal).First();
            Do(g, p, CmdType.SetCivic, s: civic.Id);
        }
        foreach (var b in Rules.Branches)
        {
            if (p.BranchCurrent.ContainsKey(b.Id)) continue;
            var next = b.Nodes.Where(n => !p.Nodes.Contains(n.Id) && n.Requires.All(r => g.Knows(p, r)))
                .OrderBy(n => n.Cost).ThenBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault();
            if (next != null) Do(g, p, CmdType.SetNode, s: next.Id);
        }
        // rites and spells: use whatever currently does something, keeping a reserve for religion
        foreach (var a in Rules.Actions)
        {
            // empire-level rites; caster spells are decided unit by unit
            if (a.Caster.Length > 0 || a.Target == "Tile" || !p.Has(a.Requires)) continue;
            if (p.Faith < a.CostFaith + (a.CostFaith > 0 ? 40 : 0) || p.Gold < a.CostGold + (a.CostGold > 0 ? 30 : 0)) continue;
            if (a.Kind == "Heal" && g.UnitsOf(p.Id).Count(u => u.Hp <= 60) < 2) continue;
            int target = a.Target == "City" ? g.CitiesOf(p.Id).OrderByDescending(c => c.Pop).ThenBy(c => c.Id).FirstOrDefault()?.Tile ?? -1 : -1;
            Do(g, p, CmdType.Cast, 0, target, 0, a.Id); // refused by the rules when it cannot be paid or would do nothing
        }
        Faith(g, p);
        // shortly before each congress, back ourselves with half of the accumulated favor
        if (g.S.Turn % Rules.CongressPeriod == Rules.CongressPeriod - 2 && p.Favor >= 10)
        {
            Do(g, p, CmdType.Vote, p.Id, p.Favor / 2);
            // the resolution: vote for what helps us, against what mostly helps others
            bool atWar = g.Active.Any(q => q.Id != p.Id && g.Rel(p.Id, q.Id).AtWar);
            bool good = g.S.Proposed switch
            {
                "aid" => p.Lag > 0, "exchange" => p.Itr < 0.1, "peace" => !atWar,
                "trade" => g.Active.Any(q => q.Id != p.Id && g.Rel(p.Id, q.Id).OpenBorders), _ => true,
            };
            if (p.Favor >= 6) Do(g, p, CmdType.VoteResolution, good ? 1 : 0, p.Favor / 3);
        }
        // faith finishes peaceful projects
        foreach (var c in g.CitiesOf(pid).ToList())
            if (c.Queue.Count > 0 && p.Faith >= 60 && Rules.FaithPrice(Rules.ItemCost(c.Queue[0]), c.Prod) is > 0 and var fp && fp <= p.Faith) Do(g, p, CmdType.BuyFaith, c.Id);
        Diplomacy(g, p);
        Cities(g, p);
        foreach (var u in g.UnitsOf(pid).OrderBy(u => u.Id).ToList())
        {
            if (!g.S.Units.ContainsKey(u.Id) || u.Owner != pid) continue;
            switch (u.Def.Role)
            {
                case UnitRole.Settler: Settler(g, p, u); break;
                case UnitRole.Worker: Worker(g, p, u); break;
                case UnitRole.Missionary: Missionary(g, p, u); break;
                default: Soldier(g, p, u); break;
            }
        }
    }

    private static string? Do(Game g, Player p, CmdType t, int a = 0, int b = 0, int c = 0, string s = "")
        => g.Execute(p.Id, new GameCommand(t, a, b, c, s));

    private static void Research(Game g, Player p)
    {
        if (p.Research != null) return;
        var best = Rules.AvailableTechs(p.Known, p.Future)
            .OrderBy(t => AntiSnowball.TechCost(g, p, t) * (100 + (StableHash(t.Id) & 0xFFFF ^ p.Id * 7919) % 40))
            .ThenBy(t => t.Id, StringComparer.Ordinal).First();
        Do(g, p, CmdType.SetResearch, s: best.Id);
    }

    /// <summary>string.GetHashCode is randomised per process; the simulation must replay identically after a restart.</summary>
    private static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char ch in s) h = (h ^ ch) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }

    private static void Faith(Game g, Player p)
    {
        if (g.FoundedBy(p.Id) == null && g.S.Religions.Count < Rules.Beliefs.Length && p.Faith >= Rules.ReligionPrice(g.S.Religions.Count)
            && g.CitiesOf(p.Id).Any(c => c.Capital))
        {
            int name = Enumerable.Range(0, Rules.ReligionNames.Length).First(i => g.S.Religions.All(r => r.Name != Rules.ReligionNames[i]));
            int belief = Enumerable.Range(0, Rules.Beliefs.Length).Select(i => (i + p.Id) % Rules.Beliefs.Length).First(i => g.S.Religions.All(r => r.Belief != i));
            Do(g, p, CmdType.FoundReligion, name, belief);
        }
        // a founder keeps one missionary on the road
        var mine = g.FoundedBy(p.Id);
        if (mine != null && p.Faith >= Rules.MissionaryFaith + 60 && !g.UnitsOf(p.Id).Any(u => u.Def.Role == UnitRole.Missionary)
            && g.S.Cities.Values.Any(c => c.Religion != mine.Id && p.KnownTiles[c.Tile] != 0))
        {
            var home = g.CitiesOf(p.Id).OrderByDescending(c => c.Capital).ThenBy(c => c.Id).FirstOrDefault();
            if (home != null) Do(g, p, CmdType.Recruit, home.Id);
        }
    }

    private static void Missionary(Game g, Player p, Unit u)
    {
        var here = g.CityAt(u.Tile);
        if (here != null && here.Religion != u.Religion) { Do(g, p, CmdType.Preach, u.Id); return; }
        if (u.Goto >= 0 || u.Moves <= 0) return;
        var target = g.S.Cities.Values.Where(c => c.Religion != u.Religion && p.KnownTiles[c.Tile] != 0 && !g.AtWar(p.Id, c.Owner))
            .OrderBy(c => c.Owner == p.Id ? 0 : 1).ThenBy(c => g.Grid.Distance(u.Tile, c.Tile)).ThenBy(c => c.Id).Take(3).ToList();
        foreach (var c in target)
            if (Do(g, p, CmdType.MoveUnit, u.Id, c.Tile) == null) return;
        if (target.Count == 0) Do(g, p, CmdType.Disband, u.Id);
    }

    private static void Finance(Game g, Player p)
    {
        if (p.GoldRate < 0 && p.Gold < 40 && p.Slider > 0) Do(g, p, CmdType.SetSlider, Math.Max(0, p.Slider - (p.GoldRate < -8 ? 20 : 10)));
        else if (((p.GoldRate >= 1 && p.Gold >= 20) || p.Gold > 200) && p.Slider < 100) Do(g, p, CmdType.SetSlider, p.Slider + 10);

        // an army the treasury cannot feed even with science defunded is trimmed, weakest field units first
        if (p.GoldRate < 0 && p.Slider <= 20 && p.UnitUpkeep > 0)
        {
            var extra = g.UnitsOf(p.Id).Where(u => g.IsMilitary(u) && g.CityAt(u.Tile) == null)
                .OrderBy(u => g.UnitStr(u)).ThenBy(u => u.Id).FirstOrDefault();
            if (extra != null) Do(g, p, CmdType.Disband, extra.Id);
        }
        // spare gold modernises the oldest units
        foreach (var u in g.UnitsOf(p.Id).Where(g.IsMilitary).OrderBy(u => u.Def.Str).ThenBy(u => u.Id).Take(3).ToList())
        {
            int to = Rules.UpgradeTarget(u.Type, p.Has, p.HasResource);
            if (to >= 0 && p.Gold >= Rules.UpgradePrice(u.Type, to) + 120) Do(g, p, CmdType.Upgrade, u.Id);
        }
    }

    public static bool Accepts(Game g, Player ai, Proposal pr)
    {
        var rel = g.Rel(pr.From, pr.To);
        if (g.SameFaith(pr.From, pr.To)) return true; // one faith: peace and open borders come easily
        if (pr.Kind == ProposalKind.Peace)
            return g.S.Turn - rel.SinceTurn >= 8 || g.Power(ai.Id) < g.Power(pr.From);
        return !rel.AtWar && (ai.Id * 31 + pr.From * 17 + g.S.Settings.Seed) % 4 != 0;
    }

    private static void Diplomacy(Game g, Player p)
    {
        double mine = g.Power(p.Id);
        foreach (var q in g.Active.ToList())
        {
            if (q.Id == p.Id) continue;
            var rel = g.Rel(p.Id, q.Id);
            if (!rel.Met) continue;
            if (rel.AtWar)
            {
                if (g.S.Turn - rel.SinceTurn >= 12 && (mine < g.Power(q.Id) * 1.2 || g.Rand(100) < 15))
                    Do(g, p, CmdType.Propose, q.Id, (int)ProposalKind.Peace);
            }
            else if (g.S.Turn > 40 && g.S.Turn % 8 == p.Id % 8 && g.S.Turn - rel.SinceTurn > 20
                     && mine > 1.7 * Math.Max(4, g.Power(q.Id)) && g.Rand(100) < 20)
                Do(g, p, CmdType.DeclareWar, q.Id);
            else if (!rel.OpenBorders && g.Rand(100) < 4)
                Do(g, p, CmdType.Propose, q.Id, (int)ProposalKind.OpenBorders);
        }
    }

    private static int BestUnit(Player p)
    {
        int best = Rules.UnitIndex("warrior");
        for (int i = 0; i < Rules.Units.Length; i++)
        {
            var d = Rules.Units[i];
            if (d.Role == UnitRole.Military && !d.Sea && p.Has(d.Tech) && p.HasResource(d.Needs) && d.Str + d.CityDefPct / 50 > Rules.Units[best].Str + Rules.Units[best].CityDefPct / 50)
                best = i;
        }
        return best;
    }

    private static void Cities(Game g, Player p)
    {
        var cities = g.CitiesOf(p.Id).OrderBy(c => c.Id).ToList();
        var units = g.UnitsOf(p.Id).ToList();
        int settlers = units.Count(u => u.Def.Role == UnitRole.Settler) + cities.Count(c => c.Queue.Contains("u:settler"));
        int workers = units.Count(u => u.Def.Role == UnitRole.Worker && !u.Def.Sea) + cities.Count(c => c.Queue.Contains("u:worker"));
        int boats = units.Count(u => u.Def.Id == "work_boat") + cities.Count(c => c.Queue.Contains("u:work_boat"));
        int military = units.Count(g.IsMilitary);
        bool war = g.Active.Any(q => q.Id != p.Id && g.Rel(p.Id, q.Id).AtWar);
        int target = Math.Min(3 + g.S.Turn / 30, 8);
        string soldier = "u:" + Rules.Units[BestUnit(p)].Id;

        foreach (var c in cities)
        {
            if (c.Queue.Count > 0 && c.Queue[0] != Rules.Wealth) continue;
            string item;
            int defenders = g.At(c.Tile).Count(g.IsMilitary);
            var building = Rules.Buildings
                .Where(b => g.CanBuild(p, c, "b:" + b.Id) && b.Cost <= 40 + c.ProdRate * 40)
                .OrderBy(b => b.Cost).FirstOrDefault();
            bool broke = p.GoldRate < 0 && p.Slider <= 40;
            var money = broke ? Rules.Buildings.Where(b => (b.GoldPct > 0 || b.MaintCutPct > 0) && g.CanBuild(p, c, "b:" + b.Id)).OrderBy(b => b.Cost).FirstOrDefault() : null;
            // one fishing boat per coastal city that has an unworked sea resource
            bool wantBoat = p.Has("sailing") && g.Coastal(c) && boats < cities.Count(g.Coastal)
                && g.Grid.Range(c.Tile, Rules.CityWorkRadius).Any(t => g.S.Tiles[t].IsWater && g.S.Tiles[t].Owner == p.Id && JobFor(g, p, g.S.Tiles[t], t) != WorkKind.None);
            if (defenders == 0) item = soldier;
            else if (wantBoat) { item = "u:work_boat"; boats++; }
            else if (money != null) item = "b:" + money.Id;
            else if (broke && !war) item = Rules.Wealth;
            else if (cities.Count + settlers < target && c.Pop >= 2 && BestSite(g, p, c.Tile, 8).tile >= 0) { item = "u:settler"; settlers++; }
            else if (workers < Math.Min(cities.Count, 6)) { item = "u:worker"; workers++; }
            else if (Specialist(g, p, c, units) is { } special) item = special;
            else if (military < cities.Count * (war ? 4 : 2) && (building == null || (c.Id + g.S.Turn) % 2 == 0)) { item = soldier; military++; }
            else if (building != null) item = "b:" + building.Id;
            else if (military < cities.Count * (war ? 5 : 3)) { item = soldier; military++; }
            else item = Rules.Wealth;
            if (c.Queue.Count == 0 || c.Queue[0] != item) Do(g, p, CmdType.SetProduction, c.Id, 0, 0, item);
        }
    }

    private static int SiteScore(Game g, Player p, int tile)
    {
        int score = 0;
        foreach (int n in g.Grid.Range(tile, 2))
        {
            var t = g.S.Tiles[n];
            if (t.Owner >= 0 && t.Owner != p.Id) continue;
            var (f, pr, c) = Rules.TileYield(t);
            score += f * 2 + pr + c;
        }
        return score;
    }

    private static (int tile, int score) BestSite(Game g, Player p, int from, int radius)
    {
        int best = -1, bestScore = 24;
        foreach (int t in g.Grid.Range(from, radius))
        {
            var tile = g.S.Tiles[t];
            if (tile.Terrain == Terrain.Snow || p.KnownTiles[t] == 0 || !g.CanFoundAt(p.Id, t)) continue;
            bool crowded = false;
            foreach (var c in g.S.Cities.Values) if (g.Grid.Distance(c.Tile, t) < 4) { crowded = true; break; }
            if (crowded) continue;
            int score = SiteScore(g, p, t) - g.Grid.Distance(from, t) * 2;
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return (best, bestScore);
    }

    private static void Settler(Game g, Player p, Unit u)
    {
        if (u.Goto >= 0 && g.CanFoundAt(p.Id, u.Goto)) return; // still travelling
        bool canHere = g.CanFoundAt(p.Id, u.Tile);
        if (!g.CitiesOf(p.Id).Any() && canHere) { Do(g, p, CmdType.FoundCity, u.Id); return; }
        var (site, score) = BestSite(g, p, u.Tile, 9);
        if (canHere && (site < 0 || site == u.Tile || SiteScore(g, p, u.Tile) >= score * 0.9))
        {
            Do(g, p, CmdType.FoundCity, u.Id);
            return;
        }
        if (site >= 0 && Do(g, p, CmdType.MoveUnit, u.Id, site) == null) return;
        if (canHere) Do(g, p, CmdType.FoundCity, u.Id);
    }

    private static WorkKind JobFor(Game g, Player p, in TileData raw, int tile)
    {
        var t = g.Seen(p, raw);
        if (t.Resource != Resource.None)
        {
            // a resource wants its own improvement, replacing whatever generic one is there
            var need = Rules.Res(t.Resource).Imp;
            if (Rules.ResourceWorked(t)) return WorkKind.None;
            return Rules.WorkFits(t, need) && p.Has(Rules.WorkTech(need)) ? need : WorkKind.None;
        }
        if (t.Improvement != Improvement.None || t.IsWater) return WorkKind.None;
        if (Rules.WorkFits(t, WorkKind.Mine) && p.Has("mining")) return WorkKind.Mine;
        bool wantFarm = tile % 3 != 0;
        if (wantFarm && Rules.WorkFits(t, WorkKind.Farm) && p.Has("agriculture")) return WorkKind.Farm;
        if (Rules.WorkFits(t, WorkKind.Cottage) && p.Has("pottery")) return WorkKind.Cottage;
        if (Rules.WorkFits(t, WorkKind.Farm) && p.Has("agriculture")) return WorkKind.Farm;
        return WorkKind.None;
    }

    private static void Worker(Game g, Player p, Unit u)
    {
        if (u.Work != WorkKind.None || u.Goto >= 0) return;
        int best = -1, bestDist = int.MaxValue;
        WorkKind bestKind = WorkKind.None;
        foreach (var c in g.CitiesOf(p.Id))
            foreach (int t in g.Grid.Range(c.Tile, Rules.CityWorkRadius))
            {
                var tile = g.S.Tiles[t];
                if (tile.Owner != p.Id) continue;
                if (tile.IsWater != u.Def.Sea) continue; // boats work the sea, workers the land
                var kind = JobFor(g, p, tile, t);
                if (kind == WorkKind.None) continue;
                if (g.At(t).Any(x => x.Id != u.Id && x.Def.Role == UnitRole.Worker)) continue;
                int d = g.Grid.Distance(u.Tile, t) * 2 - (c.Worked.Contains(t) ? 3 : 0);
                if (d < bestDist) { bestDist = d; best = t; bestKind = kind; }
            }
        if (best < 0)
        {
            if (!u.Def.Sea && p.Has("wheel") && g.S.Tiles[u.Tile].Owner == p.Id && Rules.WorkFits(g.S.Tiles[u.Tile], WorkKind.Road)
                && g.CitiesOf(p.Id).Any(c => g.Grid.Distance(c.Tile, u.Tile) == 1))
                Do(g, p, CmdType.Work, u.Id, (int)WorkKind.Road);
            return;
        }
        if (best == u.Tile) Do(g, p, CmdType.Work, u.Id, (int)bestKind);
        else Do(g, p, CmdType.MoveUnit, u.Id, best);
    }

    /// <summary>Keeps a few support and caster units around: healers, ranged units, anything that performs actions.</summary>
    private static string? Specialist(Game g, Player p, City c, List<Unit> units)
    {
        int want = 1 + g.CitiesOf(p.Id).Count() / 3;
        foreach (var d in Rules.Units)
        {
            bool special = d.X.HealAura > 0 || d.X.Ranged > 0 || Rules.Actions.Any(a => a.Caster == d.Id);
            if (!special || !g.CanBuild(p, c, "u:" + d.Id)) continue;
            if (units.Count(u => u.Def.Id == d.Id) + g.CitiesOf(p.Id).Count(x => x.Queue.Contains("u:" + d.Id)) < want) return "u:" + d.Id;
        }
        return null;
    }

    private static bool TryCast(Game g, Player p, Unit u, HashSet<int> enemies)
    {
        if (u.Moves <= 0) return false;
        var grid = g.Grid;
        foreach (var a in Rules.Actions)
        {
            if (a.Caster != u.Def.Id || !p.Has(a.Requires)) continue;
            int range = Math.Max(1, a.Range), target = -1;
            switch (a.Kind)
            {
                case "HealArea":
                    if (!grid.Range(u.Tile, range).Any(t => g.At(t).Any(f => f.Owner == p.Id && f.Hp <= 60))) continue;
                    break;
                case "Damage":
                    target = grid.Range(u.Tile, range)
                        .Select(t => (t, n: g.At(t).Count(e => enemies.Contains(e.Owner))))
                        .Where(x => x.n > 0 && p.KnownTiles[x.t] != 0).OrderByDescending(x => x.n).ThenBy(x => x.t).Select(x => x.t).DefaultIfEmpty(-1).First();
                    if (target < 0) continue;
                    break;
                case "Summon":
                    if (!g.S.Units.Values.Any(e => enemies.Contains(e.Owner) && grid.Distance(e.Tile, u.Tile) <= 4)) continue;
                    target = grid.Range(u.Tile, range).Where(t => g.S.Tiles[t].Passable && g.CityAt(t) == null && !g.At(t).Any(x => x.Owner != p.Id))
                        .DefaultIfEmpty(-1).First();
                    if (target < 0) continue;
                    break;
                case "Shield":
                    if (!g.S.Units.Values.Any(e => enemies.Contains(e.Owner) && grid.Distance(e.Tile, u.Tile) <= 3)) continue;
                    target = u.Tile;
                    break;
                case "Forest":
                    if (g.Rand(100) >= 12) continue;
                    target = grid.Range(u.Tile, range).Where(t =>
                    {
                        var tile = g.S.Tiles[t];
                        return tile.Owner == p.Id && tile.Passable && !tile.Forest && tile.CityId < 0 && tile.Improvement == Improvement.None
                               && tile.Resource == Resource.None && tile.Terrain is Terrain.Grassland or Terrain.Plains or Terrain.Tundra;
                    }).DefaultIfEmpty(-1).First();
                    if (target < 0) continue;
                    break;
                default: continue; // teleports and the like need a plan the simple AI does not make
            }
            if (Do(g, p, CmdType.Cast, u.Id, target, 0, a.Id) == null) return true;
        }
        return false;
    }

    private static void Soldier(Game g, Player p, Unit u)
    {
        var grid = g.Grid;
        var here = g.CityAt(u.Tile);
        var enemies = g.Active.Where(q => q.Id != p.Id && g.Rel(p.Id, q.Id).AtWar).Select(q => q.Id).ToHashSet();
        if (TryCast(g, p, u, enemies)) return;
        bool military = g.IsMilitary(u);

        // garrison duty: the oldest units in a city stay
        if (military && here != null && here.Owner == p.Id)
        {
            int need = enemies.Count > 0 ? 2 : 1;
            int rank = g.At(u.Tile).Where(g.IsMilitary).OrderBy(x => x.Id).ToList().FindIndex(x => x.Id == u.Id);
            if (rank < need) { if (!u.Fortified) Do(g, p, CmdType.Fortify, u.Id); return; }
        }
        if (u.Moves <= 0) return;

        if (military && enemies.Count > 0)
        {
            Unit? prey = null; int bestD = 4;
            foreach (var e in g.S.Units.Values)
            {
                if (!enemies.Contains(e.Owner) || p.KnownTiles[e.Tile] == 0) continue;
                int d = grid.Distance(u.Tile, e.Tile);
                if (d < bestD) { bestD = d; prey = e; }
            }
            if (prey != null)
            {
                double def = prey.Def.Str > 0 ? g.DefenseStr(prey) : 0;
                bool shoot = u.Def.X.Ranged > 0 && bestD <= u.Def.X.Ranged; // no risk in shooting
                if ((shoot || g.UnitStr(u) * u.Hp / 100.0 >= 0.75 * def) && Do(g, p, CmdType.MoveUnit, u.Id, prey.Tile) == null) return;
            }
            if (u.Hp >= 60)
            {
                var target = g.S.Cities.Values.Where(c => enemies.Contains(c.Owner) && p.KnownTiles[c.Tile] != 0)
                    .OrderBy(c => grid.Distance(u.Tile, c.Tile)).ThenBy(c => c.Id).FirstOrDefault();
                if (target != null && grid.Distance(u.Tile, target.Tile) <= 16)
                {
                    if (grid.Distance(u.Tile, target.Tile) == 1)
                    {
                        var def = g.At(target.Tile).Where(x => x.Def.Str > 0).OrderByDescending(g.DefenseStr).FirstOrDefault();
                        if (def == null || g.UnitStr(u) * u.Hp / 100.0 >= 0.6 * g.DefenseStr(def))
                            Do(g, p, CmdType.MoveUnit, u.Id, target.Tile);
                        else if (!u.Fortified) Do(g, p, CmdType.Fortify, u.Id);
                        return;
                    }
                    if (Do(g, p, CmdType.MoveUnit, u.Id, target.Tile) == null) return;
                }
            }
        }

        if (military)
        {
            var open = g.CitiesOf(p.Id).Where(c => !g.At(c.Tile).Any(g.IsMilitary))
                .OrderBy(c => grid.Distance(u.Tile, c.Tile)).ThenBy(c => c.Id).FirstOrDefault();
            if (open != null && Do(g, p, CmdType.MoveUnit, u.Id, open.Tile) == null) return;
        }

        if (u.Goto >= 0) return;
        if (!military || (u.Id % 3 != 0 && g.S.Turn < 150))
        {
            // explore: nearest known passable tile bordering the unknown
            var frontier = new List<(int d, int tile)>();
            foreach (int t in grid.Range(u.Tile, 10))
            {
                if (t == u.Tile || p.KnownTiles[t] == 0 || !g.S.Tiles[t].Passable || !g.CanEnterTerritory(p.Id, t)) continue;
                bool edge = false;
                foreach (int n in grid.Nb[t]) if (p.KnownTiles[n] == 0) { edge = true; break; }
                if (edge) frontier.Add((grid.Distance(u.Tile, t) * 8 + g.Rand(8), t));
            }
            frontier.Sort();
            for (int i = 0; i < frontier.Count && i < 4; i++)
                if (Do(g, p, CmdType.MoveUnit, u.Id, frontier[i].tile) == null) return;
        }

        if (here != null && here.Owner == p.Id) { if (!u.Fortified) Do(g, p, CmdType.Fortify, u.Id); return; }
        var home = g.CitiesOf(p.Id).OrderBy(c => grid.Distance(u.Tile, c.Tile)).ThenBy(c => c.Id).FirstOrDefault();
        if (home != null) Do(g, p, CmdType.MoveUnit, u.Id, home.Tile);
    }
}
