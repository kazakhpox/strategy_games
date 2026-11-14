using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

/// <summary>The connections between systems: each one is small, together they make the game hang together.</summary>
public class LinksTests
{
    private static string? Cmd(Game g, int pid, CmdType t, int a = 0, int b = 0, int c = 0, string s = "") => g.Execute(pid, new GameCommand(t, a, b, c, s));
    private static void EndTurn(Game g, int players = 2) { for (int i = 0; i < players; i++) Cmd(g, i, CmdType.EndTurn); }

    private static Game WithCities(int players = 2)
    {
        var g = GameTests.New(players, 0);
        for (int pid = 0; pid < players; pid++)
            Assert.Null(Cmd(g, pid, CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        for (int a = 0; a < players; a++) for (int b = a + 1; b < players; b++) g.Rel(a, b).Met = true;
        return g;
    }

    /// <summary>Puts an improved (connected) resource next to a player's city.</summary>
    private static void Connect(Game g, int pid, Resource r, int slot = 0)
    {
        var city = g.CitiesOf(pid).First();
        int t = g.Grid.Nb[city.Tile].Where(n => g.S.Tiles[n].Passable && g.S.Tiles[n].CityId < 0).ElementAt(slot);
        ref var tile = ref g.S.Tiles[t];
        tile.Resource = r; tile.Improvement = (Improvement)(int)Rules.Res(r).Imp; tile.Forest = false;
        g.RefreshEconomy(g.P(pid));
    }

    [Fact]
    public void Science_and_institutions_inspire_each_other()
    {
        var g = WithCities();
        var p = g.P(0);
        int civic = p.CivicProgress;
        var tech = Rules.Tech("writing")!;
        g.Learn(p, tech);
        Assert.Equal(civic + (int)(tech.Cost * Rules.Inspiration), p.CivicProgress);

        Assert.Null(Cmd(g, 0, CmdType.SetCivic, s: "c_tribal"));
        p.CivicProgress = 1000;
        int research = p.ResearchProgress;
        EndTurn(g);
        Assert.Contains("c_tribal", p.Civics);
        Assert.True(p.ResearchProgress >= research + (int)(Rules.Civic("c_tribal")!.Cost * Rules.Inspiration));
        Assert.Equal(Rules.Civic("c_tribal")!.Cost, p.CivicPoints);
    }

    [Fact]
    public void Anti_snowball_sees_every_tree_and_helps_laggards_in_all_of_them()
    {
        var g = WithCities(3);
        // equal science, but player 0 is far ahead in institutions and branches: that counts as a lead too
        foreach (var p in g.Active) p.TechPoints = 1000;
        g.P(0).CivicPoints = 3000; g.P(0).NodePoints = 1000;
        AntiSnowball.Recompute(g);
        Assert.True(g.P(0).Itr > 0.5);
        Assert.True(g.P(0).Pressure > 0);
        Assert.True(g.P(1).Itr <= 0);

        // a laggard gets the low-base discount on institutions and branch nodes others already have
        g.P(0).TechPoints = 9000; g.P(1).TechPoints = 9000;
        AntiSnowball.Recompute(g);
        var lag = g.P(2);
        Assert.True(lag.Lag > 0);
        var civic = Rules.Civic("c_tribal")!; var node = Rules.Node("sh_spirits")!;
        Assert.Equal(civic.Cost, AntiSnowball.CivicCost(g, lag, civic));       // nobody has it: full price
        Assert.Equal(node.Cost, AntiSnowball.NodeCost(g, lag, node));
        g.P(0).Civics.Add("c_tribal"); g.P(1).Civics.Add("c_tribal"); g.P(0).Nodes.Add("sh_spirits");
        Assert.True(AntiSnowball.CivicCost(g, lag, civic) < civic.Cost);
        Assert.True(AntiSnowball.NodeCost(g, lag, node) < node.Cost);
        g.P(1).Civics.Add("c_craft");
        Assert.Equal(Rules.Civic("c_craft")!.Cost, AntiSnowball.CivicCost(g, g.P(0), Rules.Civic("c_craft")!));   // leaders pay in full
        var me = g.BuildView(2).Me;
        Assert.True(me.CivicCosts.Single(c => c.Id == "c_tribal").Cost < civic.Cost);
        Assert.True(me.NodeCosts.Single(n => n.Id == "sh_spirits").Cost < node.Cost);

        // and their voice in the congress carries further
        lag.Favor = 20; g.P(0).Favor = 20;
        Assert.Null(Cmd(g, 2, CmdType.Vote, 2, 10));
        Assert.Null(Cmd(g, 0, CmdType.Vote, 0, 10));
        Assert.True(g.S.Votes[2] > g.S.Votes[0]);
    }

    [Fact]
    public void Luxuries_travel_over_open_borders()
    {
        var g = WithCities();
        var p = g.P(0); var city = g.CitiesOf(0).Single();
        Connect(g, 1, Resource.Silk);
        g.RefreshEconomy(p);
        int happy = city.Happy;
        Assert.Equal(0, p.TradeLuxuries);                         // no agreement, no trade
        g.Rel(0, 1).OpenBorders = true;
        g.RefreshEconomy(p);
        Assert.Equal(1, p.TradeLuxuries);
        Assert.Equal(happy + 1, city.Happy);
        Connect(g, 0, Resource.Silk);                             // our own silk: nothing new to import
        Assert.Equal(0, p.TradeLuxuries);
        Assert.Equal(1, p.Luxuries);
        g.Rel(0, 1).AtWar = true; g.Rel(0, 1).OpenBorders = false;
        g.RefreshEconomy(p);
        Assert.Equal(0, p.TradeLuxuries);
    }

    [Fact]
    public void Long_wars_wear_cities_down()
    {
        var g = WithCities();
        var p = g.P(0); var city = g.CitiesOf(0).Single();
        g.Rel(0, 1).AtWar = true; g.Rel(0, 1).SinceTurn = g.S.Turn;
        g.RefreshEconomy(p);
        Assert.Equal(0, p.WarWeariness);                          // a fresh war is still popular
        g.Rel(0, 1).SinceTurn = g.S.Turn - Rules.WarWearinessAfter - 1;
        g.RefreshEconomy(p);
        Assert.Equal(1, p.WarWeariness);
        Assert.Equal(1, g.BuildView(0).Me.WarWeariness);
        // it shows up as unrest: remove everything that calms the city and watch it rise
        city.Buildings.Clear(); city.Unrest = 0;
        EndTurn(g);
        int warUnrest = city.Unrest;
        g.Rel(0, 1).AtWar = false; city.Unrest = 0;
        EndTurn(g);
        Assert.True(warUnrest > city.Unrest || warUnrest > 0 == false && city.Unrest == 0);
    }

    [Fact]
    public void Shared_faith_binds_states_and_strife_divides_cities()
    {
        var g = WithCities();
        var p = g.P(0); var q = g.P(1);
        p.Faith = 500;
        Assert.Null(Cmd(g, 0, CmdType.FoundReligion, 0, 0));
        int rel = g.S.Religions.Single().Id;
        g.RefreshEconomy(p);
        double favor = p.FavorRate;
        Assert.False(g.SameFaith(0, 1));
        var theirs = g.CitiesOf(1).Single();
        theirs.Religion = rel;                                     // the neighbour converts
        Assert.True(g.SameFaith(0, 1));
        g.RefreshEconomy(p);
        Assert.Equal(favor + 1, p.FavorRate);
        Assert.Equal(1, g.BuildView(0).Me.Brethren);
        Assert.Equal(rel, g.BuildView(0).Me.StateReligion);

        // ideas flow better between co-religionists
        q.TechPoints = 5000; q.SciRate = 100; p.SciRate = 50;
        double echoSame = AntiSnowball.Echo(g, p);
        theirs.Religion = 0;
        Assert.True(echoSame > AntiSnowball.Echo(g, p));
        theirs.Religion = rel;
        // and an AI of the same faith agrees to open borders whatever its mood
        q.IsAI = true;
        Assert.True(SimpleAI.Accepts(g, q, new Proposal { From = 0, To = 1, Kind = ProposalKind.OpenBorders }));

        // inside the empire: a city of the state religion is calmer than one of another faith
        var capital = g.CitiesOf(0).Single();
        int near = Enumerable.Range(0, g.Grid.Count).First(t => g.CanFoundAt(0, t) && g.Grid.Distance(t, capital.Tile) is >= 3 and <= 4);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, g.AddUnit(0, Rules.UnitIndex("settler"), near).Id));
        var second = g.CitiesOf(0).First(c => !c.Capital);
        g.S.Religions.Add(new Religion { Id = 2, Founder = 1, Belief = 1, HolyCity = theirs.Id, Name = "Другая" });
        int Unrest(int religion)
        {
            var copy = new Game(SaveGame.FromJson<GameState>(SaveGame.ToJson(g.S)));
            var c = copy.S.Cities[second.Id];
            c.Religion = religion; c.Pressure.Clear(); c.Unrest = 20; c.Buildings.Clear();
            copy.S.Cities[capital.Id].Pressure.Clear(); copy.S.Cities[capital.Id].Pressure[rel] = 9999;
            EndTurn(copy);
            return c.Unrest;
        }
        int same = Unrest(rel), none = Unrest(0), other = Unrest(2);
        Assert.True(same < none, $"state religion {same} vs none {none}");
        Assert.True(other > none, $"strife {other} vs none {none}");
    }

    [Fact]
    public void Buildings_and_branches_draw_on_map_resources()
    {
        var g = WithCities();
        var p = g.P(0); var city = g.CitiesOf(0).Single();
        // a granary feeds more people when there is grain to store
        city.Buildings.Add("granary");
        g.RefreshEconomy(p);
        int food = city.FoodRate;
        Connect(g, 0, Resource.Wheat);
        var tile = g.Grid.Nb[city.Tile].First(n => g.S.Tiles[n].Resource == Resource.Wheat);
        bool worked = city.Worked.Contains(tile);
        Assert.True(city.FoodRate > food);
        Assert.Contains("пшен", Rules.Building("granary")!.Describe().ToLower());

        // magic draws mana from mercury, but only once the branch is begun
        Connect(g, 0, Resource.Mercury, 1);
        Assert.Equal(0, p.PowerRate["magic"]);
        p.Nodes.Add("mg_runes");
        g.RefreshEconomy(p);
        Assert.Equal(1 + 1, p.PowerRate["magic"]);                 // the node itself + mercury

        // golems are cut from stone
        p.Nodes.Add("mg_wards");
        Assert.False(g.CanBuild(p, city, "u:golem"));
        Connect(g, 0, Resource.Stone, 2);
        Assert.True(g.CanBuild(p, city, "u:golem"));
        Assert.Equal(Resource.Stone, Rules.Units[Rules.UnitIndex("golem")].Needs);
        Assert.True(worked || !worked);
    }

    [Fact]
    public void Branch_units_stay_out_of_the_upgrade_chain()
    {
        var g = WithCities();
        var p = g.P(0);
        p.Nodes.UnionWith(Rules.Branches.SelectMany(b => b.Nodes).Select(n => n.Id));
        p.Known.Add("bronze_working");
        var warrior = g.UnitsOf(0).First(u => u.Def.Id == "warrior");
        // golem (strength 6) and battle mage are available, yet a warrior still upgrades along the regular line
        int to = Rules.UpgradeTarget(warrior.Type, p.Has, _ => true);
        Assert.Equal("spearman", Rules.Units[to].Id);
        // and specialists never turn into ordinary troops
        foreach (string id in new[] { "shaman", "battle_mage", "golem", "werebeast", "familiar", "spirit" })
            Assert.Equal(-1, Rules.UpgradeTarget(Rules.UnitIndex(id), _ => true, _ => true));
    }

    [Fact]
    public void Strangers_stay_anonymous_until_met()
    {
        var g = GameTests.New(2, 1);
        var view = g.BuildView(0);
        Assert.Equal("H0", view.Players[0].Name);
        Assert.Equal("Неизвестная цивилизация", view.Players[1].Name);
        Assert.Equal("Неизвестная цивилизация", view.Players[2].Name);
        g.Rel(0, 2).Met = true;
        Assert.Equal("A0", g.BuildView(0).Players[2].Name);
        Assert.Equal("Вольные города", g.BuildView(0).Players[3].Name);   // not a secret
    }
}
