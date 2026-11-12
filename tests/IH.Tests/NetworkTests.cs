using System.Net;
using IH.Bot;
using IH.Core;
using IH.Server;
using IH.Shared;
using Xunit;

namespace IH.Tests;

[Collection("network")]
public class NetworkTests
{
    private static GameServer StartServer(string saveDir)
    {
        var server = new GameServer(new ServerOptions { Port = 0, Quiet = Environment.GetEnvironmentVariable("IH_LOG") == null, SaveDir = saveDir, AutosaveEvery = 5, AfkSeconds = 2 });
        server.Start();
        return server;
    }

    /// <summary>The client mirror must equal exactly what the server thinks this player may know.</summary>
    private static void AssertMirrors(Game g, int pid, ClientWorld w)
    {
        var view = g.BuildView(pid);
        var p = g.P(pid);
        var bad = Enumerable.Range(0, p.KnownTiles.Length).Where(i => (p.KnownTiles[i] == 0 ? 0 : (p.KnownTiles[i] & TileData.VisibleBit) != 0 ? 2 : 1) != w.Vis[i]).ToList();
        Assert.True(bad.Count == 0, $"pid {pid} alive={p.Alive} turn srv {g.S.Turn} cli {w.Turn}: {bad.Count} tiles differ, e.g. {string.Join(",", bad.Take(8).Select(i => $"{i}:srv{(p.KnownTiles[i] >> 62)}/cli{w.Vis[i]}"))}; units srv {view.Units.Count} cli {w.Units.Count}; myunits {g.UnitsOf(pid).Count()} cities {g.CitiesOf(pid).Count()}");
        for (int i = 0; i < p.KnownTiles.Length; i++)
        {
            ulong k = p.KnownTiles[i];
            int vis = k == 0 ? 0 : (k & TileData.VisibleBit) != 0 ? 2 : 1;
            Assert.Equal(vis, w.Vis[i]);
            if (k != 0) Assert.Equal(TileData.Unpack(k), w.Tiles[i]);
        }
        Assert.Equal(view.Units.Select(u => (u.Id, u.Tile, u.Hp)).OrderBy(x => x), w.Units.Values.Select(u => (u.Id, u.Tile, u.Hp)).OrderBy(x => x));
        Assert.Equal(p.KnownCities.Values.Select(c => (c.Id, c.Owner, c.Pop)).OrderBy(x => x), w.Cities.Values.Select(c => (c.Id, c.Owner, c.Pop)).OrderBy(x => x));
        Assert.Equal(p.Gold, w.Me.Gold);
        Assert.Equal(g.S.Turn, w.Turn);
        // nothing hidden ever reached the client
        Assert.All(w.Units.Values, u => Assert.Equal(2, w.Vis[u.Tile]));
    }

    private static void Pump(GameServer server, IEnumerable<NetClient> clients, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            server.Tick();
            foreach (var c in clients) c.Poll();
            Thread.Sleep(2);
        }
    }

    /// <summary>Pumps until the (lossy, delayed) link has been silent for a while, i.e. everything in flight has arrived.</summary>
    private static void Settle(GameServer server, List<NetClient> clients)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        long last = -1; var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < limit)
        {
            Pump(server, clients, 50);
            long total = clients.Sum(c => (long)c.DeltasReceived);
            if (total != last || clients.Any(c => c.PendingCommands > 0)) { last = total; quietSince = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - quietSince).TotalMilliseconds > 700) return;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bots_play_over_a_degraded_link_and_stay_in_sync(bool simultaneous)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = StartServer(dir);
        using var proxy = new LagProxy(0, new IPEndPoint(IPAddress.Loopback, server.Port), 25, 20, 3);
        try
        {
            var report = StressTest.Run("127.0.0.1", proxy.Port, 3, 1, 12, simultaneous, TimeSpan.FromSeconds(120), null, server.Tick);
            Assert.True(report.Completed, $"game stalled at turn {report.Turn}");
            Assert.True(proxy.Dropped > 0, "the proxy should have dropped packets");
            Assert.True(report.AvgRtt >= 40, $"commands should feel the artificial latency (avg {report.AvgRtt} ms)");

            // let the bots finish what they are doing, then compare every mirror with the server
            var session = server.Sessions.Values.Single();
            var g = session.Game!;
            foreach (var b in report.Bots) b.Paused = true;
            Settle(server, report.Bots.Select(b => b.Net).ToList());
            // seats are assigned in arrival order, which the jittery link may shuffle
            Assert.Equal(new[] { 0, 1, 2 }, report.Bots.Select(b => b.World!.MyId).OrderBy(x => x));
            foreach (var b in report.Bots) AssertMirrors(g, b.World!.MyId, b.World);
            Assert.True(File.Exists(Path.Combine(dir, $"game{session.Id}.json")), "autosave missing");

            // reconnect: a brand new connection with the old seat token gets the full state back
            string token = report.Bots[0].Net.Token;
            int seat = report.Bots[0].World!.MyId;
            report.Bots[0].Net.Disconnect();
            Pump(server, report.Bots.Skip(1).Select(b => b.Net), 300);
            Assert.False(g.P(seat).Connected);
            var again = new NetClient { Name = "Бот1", Token = token };
            again.Connect("127.0.0.1", proxy.Port);
            var all = report.Bots.Skip(1).Select(b => b.Net).Append(again).ToList();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && !(again.State == NetState.InGame && again.World is { Turn: > 0 })) Pump(server, all, 20);
            Assert.Equal(NetState.InGame, again.State);
            Settle(server, all);
            Assert.True(g.P(seat).Connected);
            Assert.Equal(seat, again.World!.MyId);
            AssertMirrors(g, seat, again.World!);

            // a stranger cannot take a seat in a running game
            var stranger = new NetClient { Name = "Чужак" };
            string? error = null;
            stranger.ErrorReceived += e => error = e;
            stranger.Connect("127.0.0.1", server.Port);
            all.Add(stranger);
            Pump(server, all, 300);
            stranger.Send(MsgType.JoinGame, new IntMsg { V = session.Id });
            stranger.SendCommand(CmdType.EndTurn);
            Pump(server, all, 300);
            Assert.NotNull(error);
            Assert.Null(stranger.World);

            // the save file restores into an identical simulation
            session.Save();
            var save = SaveGame.FromJson<SessionSave>(File.ReadAllText(Path.Combine(dir, $"game{session.Id}.json")));
            Assert.Equal(SaveGame.ToJson(g.S), SaveGame.ToJson(new Game(save.State).S));
            Assert.Equal(3, save.Slots.Count);
            foreach (var c in all) c.Dispose();
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Disconnected_player_does_not_block_the_game_forever()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ih-test-" + Guid.NewGuid().ToString("N"));
        var server = StartServer(dir);
        try
        {
            var a = new NetClient { Name = "A" }; var b = new NetClient { Name = "B" };
            var both = new[] { a, b };
            a.Connect("127.0.0.1", server.Port); b.Connect("127.0.0.1", server.Port);
            Pump(server, both, 300);
            a.Send(MsgType.CreateGame, new GameSettings { MapW = 40, MapH = 28, AiCount = 0, MaxPlayers = 2, Seed = 3 });
            Pump(server, both, 200);
            b.Send(MsgType.JoinGame, new IntMsg { V = a.Room!.Info.Id });
            Pump(server, both, 200);
            b.Send(MsgType.StartGame); // not the host
            Pump(server, both, 200);
            Assert.Null(a.World);
            a.Send(MsgType.StartGame);
            Pump(server, both, 300);
            Assert.Equal(1, a.World!.Turn);

            b.Disconnect();
            a.SendCommand(CmdType.EndTurn);
            Pump(server, new[] { a }, 500);
            Assert.Equal(1, a.World.Turn);          // still inside the grace period
            Assert.False(a.World.Players[1].Connected);
            Pump(server, new[] { a }, 2500);
            Assert.Equal(2, a.World.Turn);          // grace period over: B's turn was ended for them
            a.Dispose(); b.Dispose();
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
