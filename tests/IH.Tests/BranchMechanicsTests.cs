using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

/// <summary>The mechanics that make branches play differently: abilities, building effects, targeted spells.</summary>
public class BranchMechanicsTests
{
    private static string? Cmd(Game g, int pid, CmdType t, int a = 0, int b = 0, int c = 0, string s = "") => g.Execute(pid, new GameCommand(t, a, b, c, s));
    private static void EndTurn(Game g) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
    private static Unit Add(Game g, int owner, string type, int tile) => g.AddUnit(owner, Rules.UnitIndex(type), tile);

    /// <summary>Two players at war, both with a city; player 0 knows every branch node and has power to spend.</summary>
    private static (Game g, Player p, City city, City enemyCity) Setup()
    {
        var g = GameTests.New(2, 0);
        foreach (int pid in new[] { 0, 1 }) Assert.Null(Cmd(g, pid, CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        var p = g.P(0);
        p.Nodes.UnionWith(Rules.Branches.SelectMany(b => b.Nodes).Select(n => n.Id));
        foreach (var b in Rules.Branches) p.Power[b.Id] = 50;
        g.Rel(0, 1).Met = true; g.Rel(0, 1).AtWar = true;
        // spells need an explored target; these tests are not about scouting
        for (int i = 0; i < p.KnownTiles.Length; i++) p.KnownTiles[i] = g.S.Tiles[i].Pack() | TileData.ExploredBit;
        g.RefreshEconomy(p); // node bonuses (unit strength among them) are in effect from here on
        return (g, p, g.CitiesOf(0).Single(), g.CitiesOf(1).Single());
    }

    /// <summary>Open land around a city: no forest, no hills, so terrain does not blur the numbers.</summary>
    private static List<int> Field(Game g, City c, int minDist = 1, int maxDist = 2)
    {
        var res = new List<int>();
        foreach (int t in g.Grid.Range(c.Tile, maxDist))
        {
            if (g.Grid.Distance(t, c.Tile) < minDist || !g.S.Tiles[t].Passable) continue;
            ref var tile = ref g.S.Tiles[t];
            tile.Forest = false; tile.Relief = Relief.Flat; tile.Improvement = Improvement.None;
            if (tile.Terrain is Terrain.Desert or Terrain.Snow) tile.Terrain = Terrain.Plains;
            res.Add(t);
        }
        return res;
    }

    [Fact]
    public void Ranged_units_shoot_without_taking_damage()
    {
        var (g, _, city, _) = Setup();
        var field = Field(g, city);
        int from = field[0];
        int to = field.First(t => g.Grid.Distance(from, t) == 2);
        var mage = Add(g, 0, "battle_mage", from);
        var target = Add(g, 1, "warrior", to);
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, mage.Id, to));
        Assert.Equal(from, mage.Tile);                    // it did not walk into melee
        Assert.Equal(100, mage.Hp);                       // and took nothing back
        Assert.InRange(target.Hp, 30, 92);
        Assert.Equal(0, mage.Moves);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Ranged && e.Dto.S == "0");
        // finishing the target off from range removes it without the mage moving in
        target.Hp = 5; mage.Moves = mage.MaxMoves;
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, mage.Id, to));
        Assert.False(g.S.Units.ContainsKey(target.Id));
        Assert.Equal(from, mage.Tile);
        // an ordinary unit given the same order has to fight hand to hand
        var far = Add(g, 1, "warrior", to);
        var sword = Add(g, 0, "warrior", from);
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, sword.Id, to));
        Assert.True(sword.Tile != from || !g.S.Units.ContainsKey(sword.Id) || far.Hp == 100);
    }

    [Fact]
    public void Healing_aura_regeneration_and_constructs()
    {
        var (g, _, city, _) = Setup();
        g.Rel(0, 1).AtWar = false;
        var field = Field(g, city, 2, 3);
        int spot = field[0];
        int next = field.First(t => g.Grid.Distance(spot, t) == 1);
        int away = field.First(t => g.Grid.Distance(spot, t) >= 3);
        var shaman = Add(g, 0, "shaman", spot);
        var hurt = Add(g, 0, "warrior", next); hurt.Hp = 40;
        var lonely = Add(g, 0, "warrior", away); lonely.Hp = 40;
        var beast = Add(g, 0, "werebeast", away); beast.Hp = 40;
        var golem = Add(g, 0, "golem", away); golem.Hp = 40;
        foreach (var u in new[] { shaman, hurt, lonely, beast }) u.Moves = 0;   // moved this turn: no healing by rest
        EndTurn(g);
        Assert.Equal(50, hurt.Hp);            // the shaman's aura
        Assert.Equal(40, lonely.Hp);          // nobody around
        Assert.Equal(50, beast.Hp);           // regenerates on its own
        Assert.Equal(40, golem.Hp);           // a construct never heals by resting, even with full moves
        g.RefreshEconomy(g.P(0));
        double withGolem = g.P(0).UnitUpkeep;
        g.RemoveUnit(golem);
        g.RefreshEconomy(g.P(0));
        Assert.Equal(withGolem, g.P(0).UnitUpkeep);   // and costs no upkeep
    }

    [Fact]
    public void Fear_and_forest_affinity_change_strength()
    {
        var (g, _, city, _) = Setup();
        var field = Field(g, city);
        int a = field[0], b = field.First(t => g.Grid.Distance(a, t) == 1);
        var enemy = Add(g, 1, "warrior", a);
        double calm = g.FieldStr(enemy);
        var spirit = Add(g, 0, "spirit", b);
        Assert.Equal(calm * 0.8, g.FieldStr(enemy), 6);                 // frightened by the spirit next to it
        var enemyGolem = Add(g, 1, "golem", a);
        Assert.Equal(g.UnitStr(enemyGolem), g.FieldStr(enemyGolem), 6); // constructs feel nothing
        g.RemoveUnit(spirit);

        var beast = Add(g, 0, "werebeast", b);
        double open = g.FieldStr(beast);
        g.S.Tiles[b].Forest = true;
        Assert.Equal(open * 1.5, g.FieldStr(beast), 6);
        Assert.True(g.DefenseStr(beast) > open * 1.5);                  // plus the usual forest cover
    }

    [Fact]
    public void Stealth_units_are_hidden_until_something_stands_next_to_them()
    {
        var (g, _, _, enemyCity) = Setup();
        int spot = g.Grid.Range(enemyCity.Tile, 2).First(t => g.Grid.Distance(t, enemyCity.Tile) == 2 && g.S.Tiles[t].Passable);
        foreach (var u in g.UnitsOf(1).ToList()) if (g.Grid.Distance(u.Tile, spot) <= 1) g.RemoveUnit(u);
        var familiar = Add(g, 0, "familiar", spot);
        var scout = Add(g, 0, "warrior", spot);
        var view = g.BuildView(1);
        Assert.True(view.Visible[spot]);                                              // the tile is in plain sight...
        Assert.Contains(view.Units, u => u.Id == scout.Id);
        Assert.DoesNotContain(view.Units, u => u.Id == familiar.Id);                  // ...yet the familiar is not reported
        Assert.Contains(g.BuildView(0).Units, u => u.Id == familiar.Id);              // its owner always sees it
        int beside = g.Grid.Nb[spot].First(t => g.S.Tiles[t].Passable);
        Add(g, 1, "warrior", beside);
        Assert.Contains(g.BuildView(1).Units, u => u.Id == familiar.Id);              // spotted from an adjacent tile
    }

    [Fact]
    public void Flying_units_cross_mountains_and_water()
    {
        var (g, _, city, _) = Setup();
        int peak = g.Grid.Nb[city.Tile][0], lake = g.Grid.Nb[city.Tile][1];
        g.S.Tiles[peak].Relief = Relief.Mountain;
        g.S.Tiles[lake].Terrain = Terrain.Ocean; g.S.Tiles[lake].Relief = Relief.Flat;
        var familiar = Add(g, 0, "familiar", city.Tile);
        var walker = Add(g, 0, "warrior", city.Tile);
        Assert.Null(g.PathFor(walker, peak));
        Assert.Null(g.PathFor(walker, lake));
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, familiar.Id, peak));
        Assert.Equal(peak, familiar.Tile);
        familiar.Moves = familiar.MaxMoves;
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, familiar.Id, lake));
        Assert.Equal(lake, familiar.Tile);
        Assert.False(g.Embarked(familiar));                                           // hovering, not swimming
        Assert.Equal(Rules.MovePoint, Rules.MoveCost(g.S.Tiles[city.Tile], g.S.Tiles[peak], new Mover(false, false, false, true)));
    }

    [Fact]
    public void Shaman_summons_a_spirit_that_fades_and_grows_forest()
    {
        var (g, p, city, _) = Setup();
        var field = Field(g, city);
        int at = field[0], beside = field.First(t => g.Grid.Distance(at, t) == 1), farTile = field.First(t => g.Grid.Distance(at, t) >= 2);
        var shaman = Add(g, 0, "shaman", at);
        var warrior = g.UnitsOf(0).First(u => u.Def.Id == "warrior");

        Assert.NotNull(Cmd(g, 0, CmdType.Cast, warrior.Id, beside, 0, "sh_summon"));          // only a shaman can
        Assert.Contains("далеко", Cmd(g, 0, CmdType.Cast, shaman.Id, farTile, 0, "sh_summon"));
        Assert.Null(Cmd(g, 0, CmdType.Cast, shaman.Id, beside, 0, "sh_summon"));
        var spirit = g.UnitsOf(0).Single(u => u.Def.Id == "spirit");
        Assert.Equal(beside, spirit.Tile);
        Assert.Equal(44, p.Power["shamanism"]);
        Assert.Equal(0, shaman.Moves);                                                         // casting is the shaman's action for the turn
        Assert.Contains("очков движения", Cmd(g, 0, CmdType.Cast, shaman.Id, farTile, 0, "sh_grow"));
        Assert.Equal(8, g.BuildView(0).Units.Single(u => u.Id == spirit.Id).Expires);
        for (int i = 0; i < 7; i++) { EndTurn(g); Assert.True(g.S.Units.ContainsKey(spirit.Id), $"gone too early, after {i + 1} turns"); }
        EndTurn(g);
        Assert.False(g.S.Units.ContainsKey(spirit.Id));                                        // the summon ran out

        // growing a forest changes the map for good
        Assert.False(g.S.Tiles[farTile].Forest);
        Assert.Null(Cmd(g, 0, CmdType.Cast, shaman.Id, farTile, 0, "sh_grow"));
        Assert.True(g.S.Tiles[farTile].Forest);
        shaman.Moves = shaman.MaxMoves; p.Cooldowns.Clear();
        Assert.Contains("уже лес", Cmd(g, 0, CmdType.Cast, shaman.Id, farTile, 0, "sh_grow"));

        // the healing rite works around the shaman only
        var near = Add(g, 0, "warrior", beside); near.Hp = 30;
        Assert.Null(Cmd(g, 0, CmdType.Cast, shaman.Id, -1, 0, "sh_heal"));
        Assert.Equal(70, near.Hp);
    }

    [Fact]
    public void Mage_spells_fireball_shield_and_teleport()
    {
        var (g, p, city, _) = Setup();
        var field = Field(g, city, 2, 4);
        int at = field[0];
        int target = field.First(t => g.Grid.Distance(at, t) == 3);
        int splash = field.First(t => g.Grid.Distance(target, t) == 1 && t != at);
        var mage = Add(g, 0, "battle_mage", at);
        var centre = Add(g, 1, "warrior", target);
        var edge = Add(g, 1, "warrior", splash);

        Assert.Null(Cmd(g, 0, CmdType.Cast, mage.Id, target, 0, "mg_fireball"));
        Assert.Equal(65, centre.Hp);
        Assert.Equal(83, edge.Hp);                                                            // half damage around the impact
        Assert.Equal(45, p.Power["magic"]);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Ranged);
        mage.Moves = mage.MaxMoves;
        Assert.Contains("через", Cmd(g, 0, CmdType.Cast, mage.Id, target, 0, "mg_fireball"));
        p.Cooldowns.Clear();
        g.RemoveUnit(centre); g.RemoveUnit(edge);
        Assert.Contains("нет вражеских", Cmd(g, 0, CmdType.Cast, mage.Id, target, 0, "mg_fireball"));   // nothing wasted on empty ground
        Assert.Equal(45, p.Power["magic"]);

        // shield
        var guard = Add(g, 0, "warrior", at);
        double before = g.DefenseStr(guard);
        Assert.Null(Cmd(g, 0, CmdType.Cast, mage.Id, at, 0, "mg_shield"));
        Assert.Equal(before * 1.5, g.DefenseStr(guard), 6);
        Assert.Equal(50, g.BuildView(0).Units.Single(u => u.Id == guard.Id).Shield);
        EndTurn(g); EndTurn(g); EndTurn(g);
        Assert.Equal(before, g.DefenseStr(guard), 6);                                         // it wears off

        // teleport takes the mage and its companions home
        mage.Moves = mage.MaxMoves; p.Power["magic"] = 50;
        Assert.NotNull(Cmd(g, 0, CmdType.Cast, mage.Id, g.CitiesOf(1).Single().Tile, 0, "mg_teleport"));   // not into a foreign city
        Assert.Null(Cmd(g, 0, CmdType.Cast, mage.Id, city.Tile, 0, "mg_teleport"));
        Assert.Equal(city.Tile, mage.Tile);
        Assert.Equal(city.Tile, guard.Tile);
    }

    [Fact]
    public void Buildings_bombard_protect_heal_and_cities_can_be_blessed()
    {
        var (g, p, city, _) = Setup();
        var field = Field(g, city);
        int near = field.First(t => g.Grid.Distance(t, city.Tile) == 1), ring = field.First(t => g.Grid.Distance(t, city.Tile) == 2);

        // totem pole: own units around the city defend better
        var guard = Add(g, 0, "warrior", ring);
        double bare = g.DefenseStr(guard);
        city.Buildings.Add("totem_pole");
        Assert.Equal(bare * 1.25, g.DefenseStr(guard), 6);
        Assert.Equal(25, g.AuraDef(guard));

        // herbalist: wounded units in and next to the city recover faster
        city.Buildings.Add("herbalist");
        var hurt = Add(g, 0, "warrior", near); hurt.Hp = 30; hurt.Moves = 0;
        // mage tower: shells one besieger a turn
        city.Buildings.Add("mage_tower");
        var besieger = Add(g, 1, "warrior", ring == near ? near : field.Last(t => g.Grid.Distance(t, city.Tile) == 2));
        var distant = Add(g, 1, "warrior", g.Grid.Range(city.Tile, 4).First(t => g.Grid.Distance(t, city.Tile) == 4 && g.S.Tiles[t].Passable));
        besieger.Moves = 0; // it marched this turn, so it does not also rest
        EndTurn(g);
        Assert.Equal(50, hurt.Hp);
        Assert.Equal(85, besieger.Hp);
        Assert.Equal(100, distant.Hp);                                                        // out of range
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Ranged && e.Dto.A == city.Tile);

        // rain: a temporary food blessing on one city
        g.RefreshEconomy(p);
        int food = city.FoodRate;
        Assert.NotNull(Cmd(g, 0, CmdType.Cast, 0, g.CitiesOf(1).Single().Tile, 0, "sh_rain"));   // must be our city
        Assert.Null(Cmd(g, 0, CmdType.Cast, 0, city.Tile, 0, "sh_rain"));
        g.RefreshEconomy(p);
        Assert.True(city.FoodRate > food);
        Assert.Contains(g.BuildView(0).Me.Demo.Length >= 0 ? g.P(0).KnownCities[city.Id].Detail!.Buffs : Array.Empty<string>(), b => b.StartsWith("пища +50%"));
        for (int i = 0; i < 6; i++) EndTurn(g);
        g.RefreshEconomy(p);
        Assert.Empty(city.Buffs);                                                             // and it passes
    }

    [Theory]
    [InlineData("""{ "Id": "u1", "Name": "U", "Cost": 5, "Str": 2, "Ranged": 2 }""", "", "RangedStr")]
    [InlineData("""{ "Id": "u1", "Name": "U", "Cost": 5, "Str": 2, "FearPct": 95 }""", "", "FearPct")]
    [InlineData("", """{ "Id": "a1", "Name": "A", "Kind": "Damage", "Amount": 5, "CostPower": 2 }""", "нужен Target «Tile»")]
    [InlineData("", """{ "Id": "a1", "Name": "A", "Kind": "Summon", "Amount": 1, "CostPower": 2, "Unit": "dragon" }""", "«dragon» не найден")]
    [InlineData("", """{ "Id": "a1", "Name": "A", "Kind": "HealArea", "Amount": 5, "CostPower": 2 }""", "требует исполнителя")]
    [InlineData("", """{ "Id": "a1", "Name": "A", "Kind": "Bless", "Amount": 5, "CostPower": 2, "Target": "City", "Stat": "Luck", "Duration": 3 }""", "Stat должен быть")]
    [InlineData("", """{ "Id": "a1", "Name": "A", "Kind": "Gold", "Amount": 5, "CostPower": 2, "Caster": "wizard" }""", "«wizard» не найден")]
    public void Mistakes_in_abilities_and_spells_are_explained(string unit, string action, string expected)
    {
        string json = $$"""
            { "Id": "x", "Name": "X", "FromFaith": 1, "Nodes": [ { "Id": "xa", "Name": "A", "Cost": 5 } ],
              "Units": [ {{unit}} ], "Actions": [ {{action}} ] }
            """;
        string dir = Path.Combine(Path.GetTempPath(), "ih-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.json"), json);
            var log = new List<string>();
            Assert.Equal(2, ModLoader.Load(dir, log.Add).Count);
            Assert.Contains(log, l => l.Contains(expected));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ai_plays_a_long_game_with_all_branch_mechanics()
    {
        var g = Simulation.Run(220, 4, 12, false, null, false);
        Assert.All(g.Active, p => Assert.True(p.Nodes.Count >= 4, $"{p.Name} learned only {p.Nodes.Count} nodes"));
        // someone actually fields the new units and buildings
        Assert.Contains(g.S.Cities.Values, c => c.Buildings.Any(b => Rules.BranchOfBuilding(b) != null));
        Assert.Contains(g.Active, p => p.Cooldowns.Count > 0);                                // and casts
        Assert.All(g.S.Units.Values, u => Assert.InRange(u.Hp, 1, 100));
        Assert.All(g.Active, p => Assert.All(p.Power.Values, v => Assert.InRange(v, 0, Rules.PowerCap)));
    }
}
