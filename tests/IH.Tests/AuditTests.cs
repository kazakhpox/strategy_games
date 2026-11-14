using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class AuditTests
{
    /// <summary>What each player is told must never contain what they cannot know.</summary>
    private static List<string> FogLeaks(Game g)
    {
        var bad = new List<string>();
        foreach (var p in g.Active.ToList())
        {
            var view = g.BuildView(p.Id);
            foreach (var u in view.Units)
            {
                if (!view.Visible[u.Tile]) bad.Add($"ход {g.S.Turn}: {p.Name} видит юнит {u.Id} вне обзора");
                if (u.Owner != p.Id && (u.Moves != 0 || u.Goto != -1)) bad.Add($"ход {g.S.Turn}: {p.Name} знает приказы чужого юнита {u.Id}");
            }
            for (int i = 0; i < p.KnownTiles.Length; i++)
            {
                if (p.KnownTiles[i] == 0) continue;
                var r = TileData.Unpack(p.KnownTiles[i]).Resource;
                if (!Rules.ResourceVisible(r, p.Has)) bad.Add($"ход {g.S.Turn}: {p.Name} видит {Rules.Res(r).Name} без технологии");
            }
            foreach (var o in view.Players)
                if (!o.Met && !o.Neutral && (o.Name != "Неизвестная цивилизация" || o.Techs != 0 || o.Cities != 0 || o.Score != 0))
                    bad.Add($"ход {g.S.Turn}: {p.Name} знает данные незнакомой цивилизации {o.Id}");
            foreach (var c in p.KnownCities.Values)
                if (c.Owner != p.Id && c.Detail != null) bad.Add($"ход {g.S.Turn}: {p.Name} видит внутренности чужого города {c.Name}");
            if (bad.Count > 10) break;
        }
        return bad;
    }

    /// <summary>Long AI games on every map type, audited every turn.</summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 11)]
    [InlineData(1, 5)]
    [InlineData(2, 7)]
    [InlineData(2, 21)]
    public void World_stays_consistent_through_a_long_game(int mapType, int seed)
    {
        var settings = new GameSettings { MapW = 56, MapH = 36, Seed = seed, Simultaneous = true, MapType = mapType };
        var g = Game.Create(settings, Enumerable.Range(0, 5).Select(i => ($"AI{i}", true)).ToList());
        var problems = new List<string>();
        for (int turn = 0; turn < 320 && problems.Count == 0; turn++)
        {
            g.StepAiOnly();
            g.Events.Clear();
            problems.AddRange(g.CheckInvariants());
            if (turn % 20 == 0) problems.AddRange(FogLeaks(g));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(12)));
    }
}
