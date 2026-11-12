using IH.Shared;

namespace IH.Core;

/// <summary>
/// Anti-Snowball engine. Once per global turn computes each player's Technology Gap Index (ITR)
/// and derives leader penalties and laggard bonuses from it.
///
///   L_p   = accumulated science level (base cost of all known techs + current progress)
///   ITR_p = (L_p - median(L_others)) / (median(L_others) + ItrSmoothing)
///
///   Leader (ITR above the deadzone): pressure g = sat((ITR - deadzone) / scale), sat(x) = x / (1 + x)
///     information vacuum : science x (1 - SciencePenalty * g), culture x (1 - CulturePenalty * g)
///     logistic strain    : upkeep  x (1 + UpkeepPenalty * g + SprawlPenalty * sat(sprawl))
///     social unrest      : per-city unrest growth proportional to g and distance from the capital
///   Laggard (ITR below zero): lag = clamp((-ITR - LagDeadzone) * LagScale, 0, 1)
///     low-base effect    : tech cost x (1 - min(MaxDiscount, CatchUpStrength * lag * share of others knowing it))
///     science echo       : bonus science from peaceful contacts with more advanced civilizations
/// </summary>
public static class AntiSnowball
{
    public static double Sat(double x) => x <= 0 ? 0 : x / (1 + x);

    public static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    public static void Recompute(Game g)
    {
        var bal = g.Bal;
        var active = g.Active.ToList();
        var cityCount = active.ToDictionary(p => p.Id, p => g.CitiesOf(p.Id).Count());
        foreach (var p in active)
        {
            var others = active.Where(o => o.Id != p.Id).ToList();
            if (others.Count == 0)
            {
                p.Itr = p.Pressure = p.Lag = p.Sprawl = 0; p.SciMult = p.CulMult = p.UpkeepMult = 1;
                continue;
            }
            double med = Median(others.Select(o => (double)o.ScienceLevel).ToList());
            p.Itr = (p.ScienceLevel - med) / (med + bal.ItrSmoothing);
            p.Pressure = Sat((p.Itr - bal.LeaderDeadzone) / bal.LeaderScale);
            p.Lag = Math.Clamp((-p.Itr - bal.LagDeadzone) * bal.LagScale, 0, 1);

            double medCities = Median(others.Select(o => (double)cityCount[o.Id]).ToList());
            p.Sprawl = Math.Max(0, (cityCount[p.Id] - medCities) / (medCities + 2));

            p.SciMult = 1 - bal.SciencePenalty * p.Pressure;
            p.CulMult = 1 - bal.CulturePenalty * p.Pressure;
            p.UpkeepMult = 1 + bal.UpkeepPenalty * p.Pressure + bal.SprawlPenalty * Sat(p.Sprawl);
        }
    }

    /// <summary>Effective research cost for this player, including the catch-up discount.</summary>
    public static int TechCost(Game g, Player p, TechDef t)
    {
        if (p.Lag <= 0) return t.Cost;
        int others = 0, know = 0;
        foreach (var o in g.Active)
        {
            if (o.Id == p.Id) continue;
            others++;
            if (o.Has(t.Id)) know++;
        }
        if (others == 0 || know == 0) return t.Cost;
        double discount = Math.Min(g.Bal.MaxDiscount, g.Bal.CatchUpStrength * p.Lag * know / others);
        return Math.Max(1, (int)Math.Round(t.Cost * (1 - discount)));
    }

    /// <summary>"Science echo": per-turn bonus science from peaceful contacts with more advanced civilizations.</summary>
    public static double Echo(Game g, Player p)
    {
        double echo = 0;
        long mine = p.ScienceLevel;
        foreach (var o in g.Active)
        {
            if (o.Id == p.Id || o.ScienceLevel <= mine) continue;
            var rel = g.Rel(p.Id, o.Id);
            if (!rel.Met || rel.AtWar) continue;
            double gap = Math.Min(1.0, 2.0 * (o.ScienceLevel - mine) / o.ScienceLevel);
            echo += o.SciRate * (g.Bal.EchoBase + (rel.OpenBorders ? g.Bal.EchoOpenBorders : 0)) * gap;
        }
        return Math.Min(echo, g.Bal.EchoCap * p.SciRate + 2);
    }
}
