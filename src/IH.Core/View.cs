using IH.Shared;

namespace IH.Core;

/// <summary>What one player is allowed to know right now.</summary>
public sealed class PlayerView
{
    public bool[] Visible = Array.Empty<bool>();
    public List<UnitDto> Units = new();
    public PlayerDto[] Players = Array.Empty<PlayerDto>();
    public MeDto Me = new();
}

public sealed partial class Game
{
    /// <summary>Recomputes line of sight and updates the player's fog-of-war memory and first contacts.</summary>
    public bool[] UpdateVisibility(Player p)
    {
        var vis = new bool[S.Tiles.Length];
        if (!p.Alive && !p.Neutral) Array.Fill(vis, true); // defeated players spectate
        else
        {
            foreach (var u in UnitsOf(p.Id))
            {
                int sight = u.Def.Sight + (S.Tiles[u.Tile].Relief == Relief.Hills ? 1 : 0);
                foreach (int t in Grid.Range(u.Tile, sight)) vis[t] = true;
            }
            for (int i = 0; i < S.Tiles.Length; i++)
            {
                if (S.Tiles[i].Owner != p.Id) continue;
                vis[i] = true;
                foreach (int n in Grid.Nb[i]) vis[n] = true;
            }
        }

        for (int i = 0; i < vis.Length; i++)
        {
            if (!vis[i]) { p.KnownTiles[i] &= ~TileData.VisibleBit; continue; }
            var t = S.Tiles[i];
            p.KnownTiles[i] = Seen(p, t).Pack() | TileData.ExploredBit | TileData.VisibleBit;
            if (t.Owner >= 0 && t.Owner != p.Id) Meet(p.Id, t.Owner);
            if (t.CityId >= 0)
            {
                var c = S.Cities[t.CityId];
                p.KnownCities[c.Id] = c.Owner == p.Id ? CityOwnDto(c) : CityPublicDto(c);
            }
            var here = At(i);
            for (int k = 0; k < here.Count; k++) if (here[k].Owner != p.Id) Meet(p.Id, here[k].Owner);
        }
        return vis;
    }

    private void Meet(int a, int b)
    {
        if (P(a).Neutral || P(b).Neutral || !P(a).Alive) return;
        Rel(a, b).Met = true;
    }

    public CityDto CityPublicDto(City c) => new()
    {
        Id = c.Id, Owner = c.Owner, Tile = c.Tile, Pop = c.Pop, Radius = c.Radius, Name = c.Name,
        Capital = c.Capital, Disorder = c.DisorderTurns > 0, Religion = c.Religion,
    };

    private CityDto CityOwnDto(City c)
    {
        var d = CityPublicDto(c);
        d.Detail = new CityDetail
        {
            Food = c.Food, FoodNeed = Rules.FoodToGrow(c.Pop), FoodRate = c.FoodRate, Prod = c.Prod, ProdRate = c.ProdRate,
            Commerce = c.Commerce, Science = (int)Math.Round(c.SciRate), Gold = (int)Math.Round(c.GoldRate),
            Culture = c.Culture, CultureRate = Math.Max(1, (int)Math.Round(c.CulRate)), Happy = c.Happy, Unhappy = c.Unhappy,
            Unrest = c.Unrest, DisorderTurns = c.DisorderTurns, Maint = (int)Math.Round(c.Maint + c.BuildingUpkeep),
            Queue = c.Queue.ToArray(), Buildings = c.Buildings.OrderBy(x => x).ToArray(), Worked = c.Worked.ToArray(),
            Pressure = c.Pressure.OrderByDescending(x => x.Value).SelectMany(x => new[] { x.Key, x.Value }).ToArray(),
        };
        return d;
    }

    public static UnitDto ToDto(Unit u, bool own) => new()
    {
        Id = u.Id, Owner = u.Owner, Type = u.Type, Tile = u.Tile, Hp = u.Hp, Moves = own ? u.Moves : 0,
        WorkTurns = own ? u.WorkTurns : 0, Goto = own ? u.Goto : -1, Work = (byte)u.Work, Fortified = u.Fortified,
    };

    public int Score(Player p)
    {
        int land = 0;
        for (int i = 0; i < S.Tiles.Length; i++) if (S.Tiles[i].Owner == p.Id) land++;
        return CitiesOf(p.Id).Sum(c => c.Pop * 2 + 5) + p.TechCount * 3 + land / 2;
    }

    /// <summary>Builds the fog-of-war filtered view the server is allowed to send to this player.</summary>
    public PlayerView BuildView(int pid)
    {
        var p = P(pid);
        if (p.Alive && !p.Neutral) RefreshEconomy(p);
        var view = new PlayerView { Visible = UpdateVisibility(p) };

        foreach (var u in S.Units.Values)
            if (view.Visible[u.Tile]) view.Units.Add(ToDto(u, u.Owner == pid));

        view.Players = S.Players.Select(o =>
        {
            var rel = o.Id == pid ? null : Rel(pid, o.Id);
            bool met = o.Id == pid || rel!.Met || !p.Alive;
            return new PlayerDto
            {
                Id = o.Id, Name = o.Name, Color = o.Color, AI = o.IsAI, Alive = o.Alive, Neutral = o.Neutral,
                Connected = o.Connected, EndedTurn = o.EndedTurn, Met = met,
                Score = met && !o.Neutral ? Score(o) : 0, Cities = met ? CitiesOf(o.Id).Count() : 0,
                Era = met ? Rules.EraOf(o.Known, o.Future) : 0, Techs = met ? o.TechCount : 0,
                AtWar = o.Id != pid && AtWar(pid, o.Id), OpenBorders = rel != null && rel.OpenBorders,
            };
        }).ToArray();

        view.Me = new MeDto
        {
            Gold = p.Gold, GoldRate = p.GoldRate, SciRate = (int)Math.Round(p.SciRate + p.EchoBonus), Slider = p.Slider,
            ResearchProgress = p.ResearchProgress, EchoBonus = (int)Math.Round(p.EchoBonus),
            Income = (int)Math.Round(p.Income), UnitUpkeep = (int)Math.Round(p.UnitUpkeep),
            BuildingUpkeep = (int)Math.Round(p.BuildingUpkeep), CityMaint = (int)Math.Round(p.CityMaint),
            RoadUpkeep = (int)Math.Round(p.RoadUpkeep), StrainCost = (int)Math.Round(p.StrainCost),
            Research = p.Research ?? "", Known = p.Known.OrderBy(x => x).ToArray(), Future = (int[])p.Future.Clone(),
            Itr = (float)p.Itr, Pressure = (float)p.Pressure, Lag = (float)p.Lag, SciMult = (float)p.SciMult,
            CulMult = (float)p.CulMult, UpkeepMult = (float)p.UpkeepMult, Sprawl = (float)p.Sprawl,
            Available = Rules.AvailableTechs(p.Known, p.Future)
                .Select(t => new TechCostDto { Id = t.Id, BaseCost = t.Cost, Cost = AntiSnowball.TechCost(this, p, t) }).ToArray(),
            Proposals = S.Proposals.Where(x => x.To == pid || x.From == pid)
                .Select(x => new ProposalDto { Id = x.Id, From = x.From, To = x.To, Kind = (byte)x.Kind }).ToArray(),
            Demo = Demographics(p),
            Nodes = p.Nodes.OrderBy(x => x).ToArray(),
            Cooldowns = Rules.Actions.Select(a => Math.Max(0, p.Cooldowns.GetValueOrDefault(a.Id) - S.Turn)).ToArray(),
            BranchCurrent = Rules.Branches.Select(b => p.BranchCurrent.GetValueOrDefault(b.Id) ?? "").ToArray(),
            BranchProgress = Rules.Branches.Select(b => p.BranchProgress.GetValueOrDefault(b.Id)).ToArray(),
            BranchRate = Rules.Branches.Select(b => (int)Math.Round(p.BranchRate.GetValueOrDefault(b.Id))).ToArray(),
            Civic = p.Civic ?? "", Civics = p.Civics.OrderBy(x => x).ToArray(), CivicProgress = p.CivicProgress, CivicLevel = p.CivicLevel,
            CultureRate = (int)Math.Round(p.CultureRate), MyReligion = FoundedBy(pid)?.Id ?? 0,
            Religions = S.Religions.Select(r => new ReligionDto
            {
                Id = r.Id, Name = r.Name, Founder = r.Founder, Belief = r.Belief, HolyCity = r.HolyCity,
                Cities = S.Cities.Values.Count(c => c.Religion == r.Id),
            }).ToArray(),
            Faith = p.Faith, FaithRate = (int)p.FaithRate, Favor = p.Favor, FavorRate = (int)p.FavorRate, Luxuries = p.Luxuries,
            Chair = S.Chair, CongressIn = Rules.CongressPeriod - S.Turn % Rules.CongressPeriod,
            Resources = p.Resources.ToArray(),
            Votes = S.Players.Select(o => S.Votes.GetValueOrDefault(o.Id)).ToArray(),
        };
        return view;
    }

    private DemoDto[] Demographics(Player me)
    {
        var active = Active.ToList();
        if (!active.Contains(me)) return Array.Empty<DemoDto>();
        var land = new int[S.Players.Count];
        for (int i = 0; i < S.Tiles.Length; i++) if (S.Tiles[i].Owner >= 0) land[S.Tiles[i].Owner]++;
        DemoDto Row(string key, Func<Player, double> f)
        {
            var vals = active.Select(f).ToList();
            double mine = f(me);
            return new DemoDto
            {
                Key = key, Mine = (int)Math.Round(mine), Best = (int)Math.Round(vals.Max()), Worst = (int)Math.Round(vals.Min()),
                Avg = (int)Math.Round(vals.Average()), Rank = 1 + vals.Count(v => v > mine),
            };
        }
        return new[]
        {
            Row("Наука за ход", p => p.SciRate + p.EchoBonus),
            Row("Уровень науки", p => p.ScienceLevel),
            Row("Доход золота", p => p.GoldRate),
            Row("Производство", p => CitiesOf(p.Id).Sum(c => c.ProdRate)),
            Row("Население", p => CitiesOf(p.Id).Sum(c => c.Pop)),
            Row("Территория", p => land[p.Id]),
            Row("Военная мощь", p => Power(p.Id)),
        };
    }
}
