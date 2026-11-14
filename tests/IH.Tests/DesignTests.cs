using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class DesignTests
{
    private static string? Cmd(Game g, int pid, CmdType t, int a = 0, int b = 0, int c = 0, string s = "") => g.Execute(pid, new GameCommand(t, a, b, c, s));
    private static void EndTurn(Game g) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }

    private static Game WithCities()
    {
        var g = GameTests.New(2, 0);
        foreach (int pid in new[] { 0, 1 }) Assert.Null(Cmd(g, pid, CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        g.Rel(0, 1).Met = true;
        return g;
    }

    [Fact]
    public void Combat_odds_shown_to_the_player_match_what_the_server_rolls()
    {
        Assert.Equal(0.5, Rules.Duel(4, 4, 100, 100).winChance, 3);
        Assert.True(Rules.Duel(10, 2, 100, 100).winChance > 0.97);
        Assert.True(Rules.Duel(2, 10, 100, 100).winChance < 0.03);
        Assert.True(Rules.Duel(4, 4, 40, 100).winChance < 0.25);          // a wounded attacker
        Assert.Equal(1, Rules.Duel(3, 0, 100, 100).winChance);            // a defenceless target

        // empirical check: many real fights between an archer and a warrior on open ground
        var g = WithCities();
        g.Rel(0, 1).AtWar = true;
        var city = g.CitiesOf(0).Single();
        int a = g.Grid.Range(city.Tile, 2).First(t => g.S.Tiles[t].Passable && g.S.Tiles[t].CityId < 0);
        int b = g.Grid.Nb[a].First(t => g.S.Tiles[t].Passable && g.S.Tiles[t].CityId < 0);
        foreach (int t in new[] { a, b }) { g.S.Tiles[t].Forest = false; g.S.Tiles[t].Relief = Relief.Flat; }
        int wins = 0; const int fights = 500;
        double predicted = 0;
        for (int i = 0; i < fights; i++)
        {
            var att = g.AddUnit(0, Rules.UnitIndex("archer"), a);
            var def = g.AddUnit(1, Rules.UnitIndex("warrior"), b);
            if (i == 0) predicted = Rules.Duel(g.FieldStr(att), g.DefenseStr(def), att.Hp, def.Hp).winChance;
            Assert.Null(Cmd(g, 0, CmdType.MoveUnit, att.Id, b));
            if (g.S.Units.ContainsKey(att.Id)) { wins++; g.RemoveUnit(att); }
            if (g.S.Units.ContainsKey(def.Id)) g.RemoveUnit(def);
            g.Events.Clear();
        }
        Assert.InRange(predicted, 0.55, 0.97); // a contest, not a foregone conclusion
        Assert.InRange(wins / (double)fights, predicted - 0.07, predicted + 0.07);
    }

    [Fact]
    public void The_forecast_on_the_client_uses_the_numbers_the_server_fights_with()
    {
        var g = WithCities();
        g.Rel(0, 1).AtWar = true;
        var city = g.CitiesOf(0).Single();
        int a = g.Grid.Range(city.Tile, 2).First(t => g.S.Tiles[t].Passable && g.S.Tiles[t].CityId < 0);
        int b = g.Grid.Nb[a].First(t => g.S.Tiles[t].Passable && g.S.Tiles[t].CityId < 0);
        g.S.Tiles[a].Forest = false; g.S.Tiles[a].Relief = Relief.Flat;
        g.S.Tiles[b].Forest = true; g.S.Tiles[b].Relief = Relief.Hills;   // the defender sits well
        var att = g.AddUnit(0, Rules.UnitIndex("warrior"), a);
        var def = g.AddUnit(1, Rules.UnitIndex("archer"), b);
        att.Hp = 80; def.Fortified = true;

        // what player 0's client knows: a full snapshot through the same code the server sends with
        var world = new ClientWorld(new GameStartMsg { W = g.S.W, H = g.S.H, YourId = 0, Settings = g.S.Settings, Branches = Rules.Branches.ToArray() });
        world.Apply(new IH.Server.DeltaTracker().Build(g, 0, g.BuildView(0), new List<EventDto>(), -1)!);
        var odds = world.EstimateCombat(world.Units[att.Id], b);
        Assert.NotNull(odds);
        Assert.Equal(def.Id, odds!.Defender.Id);
        Assert.False(odds.Ranged);
        Assert.Equal(g.FieldStr(att) * att.Hp / 100.0, odds.Attack, 6);
        Assert.Equal(g.DefenseStr(def), odds.Defense, 6);
        Assert.Equal(Rules.Duel(odds.Attack, odds.Defense, att.Hp, def.Hp).winChance, odds.Win, 6);
        Assert.Contains("холмы +25%", odds.DefenseNotes);
        Assert.Contains("укреплён +25%", odds.DefenseNotes);
        // nothing to forecast against friends, empty tiles or the unit's own tile
        Assert.Null(world.EstimateCombat(world.Units[att.Id], a));
        g.Rel(0, 1).AtWar = false;
        world.Apply(new IH.Server.DeltaTracker().Build(g, 0, g.BuildView(0), new List<EventDto>(), -1)!);
        Assert.Null(world.EstimateCombat(world.Units[att.Id], b));
    }

    [Fact]
    public void World_milestones_go_to_whoever_gets_there_first()
    {
        var g = WithCities();
        var p = g.P(0); var q = g.P(1);
        Assert.Empty(g.S.Milestones);
        p.Faith = 500; q.Faith = 500;
        Assert.Null(Cmd(g, 0, CmdType.FoundReligion, 0, 0));
        int favor = p.Favor;
        EndTurn(g);
        Assert.Equal(0, g.S.Milestones["religion"]);
        Assert.True(p.Favor >= favor + Rules.MilestoneFavor);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Milestone && e.Dto.S == "religion" && e.Dto.A == 0);
        // the second founder gets a religion, but not the milestone
        Assert.Null(Cmd(g, 1, CmdType.FoundReligion, 1, 1));
        EndTurn(g);
        Assert.Equal(0, g.S.Milestones["religion"]);
        var view = g.BuildView(1).Me;
        Assert.Equal(Rules.Milestones.Length, view.Milestones.Length);
        Assert.Equal(0, view.Milestones[Array.FindIndex(Rules.Milestones, m => m.Id == "religion")]);
        Assert.Contains(-1, view.Milestones);                                 // most are still open
        // another one, reached by the other player
        q.Known.UnionWith(new[] { "agriculture", "pottery", "writing", "mathematics" });
        EndTurn(g);
        Assert.Equal(1, g.S.Milestones["era1"]);
    }

    [Fact]
    public void Congress_votes_on_a_resolution_that_changes_the_rules_for_a_while()
    {
        var g = WithCities();
        var p = g.P(0); var q = g.P(1);
        Assert.Equal("exchange", g.S.Proposed);
        Assert.Null(g.S.Resolution);
        p.Favor = 30; q.Favor = 30;
        Assert.NotNull(Cmd(g, 0, CmdType.VoteResolution, 1, 99));           // more than we have
        Assert.Null(Cmd(g, 0, CmdType.VoteResolution, 1, 20));
        Assert.Null(Cmd(g, 1, CmdType.VoteResolution, 0, 10));
        Assert.Equal(10, p.Favor);
        var me = g.BuildView(0).Me;
        Assert.Equal((20, 10, "exchange", ""), (me.VotesFor, me.VotesAgainst, me.Proposed, me.Resolution));

        // science echo before the resolution passes
        q.TechPoints = 5000; q.SciRate = 100; p.SciRate = 400;
        double before = AntiSnowball.Echo(g, p);
        while (g.S.Turn % Rules.CongressPeriod != 0) EndTurn(g);
        Assert.Equal("exchange", g.S.Resolution);
        Assert.Equal("aid", g.S.Proposed);                                   // the next one is already on the table
        Assert.Equal(0, g.S.VotesFor);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Resolution && e.Dto.A == 1 && e.Dto.S == "exchange");
        q.SciRate = 100; p.SciRate = 400;
        Assert.Equal(before * 2, AntiSnowball.Echo(g, p), 6);

        // "aid" helps only those behind; here it is voted down
        p.Favor = 30; q.Favor = 30;
        Assert.Null(Cmd(g, 0, CmdType.VoteResolution, 0, 20));
        Assert.Null(Cmd(g, 1, CmdType.VoteResolution, 1, 10));
        g.Events.Clear();
        EndTurn(g);
        while (g.S.Turn % Rules.CongressPeriod != 0) EndTurn(g);
        Assert.Null(g.S.Resolution);                                          // rejected: nothing in force
        Assert.Equal("trade", g.S.Proposed);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Resolution && e.Dto.A == 0 && e.Dto.S == "aid");
    }

    [Fact]
    public void Resolutions_have_the_effects_they_promise()
    {
        var g = WithCities();
        var p = g.P(0); var city = g.CitiesOf(0).Single();
        g.RefreshEconomy(p);
        int prod = city.ProdRate; double gold = city.GoldRate;

        g.S.Resolution = "aid";
        p.Lag = 0; g.RefreshEconomy(p);
        Assert.Equal(prod, city.ProdRate);                                    // not behind: no aid
        p.Lag = 0.5; g.RefreshEconomy(p);
        Assert.True(city.ProdRate >= prod && city.ProdRate * 100 >= prod * 110 || prod < 7);

        g.S.Resolution = "trade"; p.Lag = 0;
        g.RefreshEconomy(p);
        Assert.Equal(gold, city.GoldRate, 6);                                 // no partners: no bonus
        g.Rel(0, 1).OpenBorders = true;
        g.RefreshEconomy(p);
        Assert.True(city.GoldRate > gold);

        g.S.Resolution = "peace";
        g.Rel(0, 1).OpenBorders = false; g.Rel(0, 1).AtWar = true; g.Rel(0, 1).SinceTurn = g.S.Turn;
        g.RefreshEconomy(p);
        Assert.Equal(1, p.WarWeariness);                                      // weary from the first day
        g.S.Resolution = null;
        g.RefreshEconomy(p);
        Assert.Equal(0, p.WarWeariness);
    }
}
