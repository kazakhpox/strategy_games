using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class AntiSnowballTests
{
    private static Game Three() => GameTests.New(3, 0);

    [Fact]
    public void Itr_follows_the_documented_formula()
    {
        var g = Three();
        g.P(0).TechPoints = 3000; g.P(1).TechPoints = 1000; g.P(2).TechPoints = 600;
        AntiSnowball.Recompute(g);
        var b = g.Bal;
        // others of P0: {1000, 600} -> median 800
        Assert.Equal((3000 - 800) / (800 + b.ItrSmoothing), g.P(0).Itr, 6);
        // others of P2: {3000, 1000} -> median 2000
        Assert.Equal((600 - 2000) / (2000 + b.ItrSmoothing), g.P(2).Itr, 6);
        double x = g.P(0).Itr - b.LeaderDeadzone;
        Assert.Equal(x / (1 + x), g.P(0).Pressure, 6);
    }

    [Fact]
    public void Leader_is_penalised_and_laggard_is_not()
    {
        var g = Three();
        g.P(0).TechPoints = 5000; g.P(1).TechPoints = 1000; g.P(2).TechPoints = 900;
        AntiSnowball.Recompute(g);
        var leader = g.P(0); var lag = g.P(2);
        Assert.True(leader.Pressure > 0.5);
        Assert.True(leader.SciMult < 0.8 && leader.CulMult < 0.9 && leader.UpkeepMult > 1.5);
        Assert.Equal(0, leader.Lag);
        Assert.Equal(1, lag.SciMult); Assert.Equal(1, lag.UpkeepMult); Assert.Equal(0, lag.Pressure);
        Assert.True(lag.Lag > 0);
        // penalties are bounded: the leader always keeps most of their output
        g.P(0).TechPoints = long.MaxValue / 4;
        AntiSnowball.Recompute(g);
        Assert.True(g.P(0).SciMult >= 1 - g.Bal.SciencePenalty);
    }

    [Fact]
    public void Equal_players_feel_nothing()
    {
        var g = Three();
        foreach (var p in g.Active) p.TechPoints = 2000;
        AntiSnowball.Recompute(g);
        Assert.All(g.Active, p => { Assert.Equal(0, p.Pressure); Assert.Equal(0, p.Lag); Assert.Equal(1, p.SciMult); });
    }

    [Fact]
    public void Catch_up_discount_scales_with_gap_and_spread()
    {
        var g = Three();
        var tech = Rules.Tech("writing")!;
        g.P(0).TechPoints = 6000; g.P(1).TechPoints = 5000; g.P(2).TechPoints = 500;
        AntiSnowball.Recompute(g);
        Assert.Equal(tech.Cost, AntiSnowball.TechCost(g, g.P(2), tech));          // nobody knows it yet
        g.P(0).Known.Add("writing");
        int one = AntiSnowball.TechCost(g, g.P(2), tech);
        g.P(1).Known.Add("writing");
        int two = AntiSnowball.TechCost(g, g.P(2), tech);
        Assert.True(one < tech.Cost && two < one);
        Assert.True(two >= tech.Cost * (1 - g.Bal.MaxDiscount) - 1);
        g.P(1).Known.Add("mining");
        Assert.Equal(Rules.Tech("mining")!.Cost, AntiSnowball.TechCost(g, g.P(0), Rules.Tech("mining")!)); // leaders get no discount
    }

    [Fact]
    public void Science_echo_needs_peaceful_contact_with_a_more_advanced_civilisation()
    {
        var g = Three();
        g.P(0).TechPoints = 6000; g.P(0).SciRate = 100; g.P(2).TechPoints = 500; g.P(2).SciRate = 10;
        Assert.Equal(0, AntiSnowball.Echo(g, g.P(2)));               // no contact
        g.Rel(0, 2).Met = true;
        double contact = AntiSnowball.Echo(g, g.P(2));
        g.Rel(0, 2).OpenBorders = true;
        double borders = AntiSnowball.Echo(g, g.P(2));
        Assert.True(contact > 0 && borders > contact);
        Assert.True(borders <= g.Bal.EchoCap * 10 + 2);
        Assert.Equal(0, AntiSnowball.Echo(g, g.P(0)));               // the leader learns nothing from the laggard
        g.Rel(0, 2).AtWar = true;
        Assert.Equal(0, AntiSnowball.Echo(g, g.P(2)));
    }

    [Fact]
    public void Runaway_leader_suffers_protests_and_separatism()
    {
        var g = Three();
        for (int pid = 0; pid < 3; pid++)
            g.Execute(pid, new GameCommand(CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        // give the leader a sprawling empire and an enormous science lead
        var capital = g.CitiesOf(0).Single();
        int founded = 0;
        foreach (int t in Enumerable.Range(0, g.Grid.Count).OrderByDescending(t => g.Grid.Distance(t, capital.Tile)))
        {
            if (founded >= 4 || !g.CanFoundAt(0, t)) continue;
            var s = g.AddUnit(0, Rules.UnitIndex("settler"), t);
            if (g.Execute(0, new GameCommand(CmdType.FoundCity, s.Id)) == null) founded++;
        }
        Assert.Equal(5, g.CitiesOf(0).Count());
        g.P(0).TechPoints = 200_000;
        int protests = 0, secessions = 0;
        for (int turn = 0; turn < 40; turn++)
        {
            for (int pid = 0; pid < 3; pid++) g.Execute(pid, new GameCommand(CmdType.EndTurn));
            protests += g.Events.Count(e => e.Dto.Type == EventKind.Protest);
            secessions += g.Events.Count(e => e.Dto.Type == EventKind.Secession);
            g.Events.Clear();
        }
        Assert.True(g.P(0).Pressure > g.Bal.SecessionPressure);
        Assert.True(protests > 0, "leader should face protests");
        Assert.True(secessions > 0, "distant cities should secede");
        Assert.True(g.CitiesOf(g.Neutral.Id).Any());
        Assert.True(g.CitiesOf(0).Any(c => c.Capital), "the capital never secedes");
        Assert.Empty(g.S.Cities.Values.Where(c => c.Owner == 1 || c.Owner == 2).Where(c => c.DisorderTurns > 0));
    }

    [Fact]
    public void System_reins_in_a_stronger_player_without_dethroning_them()
    {
        // AI0 gets +50% production and commerce; compare its lead over the median rival with and without the system.
        // Single games are chaotic, so the claim is about the average over several maps.
        static double Lead(Game g) => g.P(0).ScienceLevel /
            AntiSnowball.Median(g.Active.Where(p => p.Id != 0).Select(p => (double)p.ScienceLevel).ToList());
        var seeds = new[] { 1, 2, 3, 4 };
        var on = seeds.Select(s => Lead(Simulation.Run(400, 6, s, false, null, false, 1.5))).ToList();
        var off = seeds.Select(s => Lead(Simulation.Run(400, 6, s, true, null, false, 1.5))).ToList();
        string detail = $"with system {string.Join(" ", on.Select(x => x.ToString("F2")))} vs without {string.Join(" ", off.Select(x => x.ToString("F2")))}";
        Assert.True(on.Average() < off.Average() * 0.8, detail);
        Assert.True(on.Average() > 1.2, "the stronger player should still be ahead on average: " + detail);
        Assert.All(seeds.Select((_, i) => i), i => Assert.True(on[i] < off[i], detail));
    }

    [Fact]
    public void Balance_can_be_switched_off()
    {
        var g = GameTests.New(3, 0, bal: Simulation.Off());
        g.P(0).TechPoints = 50000;
        AntiSnowball.Recompute(g);
        Assert.Equal(1, g.P(0).SciMult); Assert.Equal(1, g.P(0).UpkeepMult);
        g.P(1).Known.Add("writing");
        Assert.Equal(Rules.Tech("writing")!.Cost, AntiSnowball.TechCost(g, g.P(2), Rules.Tech("writing")!));
    }
}
