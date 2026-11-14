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
        // built-in content hangs off technologies; branch content hangs off branch nodes
        Assert.All(Rules.Units, u => Assert.True(u.Tech == null || Rules.Tech(u.Tech) != null || Rules.Node(u.Tech) != null));
        Assert.All(Rules.Buildings, b => Assert.True(b.Tech == null || Rules.Tech(b.Tech) != null || Rules.Node(b.Tech) != null));
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
    public void Move_order_without_movement_left_is_refused_not_queued()
    {
        var g = New(2, 0);
        var warrior = g.UnitsOf(0).First(u => u.Def.Id == "warrior");
        int step = g.Grid.Nb[warrior.Tile].First(t => g.S.Tiles[t].Passable);
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, warrior.Id, step));
        Assert.Equal(0, warrior.Moves);
        int next = g.Grid.Nb[warrior.Tile].First(t => g.S.Tiles[t].Passable);
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, warrior.Id, next));      // spent: nothing is queued for next turn
        Assert.Equal(-1, warrior.Goto);
        Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn);
        Assert.Equal(step, warrior.Tile);                                    // the unit did not move by itself
        Assert.Equal(warrior.MaxMoves, warrior.Moves);
        // a long route started with movement left still continues over several turns
        int far = Enumerable.Range(0, g.Grid.Count).First(t => g.Grid.Distance(t, warrior.Tile) == 3 && g.PathFor(warrior, t) != null);
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, warrior.Id, far));
        Assert.Equal(far, warrior.Goto);
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
        // without iron the best the warrior can become is a spearman
        Assert.Equal("spearman", Rules.Units[Rules.UpgradeTarget(warrior.Type, g.P(0).Has, g.P(0).HasResource)].Id);
        g.P(0).Resources.Add((int)Resource.Iron);
        int to = Rules.UpgradeTarget(warrior.Type, g.P(0).Has, g.P(0).HasResource);
        Assert.Equal("swordsman", Rules.Units[to].Id);                           // best same-speed unit, not the horseman
        // keep the iron connected through the economy refresh that every command triggers
        var capTile = g.CitiesOf(0).Single().Tile;
        g.S.Tiles[capTile].Resource = Resource.Iron;
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
    public void Resources_strategic_are_hidden_luxuries_make_cities_happy()
    {
        var g = New(2, 0);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler).Id));
        var city = g.CitiesOf(0).Single();
        var p = g.P(0);
        var spots = g.Grid.Nb[city.Tile].Where(t => g.S.Tiles[t].Passable).ToList();
        int ironTile = spots[0], silkTile = spots[1];
        ref var it = ref g.S.Tiles[ironTile]; it.Resource = Resource.Iron; it.Forest = false; it.Relief = Relief.Hills; it.Improvement = Improvement.None;
        ref var st = ref g.S.Tiles[silkTile]; st.Resource = Resource.Silk; st.Improvement = Improvement.None;

        // fog-of-war level secrecy: the client is not told about iron before bronze working
        g.BuildView(0);
        Assert.Equal(Resource.None, TileData.Unpack(p.KnownTiles[ironTile]).Resource);
        Assert.Equal(Resource.Silk, TileData.Unpack(p.KnownTiles[silkTile]).Resource);
        g.Learn(p, Rules.Tech("bronze_working")!);
        Assert.Equal(Resource.Iron, TileData.Unpack(p.KnownTiles[ironTile]).Resource);

        // units that need iron cannot be built until it is mined
        p.Known.Add("iron_working"); p.Known.Add("mining"); p.Known.Add("pottery");
        Assert.NotNull(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:swordsman"));
        int happyBefore = city.Happy;
        var worker = g.AddUnit(0, Rules.UnitIndex("worker"), ironTile);
        Assert.NotNull(Cmd(g, 0, CmdType.Work, worker.Id, (int)WorkKind.Plantation));   // wrong improvement for iron
        Assert.Null(Cmd(g, 0, CmdType.Work, worker.Id, (int)WorkKind.Mine));
        var worker2 = g.AddUnit(0, Rules.UnitIndex("worker"), silkTile);
        Assert.Null(Cmd(g, 0, CmdType.Work, worker2.Id, (int)WorkKind.Plantation));
        for (int i = 0; i < 6; i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Equal(Improvement.Mine, g.S.Tiles[ironTile].Improvement);
        Assert.Equal(Improvement.Plantation, g.S.Tiles[silkTile].Improvement);
        g.RefreshEconomy(p);
        Assert.True(p.HasResource(Resource.Iron));
        Assert.Equal(1, p.Luxuries);
        Assert.Equal(happyBefore + 1, city.Happy);
        Assert.Null(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:swordsman"));
        // a connected luxury tile is worth more than the bare one
        var bare = g.S.Tiles[silkTile]; bare.Improvement = Improvement.None;
        Assert.True(Rules.TileYield(g.S.Tiles[silkTile]).c > Rules.TileYield(bare).c);
        Assert.All(Enum.GetValues<Resource>(), r => Assert.Equal(r, Rules.Res(r).Id));
    }

    [Fact]
    public void Ships_sail_land_units_embark_and_boats_work_the_sea()
    {
        var g = New(2, 0, seed: 21);
        var p = g.P(0);
        // found a city on the coast
        int coastLand = Enumerable.Range(0, g.Grid.Count).First(t => g.CanFoundAt(0, t) && g.Grid.Nb[t].Any(n => g.S.Tiles[n].IsWater));
        var settler = g.AddUnit(0, Rules.UnitIndex("settler"), coastLand);
        foreach (var u in g.UnitsOf(0).Where(u => u.Id != settler.Id).ToList()) g.RemoveUnit(u);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, settler.Id));
        var city = g.CitiesOf(0).Single();
        Assert.True(g.Coastal(city));
        Assert.NotNull(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:galley"));       // no sailing yet
        int water = g.Grid.Nb[city.Tile].First(n => g.S.Tiles[n].IsWater);

        var warrior = g.AddUnit(0, Rules.UnitIndex("warrior"), city.Tile);
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, warrior.Id, water));                 // cannot swim
        p.Known.Add("sailing"); p.Known.Add("astronomy");
        Assert.Null(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:galley"));
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, warrior.Id, water));
        Assert.Equal(water, warrior.Tile);
        Assert.Equal(0, warrior.Moves);                                                 // embarking ends the move
        Assert.True(g.Embarked(warrior));
        Assert.True(g.DefenseStr(warrior) < g.UnitStr(warrior));

        var galley = g.AddUnit(0, Rules.UnitIndex("galley"), city.Tile);
        int farWater = g.Grid.Range(city.Tile, 3).First(t => g.S.Tiles[t].IsWater && g.Grid.Distance(city.Tile, t) >= 2
                                                             && g.PathFor(galley, t) != null);
        Assert.Null(Cmd(g, 0, CmdType.MoveUnit, galley.Id, farWater));
        Assert.Equal(farWater, galley.Tile);
        int inland = Enumerable.Range(0, g.Grid.Count).First(t => g.S.Tiles[t].Passable && g.S.Tiles[t].CityId < 0 && !g.Grid.Nb[t].Any(n => g.S.Tiles[n].IsWater));
        Assert.NotNull(Cmd(g, 0, CmdType.MoveUnit, galley.Id, inland));                 // ships do not walk

        // a fishing boat improves a sea resource; a land worker cannot
        ref var sea = ref g.S.Tiles[water]; sea.Resource = Resource.Fish; sea.Improvement = Improvement.None;
        Assert.Equal(0, sea.Owner);
        var boat = g.AddUnit(0, Rules.UnitIndex("work_boat"), water);
        Assert.NotNull(Cmd(g, 0, CmdType.Work, boat.Id, (int)WorkKind.Farm));
        Assert.Null(Cmd(g, 0, CmdType.Work, boat.Id, (int)WorkKind.FishingBoats));
        for (int i = 0; i < 5; i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Equal(Improvement.FishingBoats, g.S.Tiles[water].Improvement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_map_type_generates_and_is_playable_by_ai(int mapType)
    {
        var g = Simulation.Run(80, 4, 5, false, null, false, 1, mapType);
        double land = g.S.Tiles.Count(t => !t.IsWater) / (double)g.S.Tiles.Length;
        Assert.InRange(land, mapType == 2 ? 0.12 : 0.2, mapType == 2 ? 0.45 : 0.7);
        Assert.All(g.Active, p => Assert.True(g.CitiesOf(p.Id).Any()));
        Assert.True(g.S.Tiles.Count(t => t.Resource != Resource.None) > 60);
        Assert.Contains(g.S.Tiles, t => Rules.Res(t.Resource).Class == ResClass.Luxury);
        Assert.Contains(g.S.Tiles, t => t.IsWater && t.Resource != Resource.None);
        // islands really are separate: on the archipelago the land splits into many pieces
        if (mapType == 2) Assert.True(g.S.Tiles.Count(t => t.Terrain == Terrain.Coast) > g.S.Tiles.Count(t => !t.IsWater) / 2);
    }

    [Fact]
    public void Faith_favor_congress_gifts_and_tile_purchase()
    {
        var g = New(2, 0);
        foreach (int pid in new[] { 0, 1 }) Assert.Null(Cmd(g, pid, CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        var p = g.P(0); var city = g.CitiesOf(0).Single();
        for (int i = 0; i < 3; i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.True(p.Faith > 0 && p.Favor > 0);                                        // palace faith, peace favor

        // faith buys peaceful projects only
        p.Faith = 1000;
        Assert.Null(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:warrior"));
        Assert.NotNull(Cmd(g, 0, CmdType.BuyFaith, city.Id));
        Assert.Null(Cmd(g, 0, CmdType.SetProduction, city.Id, s: "u:worker"));
        Assert.Null(Cmd(g, 0, CmdType.BuyFaith, city.Id));
        Assert.True(p.Faith < 1000);

        // buying land
        int free = g.Grid.Range(city.Tile, 3).First(t => g.S.Tiles[t].Owner < 0 && g.Grid.Nb[t].Any(n => g.S.Tiles[n].Owner == 0));
        p.Gold = 0;
        Assert.NotNull(Cmd(g, 0, CmdType.BuyTile, city.Id, free));
        p.Gold = 500;
        Assert.Null(Cmd(g, 0, CmdType.BuyTile, city.Id, free));
        Assert.Equal(0, g.S.Tiles[free].Owner);
        Assert.NotNull(Cmd(g, 0, CmdType.BuyTile, city.Id, free));                      // already owned
        Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn);
        Assert.Equal(0, g.S.Tiles[free].Owner);                                         // survives the border recount

        // gifts need contact; votes elect the chair
        Assert.NotNull(Cmd(g, 0, CmdType.Gift, 1, 0, 50));
        g.Rel(0, 1).Met = true;
        int gold1 = g.P(1).Gold;
        Assert.Null(Cmd(g, 0, CmdType.Gift, 1, 0, 50));
        Assert.Equal(gold1 + 50, g.P(1).Gold);
        Assert.NotNull(Cmd(g, 0, CmdType.Gift, 1, 1, 99999));
        p.Favor = 40; g.P(1).Favor = 40;
        Assert.Null(Cmd(g, 0, CmdType.Vote, 0, 30));
        Assert.Null(Cmd(g, 1, CmdType.Vote, 1, 20));
        Assert.NotNull(Cmd(g, 0, CmdType.Vote, 0, 30));                                 // only 10 left
        while (g.S.Turn % Rules.CongressPeriod != 0) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Equal(0, g.S.Chair);
        Assert.Empty(g.S.Votes);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.Congress);
    }

    [Fact]
    public void Culture_advances_social_institutions()
    {
        var g = New(2, 0);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler).Id));
        var p = g.P(0);
        Assert.NotNull(Cmd(g, 0, CmdType.SetCivic, s: "c_state"));               // prerequisites missing
        Assert.NotNull(Cmd(g, 0, CmdType.SetCivic, s: "agriculture"));           // not a civic at all
        Assert.Null(Cmd(g, 0, CmdType.SetCivic, s: "c_tribal"));
        g.RefreshEconomy(p);
        int happy = g.CitiesOf(0).Single().Happy;
        for (int i = 0; i < 12 && !p.Civics.Contains("c_tribal"); i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Contains("c_tribal", p.Civics);
        Assert.Null(p.Civic);
        Assert.Contains(g.Events, e => e.Dto.Type == EventKind.CivicDone);
        g.RefreshEconomy(p);
        Assert.Equal(happy + 1, g.CitiesOf(0).Single().Happy);                   // the institution's bonus is live

        // bonuses add up and reach combat strength and upkeep
        p.Civics.UnionWith(new[] { "c_craft", "c_military", "c_trade", "c_state" });
        g.RefreshEconomy(p);
        Assert.Equal(10, p.CivicFx.StrPct);
        Assert.Equal(15, p.CivicFx.UpkeepCutPct);
        var warrior = g.UnitsOf(0).First(u => u.Def.Id == "warrior");
        Assert.Equal(2 * 1.1, g.UnitStr(warrior), 6);

        // the tree never ends
        var known = new HashSet<string>(); int level = 0;
        for (int i = 0; i < 60; i++)
        {
            var next = Rules.AvailableCivics(known, level).OrderBy(c => c.Cost).First();
            Assert.All(next.Prereqs, pre => Assert.True(Rules.HasCivic(known, level, pre)));
            if (next.Level > 0) level = next.Level; else known.Add(next.Id);
        }
        Assert.Equal(Rules.Civics.Length, known.Count);
        Assert.True(level > 30);
        Assert.All(Rules.Civics, c => Assert.All(c.Prereqs, pre => Assert.NotNull(Rules.Civic(pre))));
    }

    [Fact]
    public void Religion_is_founded_spreads_and_is_carried_by_missionaries()
    {
        var g = New(2, 0);
        foreach (int pid in new[] { 0, 1 }) Assert.Null(Cmd(g, pid, CmdType.FoundCity, g.UnitsOf(pid).First(u => u.Def.Role == UnitRole.Settler).Id));
        var p = g.P(0);
        var capital = g.CitiesOf(0).Single(); var foreign = g.CitiesOf(1).Single();
        Assert.NotNull(Cmd(g, 0, CmdType.FoundReligion, 0, 1));                  // not enough faith
        Assert.NotNull(Cmd(g, 0, CmdType.Recruit, capital.Id));                  // nothing to preach yet
        p.Faith = 500;
        Assert.Null(Cmd(g, 0, CmdType.FoundReligion, 0, 1));                     // "Трудолюбие": +10% production
        Assert.NotNull(Cmd(g, 0, CmdType.FoundReligion, 1, 2));                  // one religion per founder
        g.P(1).Faith = 500;
        Assert.NotNull(Cmd(g, 1, CmdType.FoundReligion, 0, 3));                  // name taken
        Assert.NotNull(Cmd(g, 1, CmdType.FoundReligion, 1, 1));                  // belief taken
        var rel = g.S.Religions.Single();
        Assert.Equal(rel.Id, capital.Religion);
        Assert.Equal(0, foreign.Religion);

        // the belief works in the city that follows it
        capital.Buildings.Remove("placeholder");
        var without = new Game(SaveGame.FromJson<GameState>(SaveGame.ToJson(g.S)));
        without.CitiesOf(0).Single().Religion = 0;
        g.RefreshEconomy(p); without.RefreshEconomy(without.P(0));
        Assert.True(capital.ProdRate >= without.CitiesOf(0).Single().ProdRate);

        // a missionary walks into foreign land without open borders and converts the city
        Assert.Null(Cmd(g, 0, CmdType.Recruit, capital.Id));
        var m = g.UnitsOf(0).Single(u => u.Def.Role == UnitRole.Missionary);
        Assert.Equal(rel.Id, m.Religion);
        Assert.NotNull(Cmd(g, 0, CmdType.Preach, g.UnitsOf(0).First(u => u.Def.Id == "warrior").Id));
        g.RemoveUnit(m);
        m = g.AddUnit(0, Rules.UnitIndex("missionary"), g.Grid.Nb[foreign.Tile].First(t => g.S.Tiles[t].Passable));
        m.Religion = rel.Id;
        Assert.False(g.CanEnterTerritory(0, foreign.Tile));
        Assert.True(g.CanEnter(m, foreign.Tile));
        Assert.NotNull(Cmd(g, 0, CmdType.Preach, m.Id));                         // must stand in a city
        m.Tile = foreign.Tile;                                                   // (walked in)
        var before = p.FaithRate;
        Assert.Null(Cmd(g, 0, CmdType.Preach, m.Id));
        Assert.Equal(rel.Id, foreign.Religion);
        Assert.DoesNotContain(g.S.Units.Values, u => u.Def.Role == UnitRole.Missionary);
        g.RefreshEconomy(p);
        Assert.True(p.FaithRate > before);                                       // the founder is paid by follower cities
        Assert.Equal(rel.Id, g.BuildView(1).Me.Religions.Single().Id);
        Assert.Equal(2, g.BuildView(0).Me.Religions.Single().Cities);
        Assert.False(g.CanBuild(p, capital, "u:missionary"));                    // only recruited with faith
    }

    [Fact]
    public void Religion_spreads_passively_to_nearby_cities()
    {
        var g = New(2, 0);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, g.UnitsOf(0).First(u => u.Def.Role == UnitRole.Settler).Id));
        Assert.Null(Cmd(g, 1, CmdType.FoundCity, g.UnitsOf(1).First(u => u.Def.Role == UnitRole.Settler).Id));
        var capital = g.CitiesOf(0).Single();
        int near = Enumerable.Range(0, g.Grid.Count).First(t => g.CanFoundAt(0, t) && g.Grid.Distance(t, capital.Tile) is >= 3 and <= 5);
        var s = g.AddUnit(0, Rules.UnitIndex("settler"), near);
        Assert.Null(Cmd(g, 0, CmdType.FoundCity, s.Id));
        var second = g.CitiesOf(0).First(c => !c.Capital);
        g.P(0).Faith = 500;
        Assert.Null(Cmd(g, 0, CmdType.FoundReligion, 2, 4));
        Assert.Equal(0, second.Religion);
        for (int i = 0; i < 12; i++) { Cmd(g, 0, CmdType.EndTurn); Cmd(g, 1, CmdType.EndTurn); }
        Assert.Equal(capital.Religion, second.Religion);                          // within range of the holy city
        Assert.Equal(0, g.CitiesOf(1).Single().Religion);                         // far away: untouched
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
