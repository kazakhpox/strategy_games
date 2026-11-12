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
        Diplomacy(g, p);
        Cities(g, p);
        foreach (var u in g.UnitsOf(pid).OrderBy(u => u.Id).ToList())
        {
            if (!g.S.Units.ContainsKey(u.Id) || u.Owner != pid) continue;
            switch (u.Def.Role)
            {
                case UnitRole.Settler: Settler(g, p, u); break;
                case UnitRole.Worker: Worker(g, p, u); break;
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
            .OrderBy(t => AntiSnowball.TechCost(g, p, t) * (100 + (t.Id.GetHashCode(StringComparison.Ordinal) & 0xFFFF ^ p.Id * 7919) % 40))
            .ThenBy(t => t.Id, StringComparer.Ordinal).First();
        Do(g, p, CmdType.SetResearch, s: best.Id);
    }

    private static void Finance(Game g, Player p)
    {
        if (p.GoldRate < 0 && p.Gold < 40 && p.Slider > 0) Do(g, p, CmdType.SetSlider, Math.Max(0, p.Slider - (p.GoldRate < -8 ? 20 : 10)));
        else if ((p.GoldRate > 3 || p.Gold > 200) && p.Slider < 100) Do(g, p, CmdType.SetSlider, p.Slider + 10);
    }

    public static bool Accepts(Game g, Player ai, Proposal pr)
    {
        var rel = g.Rel(pr.From, pr.To);
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
            if (d.Role == UnitRole.Military && p.Has(d.Tech) && d.Str + d.CityDefPct / 50 > Rules.Units[best].Str + Rules.Units[best].CityDefPct / 50)
                best = i;
        }
        return best;
    }

    private static void Cities(Game g, Player p)
    {
        var cities = g.CitiesOf(p.Id).OrderBy(c => c.Id).ToList();
        var units = g.UnitsOf(p.Id).ToList();
        int settlers = units.Count(u => u.Def.Role == UnitRole.Settler) + cities.Count(c => c.Queue.Contains("u:settler"));
        int workers = units.Count(u => u.Def.Role == UnitRole.Worker) + cities.Count(c => c.Queue.Contains("u:worker"));
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
            if (defenders == 0) item = soldier;
            else if (cities.Count + settlers < target && c.Pop >= 2 && BestSite(g, p, c.Tile, 8).tile >= 0) { item = "u:settler"; settlers++; }
            else if (workers < Math.Min(cities.Count, 6)) { item = "u:worker"; workers++; }
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

    private static WorkKind JobFor(Player p, in TileData t, int tile)
    {
        if (t.Improvement != Improvement.None) return WorkKind.None;
        if (Rules.WorkFits(t, WorkKind.Mine) && p.Has("mining")) return WorkKind.Mine;
        bool wantFarm = t.Resource is Resource.Wheat or Resource.Cattle || tile % 3 != 0;
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
                var kind = JobFor(p, tile, t);
                if (kind == WorkKind.None) continue;
                if (g.At(t).Any(x => x.Id != u.Id && x.Def.Role == UnitRole.Worker)) continue;
                int d = g.Grid.Distance(u.Tile, t) * 2 - (c.Worked.Contains(t) ? 3 : 0);
                if (d < bestDist) { bestDist = d; best = t; bestKind = kind; }
            }
        if (best < 0)
        {
            if (p.Has("wheel") && g.S.Tiles[u.Tile].Owner == p.Id && Rules.WorkFits(g.S.Tiles[u.Tile], WorkKind.Road)
                && g.CitiesOf(p.Id).Any(c => g.Grid.Distance(c.Tile, u.Tile) == 1))
                Do(g, p, CmdType.Work, u.Id, (int)WorkKind.Road);
            return;
        }
        if (best == u.Tile) Do(g, p, CmdType.Work, u.Id, (int)bestKind);
        else Do(g, p, CmdType.MoveUnit, u.Id, best);
    }

    private static void Soldier(Game g, Player p, Unit u)
    {
        var grid = g.Grid;
        var here = g.CityAt(u.Tile);
        var enemies = g.Active.Where(q => q.Id != p.Id && g.Rel(p.Id, q.Id).AtWar).Select(q => q.Id).ToHashSet();
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
                if (g.UnitStr(u) * u.Hp / 100.0 >= 0.75 * def && Do(g, p, CmdType.MoveUnit, u.Id, prey.Tile) == null) return;
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
