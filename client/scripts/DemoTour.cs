using System.Linq;
using System.Threading.Tasks;
using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>
/// Scripted smoke test (run with "-- --connect=host:port --autohost --tour=DIR"): plays a few dozen turns through
/// the real UI/network path, saves screenshots of the main screens into DIR and quits. Not used in normal play.
/// </summary>
public partial class DemoTour : Node
{
    private readonly Main _main;
    private readonly string _dir;
    public DemoTour(Main main, string dir) { _main = main; _dir = dir; }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task Shot(string name)
    {
        await Wait(0.4);
        _main.QueueRedraw(); // low-processor mode draws no frame unless something changed
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng($"{_dir}/{name}.png");
        GD.Print($"tour: saved {name}.png");
    }

    /// <summary>Selects a unit with a real mouse click going through Godot's input pipeline.</summary>
    private async Task ClickTest()
    {
        var game = _main.Game!;
        var w = _main.Net.World!;
        var unit = w.Units.Values.First(u => u.Owner == w.MyId);
        game.Map.CenterOn(unit.Tile);
        game.Map.Select(-1);
        await Wait(0.3);
        var pos = game.Map.ScreenPos(unit.Tile);
        foreach (bool pressed in new[] { true, false })
        {
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = pos, GlobalPosition = pos });
            await Wait(0.1);
        }
        bool ok = game.Map.SelectedUnit >= 0 && w.Units.TryGetValue(game.Map.SelectedUnit, out var sel) && sel.Tile == unit.Tile;
        GD.Print(ok ? "tour: click-select OK" : $"tour: click-select FAILED (selected {game.Map.SelectedUnit})");
    }

    /// <summary>Casts "grow forest" the way a player does: pick the spell, then click a tile on the map.</summary>
    private async Task SpellTest(GameScreen game, ClientWorld w)
    {
        var shaman = w.Units.Values.FirstOrDefault(u => u.Owner == w.MyId && u.Def.Id == "shaman");
        var spell = Rules.Action("sh_grow");
        if (shaman == null || spell == null || !w.Knows(spell.Requires)) { GD.Print($"tour: spell-cast SKIPPED (shaman {(shaman != null)}, totems {w.Knows("sh_totems")})"); return; }
        int target = w.Grid.Range(shaman.Tile, spell.Range).FirstOrDefault(t =>
        {
            var tile = w.Tiles[t];
            return t != shaman.Tile && w.Vis[t] > 0 && tile.Passable && !tile.Forest && tile.CityId < 0 && tile.Improvement == Improvement.None
                   && tile.Owner == w.MyId && tile.Terrain is Terrain.Grassland or Terrain.Plains or Terrain.Tundra;
        }, -1);
        if (target < 0) { GD.Print("tour: spell-cast SKIPPED (no suitable tile)"); return; }
        game.Map.Select(shaman.Id);
        game.Map.CenterOn(shaman.Tile);
        await Wait(0.3);
        game.BeginAction(spell, shaman.Id);
        await Shot("03a_aiming");
        var pos = game.Map.ScreenPos(target);
        foreach (bool pressed in new[] { true, false })
        {
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = pos, GlobalPosition = pos });
            await Wait(0.1);
        }
        await Wait(0.6);
        GD.Print(w.Tiles[target].Forest ? "tour: spell-cast OK" : "tour: spell-cast FAILED");
        await Shot("03b_after_spell");
    }

    private async Task Run()
    {
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        await Shot("00_menu");
        var net = _main.Net;
        for (int i = 0; i < 200 && (net.World == null || net.World.Turn < 1 || _main.Game == null); i++) await Wait(0.1);
        if (net.World == null || _main.Game == null) { GD.PrintErr("tour: game did not start"); GetTree().Quit(1); return; }
        var w = net.World;
        await Shot("01_start");
        await ClickTest();

        var settler = w.Units.Values.FirstOrDefault(u => u.Owner == w.MyId && u.Def.Role == UnitRole.Settler);
        if (settler != null) net.SendCommand(CmdType.FoundCity, settler.Id);
        string[] plan = { "u:warrior", "u:worker", "u:settler", "b:monument", "u:warrior", "b:granary", "b:library" };
        int planIdx = 0;
        var rnd = new System.Random(5);
        for (int turn = 0; turn < 75; turn++)
        {
            await Wait(0.12);
            for (int bi = 0; bi < Rules.Branches.Count && bi < w.Me.BranchCurrent.Length; bi++)
            {
                if (w.Me.BranchCurrent[bi].Length > 0) continue;
                var nodes = new System.Collections.Generic.HashSet<string>(w.Me.Nodes);
                var pick = Rules.Branches[bi].Nodes.FirstOrDefault(n => !nodes.Contains(n.Id) && n.Requires.All(r => Rules.Satisfied(r, w.Me.Known, w.Me.Future, w.Me.Civics, w.Me.CivicLevel, nodes)));
                if (pick != null) net.SendCommand(CmdType.SetNode, s: pick.Id);
            }
            if (w.Me.Civic.Length == 0) net.SendCommand(CmdType.SetCivic, s: Rules.AvailableCivics(w.Me.Civics, w.Me.CivicLevel).OrderBy(c => c.Cost).First().Id);
            if (w.Me.MyReligion == 0 && w.Me.Faith >= Rules.ReligionPrice(w.Me.Religions.Length) && w.Me.Religions.Length < 6)
                net.SendCommand(CmdType.FoundReligion, Enumerable.Range(0, 8).First(i => w.Me.Religions.All(r => r.Name != Rules.ReligionNames[i])), Enumerable.Range(0, 6).First(i => w.Me.Religions.All(r => r.Belief != i)));
            if (w.Me.Research.Length == 0 && w.Me.Available.Length > 0)
                net.SendCommand(CmdType.SetResearch, s: w.Me.Available.OrderBy(a => a.Cost).First().Id);
            bool wantShaman = w.Knows("sh_spirits") && !w.Units.Values.Any(u => u.Owner == w.MyId && u.Def.Id == "shaman");
            foreach (var c in w.Cities.Values.Where(c => c.Owner == w.MyId && c.Detail is { Queue.Length: 0 }))
            {
                net.SendCommand(CmdType.SetProduction, c.Id, 0, 0, wantShaman ? "u:shaman" : plan[planIdx++ % plan.Length]);
                wantShaman = false;
            }
            foreach (var u in w.Units.Values.Where(u => u.Owner == w.MyId && u.Moves > 0 && u.Goto < 0 && u.Work == 0).ToList())
            {
                if (u.Def.Role == UnitRole.Settler) { net.SendCommand(CmdType.FoundCity, u.Id); continue; }
                if (u.Def.Id == "shaman") continue; // stays home for the spell test
                if (u.Def.Role == UnitRole.Worker)
                {
                    var kind = new[] { WorkKind.Mine, WorkKind.Farm, WorkKind.Cottage, WorkKind.Road }
                        .FirstOrDefault(k => Rules.WorkFits(w.Tiles[u.Tile], k) && w.Knows(Rules.WorkTech(k)) && w.Tiles[u.Tile].Owner == w.MyId);
                    if (kind != WorkKind.None) { net.SendCommand(CmdType.Work, u.Id, (int)kind); continue; }
                }
                var options = w.Grid.Range(u.Tile, u.Def.Role == UnitRole.Worker ? 1 : 4)
                    .Where(t => t != u.Tile && w.Vis[t] > 0 && w.Tiles[t].Passable && (u.Def.Role != UnitRole.Worker || w.Tiles[t].Owner == w.MyId)).ToList();
                if (options.Count > 0 && _main.Game != null) _main.Game.Map.OrderMove(u, options[rnd.Next(options.Count)]);
            }
            if (turn == 20) await Shot("02_moving_units");
            await Wait(0.12);
            net.SendCommand(CmdType.EndTurn);
        }
        await Wait(0.6);
        var game = _main.Game!;
        await SpellTest(game, w);
        var capital = w.Cities.Values.FirstOrDefault(c => c.Owner == w.MyId);
        if (capital != null) game.Map.CenterOn(capital.Tile);
        await Shot("03_map");
        if (capital != null) { game.OpenCity(capital.Id); await Shot("04_city"); }
        game.Toggle(Win.Science, true); await Shot("05_science");
        game.Toggle(Win.Civics, true); await Shot("05b_civics");
        game.Toggle(Win.Religion, true); await Shot("05c_religion");
        game.Toggle(Win.Branches, true); await Shot("05d_branches");
        game.Toggle(Win.Economy, true); await Shot("06_economy");
        game.Toggle(Win.Diplomacy, true); await Shot("07_diplomacy");
        game.Toggle(Win.Demographics, true); await Shot("08_demographics");
        game.Toggle(Win.None);
        game.Map.Zoom = 0.5f; game.Map.ApplyCamera(); await Shot("09_lod_medium");
        game.Map.Zoom = 0.2f; game.Map.ApplyCamera(); await Shot("10_lod_far");
        GD.Print($"tour: done at turn {w.Turn}, ping {net.PingMs} ms");
        GetTree().Quit();
    }
}
