using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class SharedTests
{
    [Fact]
    public void Wire_roundtrips_every_field_including_negative_and_null()
    {
        var d = new DeltaMsg
        {
            Full = true, Turn = 123456, CurrentPlayer = -1, TimerLeft = 30,
            Tiles = new[] { new TileDto { Idx = 77, Pack = ulong.MaxValue }, new TileDto { Idx = 0, Pack = 5 } },
            Units = new[] { new UnitDto { Id = 5, Owner = 2, Type = 3, Tile = 900, Hp = 55, Moves = 4, Goto = -1, Work = 2, Fortified = true } },
            RemovedUnits = new[] { 1, -2, int.MaxValue, int.MinValue },
            Cities = new[]
            {
                new CityDto { Id = 9, Name = "Тест Ünï", Pop = 7, Detail = new CityDetail { FoodRate = -3, Queue = new[] { "u:warrior", "b:library" }, Worked = new[] { 1, 2 } } },
                new CityDto { Id = 10, Name = "", Detail = null },
            },
            Me = new MeDto { Gold = -5, Itr = -0.37f, Research = "f2_15", Future = new[] { 1, 2, 3, 4 }, Available = new[] { new TechCostDto { Id = "wheel", Cost = 20, BaseCost = 40 } } },
            Events = new[] { new EventDto { Kind = (byte)EventKind.Combat, A = 1, B = 2, C = 3, D = 4, S = "1" } },
        };
        // large enough to be compressed
        d.Tiles = d.Tiles.Concat(Enumerable.Range(0, 500).Select(i => new TileDto { Idx = i, Pack = 12345 })).ToArray();
        var bytes = Codec.Encode(MsgType.Delta, d);
        Assert.Equal(1, bytes[1]); // compressed flag
        var r = Codec.Decode<DeltaMsg>(bytes, 0, bytes.Length);
        Assert.Equal(Wire.ToBytes(d), Wire.ToBytes(r));
        Assert.Null(r.Cities[1].Detail);
        Assert.Equal(-3, r.Cities[0].Detail!.FoodRate);
        Assert.Equal(int.MinValue, r.RemovedUnits[3]);
        Assert.Equal(-0.37f, r.Me!.Itr);
    }

    [Fact]
    public void Corrupt_packets_throw_instead_of_allocating_gigabytes()
    {
        var bad = new byte[] { (byte)MsgType.Delta, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
        Assert.ThrowsAny<Exception>(() => Codec.Decode<DeltaMsg>(bad, 0, bad.Length));
    }

    [Fact]
    public void TileData_pack_roundtrip()
    {
        var t = new TileData { Terrain = Terrain.Tundra, Relief = Relief.Hills, Forest = true, Resource = Resource.Uranium, Improvement = Improvement.Cottage, Road = true, Owner = 11, CityId = 123456 };
        var u = TileData.Unpack(t.Pack() | TileData.VisibleBit | TileData.ExploredBit);
        Assert.Equal(t, u);
        var empty = new TileData { Owner = -1, CityId = -1 };
        Assert.Equal(empty, TileData.Unpack(empty.Pack()));
    }

    [Fact]
    public void Hex_neighbours_are_symmetric_and_at_distance_one()
    {
        var g = new HexGrid(9, 8);
        for (int i = 0; i < g.Count; i++)
        {
            foreach (int n in g.Nb[i])
            {
                Assert.Equal(1, g.Distance(i, n));
                Assert.Contains(i, g.Nb[n]);
            }
            Assert.Equal(g.Nb[i].Length + 1, g.Range(i, 1).Count);
        }
        Assert.Equal(19, g.Range(4 * 9 + 4, 2).Count);
    }

    [Fact]
    public void Pathfinding_prefers_cheap_tiles_and_respects_walls()
    {
        var g = new HexGrid(10, 3);
        var path = g.FindPath(10, 19, (_, to) => g.Y(to) == 1 && g.X(to) == 5 ? -1 : 2);
        Assert.NotNull(path);
        Assert.DoesNotContain(15, path!);
        Assert.Equal(19, path![^1]);
        Assert.Null(g.FindPath(10, 19, (_, to) => g.X(to) == 5 ? -1 : 2));
    }

    [Fact]
    public void Tech_tree_never_runs_dry()
    {
        var known = new HashSet<string>();
        var future = new int[4];
        for (int i = 0; i < 400; i++)
        {
            var avail = Rules.AvailableTechs(known, future);
            Assert.NotEmpty(avail);
            var t = avail.OrderBy(x => x.Cost).First();
            Assert.All(t.Prereqs, p => Assert.True(Rules.HasTech(known, future, p)));
            if (t.Branch >= 0) future[t.Branch] = t.Level; else known.Add(t.Id);
        }
        Assert.Equal(Rules.Techs.Length, known.Count);
        Assert.True(future.Min() > 80);
        Assert.True(Rules.FutureCost(50) > Rules.FutureCost(49));
        Assert.Equal("Квантовые сети II", Rules.Tech("f2_2")!.Name);
        Assert.Null(Rules.Tech("f9_1"));
        Assert.Null(Rules.Tech("f1_0"));
        Assert.All(Rules.Units, u => Assert.True(u.Tech == null || Rules.Tech(u.Tech) != null));
        Assert.All(Rules.Buildings, b => Assert.True(b.Tech == null || Rules.Tech(b.Tech) != null));
        Assert.All(Rules.Techs, t => Assert.All(t.Prereqs, p => Assert.NotNull(Rules.Tech(p))));
    }
}

public class GameTests
{
    public static Game New(int humans, int ai, bool simultaneous = true, int seed = 7, BalanceConfig? bal = null)
    {
        var list = new List<(string, bool)>();
        for (int i = 0; i < humans; i++) list.Add(($"H{i}", false));
        for (int i = 0; i < ai; i++) list.Add(($"A{i}", true));
        return Game.Create(new GameSettings { MapW = 48, MapH = 32, Seed = seed, Simultaneous = simultaneous }, list, bal);
    }

    private static string? Cmd(Game g, int pid, CmdType t, int a = 0, int b = 0, int c = 0, string s = "") => g.Execute(pid, new GameCommand(t, a, b, c, s));

    [Fact]
    public void Settler_founds_capital_and_city_produces_units()
    {
        var g = New(2, 0);
        var settler = g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, settler.Id, s: "Столица"));
        var city = g.CitiesOf(0).Single();
        Assert.True(city.Capital);
        Assert.Contains("palace", city.Buildings);
        Assert.Equal(0, g.S.Tiles[city.Tile].Owner);
        Assert.NotNull(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:tank")); // no tech
        Assert.NotNull(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "b:palace"));
        Assert.Null(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:warrior"));
        Assert.Null(Cmd(g, 0, CmdType.SetResearch, s: "mining"));
        Assert.NotNull(Cmd(g, 0, CmdType.SetResearch, s: "fusion"));
        int before = g.UnitsOf(0).Count();
        for (int i = 0; i < 12; i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Equal(13, g.S.Turn);
        Assert.True(g.UnitsOf(0).Count() > before);
    }

    [Fact]
    public void Server_rejects_illegal_commands()
    {
        var g = New(2, 0);
        var enemyUnit = g.UnitsOf(1).First();
        var mine = g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Military);
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, enemyUnit.Id, mine.Tile));          // not my unit
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, mine.Id, -5));                       // bad tile
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, mine.Id, 999999));
        Assert.NotNull(Cmd(g, 0, CmdType.FoundCity, mine.Id));                          // warrior can't found
        Assert.NotNull(Cmd(g, 0, CmdType.DeclareWar, 1));                               // never met
        Assert.NotNull(Cmd(g, 0, CmdType.DeclareWar, 0));
        Assert.NotNull(Cmd(g, 0, CmdType.DeclareWar, 99));
        Assert.NotNull(Cmd(g, 0, CmdType.SetSlider, 500));
        Assert.NotNull(Cmd(g, 0, CmdType.Buy, 12345));
        Assert.NotNull(g.Execute(0, new GameCommand { Type = 200 }));
        Assert.NotNull(g.Execute(-1, new GameCommand(CmdType.EndTurn)));
        Assert.Null(Cmd(g, 0, CmdType.EndTurn));
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, mine.Id, g.Grid.Nb[mine.Tile][0]));  // already ended turn
    }

    [Fact]
    public void Sequential_mode_enforces_turn_order()
    {
        var g = New(2, 1, simultaneous: false);
        Assert.Equal(0, g.S.CurrentPlayer);
        Assert.NotNull(Cmd(g, 1, CmdType.EndTurn));
        Assert.Null(Cmd(g, 0, CmdType.EndTurn));
        Assert.Equal(1, g.S.CurrentPlayer);
        Assert.Equal(1, g.S.Turn);
        Assert.Null(Cmd(g, 1, CmdType.EndTurn)); // AI (player 2) moves automatically, then a new global turn starts
        Assert.Equal(0, g.S.CurrentPlayer);
        Assert.Equal(2, g.S.Turn);
    }

    [Fact]
    public void Simulation_is_deterministic_and_survives_save_load()
    {
        var a = Simulation.Run(60, 4, 99, false, null, false);
        var b = Simulation.Run(60, 4, 99, false, null, false);
        string ja = SaveGame.ToJson(a.S), jb = SaveGame.ToJson(b.S);
        Assert.Equal(ja, jb);

        var loaded = new Game(SaveGame.FromJson<GameState>(ja));
        Assert.Equal(ja, SaveGame.ToJson(loaded.S));
        for (int i = 0; i < 25; i++) { a.StepAiOnly(); loaded.StepAiOnly(); }
        Assert.Equal(SaveGame.ToJson(a.S), SaveGame.ToJson(loaded.S));
        Assert.True(a.S.Cities.Count >= 4);
    }

    [Fact]
    public void Combat_resolves_and_cities_can_be_captured()
    {
        var g = New(2, 0);
        foreach (int pid in new[] { 0, 1 })
            Assert.Null(Cmd(g, pid, CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        var target = g.CitiesOf(1).Single();
        g.Rel(0, 1).Met = true;
        Assert.Null(Cmd(g, 0, CmdType.DeclareWar, 1));
        // drop an overwhelming army next to the enemy capital
        int spot = g.Grid.Nb[target.Tile].First(t => g.S.Tiles[t].Passable);
        for (int i = 0; i < 6; i++) g.AddUnit(0, Rules.UnitIndex("tank"), spot);
        foreach (var u in g.At(spot).ToList())
        {
            if (target.Owner == 0) break;
            Cmd(g, 0, CmdType.MoveUnit, u.Id, target.Tile);
        }
        Assert.Equal(0, target.Owner);
        Assert.False(g.P(1).Alive);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.CityCaptured);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.PlayerDefeated);
        Assert.Empty(g.UnitsOf(1));
    }

    [Fact]
    public void Units_upgrade_for_gold_inside_own_borders()
    {
        var g = New(2, 0);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler).Id));
        var warrior = g.UnitsOf(0).First(u => u.Def.Id == "warrior");
        Assert.NotNull(Cmd(g, 0, CmdType.Upgrade, warrior.Id));                 // nothing better is known yet
        g.P(0).Known.Add("bronze_working"); g.P(0).Known.Add("iron_working");
        int to = Rules.UpgradeTarget(warrior.Type, g.P(0).Has);
        Assert.Equal("swordsman", Rules.Units[to].Id);                           // best same-speed unit, not the horseman
        g.P(0).Gold = 5;
        Assert.NotNull(Cmd(g, 0, CmdType.Upgrade, warrior.Id));                 // too poor
        g.P(0).Gold = 500;
        int price = Rules.UpgradePrice(warrior.Type, to);
        Assert.Null(Cmd(g, 0, CmdType.Upgrade, warrior.Id));
        Assert.Equal("swordsman", warrior.Def.Id);
        Assert.Equal(500 - price, g.P(0).Gold);
        Assert.Equal(0, warrior.Moves);
        Assert.NotNull(Cmd(g, 0, CmdType.Upgrade, warrior.Id));                 // already the best
        Assert.Equal(-1, Rules.UpgradeTarget(Rules.UnitIndex("settler"), _ => true));
    }

    [Fact]
    public void Fog_of_war_hides_everything_outside_line_of_sight()
    {
        var g = Simulation.Run(40, 4, 5, false, null, false);
        for (int pid = 0; pid < 4; pid++)
        {
            var view = g.BuildView(pid);
            var p = g.P(pid);
            Assert.All(view.Units, u => Assert.True(view.Visible[u.Tile]));
            Assert.Contains(view.Units, u => u.Owner == pid);
            Assert.All(view.Units.Where(u => u.Owner != pid), u => Assert.Equal(-1, u.Goto));
            // memory never contains details of tiles that were never seen
            for (int i = 0; i < g.S.Tiles.Length; i++)
                if (!view.Visible[i]) Assert.Equal(0UL, p.KnownTiles[i] & TileData.VisibleBit);
            Assert.All(p.KnownCities.Values.Where(c => c.Owner != pid), c => Assert.Null(c.Detail));
            Assert.Contains(p.KnownTiles, k => k == 0); // 40 turns is not enough to see the whole world
            foreach (var o in view.Players.Where(o => !o.Met)) { Assert.Equal(0, o.Techs); Assert.Equal(0, o.Cities); }
        }
    }
}
