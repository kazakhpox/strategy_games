using IH.Core;
using IH.Shared;
using Xunit;

namespace IH.Tests;

public class AuditTests
{
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
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(12)));
    }
}
