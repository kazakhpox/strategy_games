using IH.Shared;

namespace IH.Core;

/// <summary>All tunable Anti-Snowball coefficients. Can be overridden from a JSON file on the server.</summary>
public sealed class BalanceConfig
{
    // ITR = (L_p - median(L_others)) / (median + ItrSmoothing), L = accumulated science level
    public double ItrSmoothing = 120;
    // Leader pressure g = sat((ITR - LeaderDeadzone) / LeaderScale), sat(x) = x / (1 + x), g in [0, 1)
    public double LeaderDeadzone = 0.25;
    public double LeaderScale = 1.0;
    public double SciencePenalty = 0.45;     // "information vacuum": science x (1 - k*g)
    public double CulturePenalty = 0.35;     //                       culture x (1 - k*g)
    public double UpkeepPenalty = 1.2;       // "logistic strain":    upkeep  x (1 + k*g + SprawlPenalty*sat(sprawl))
    public double SprawlPenalty = 0.5;
    public double UnrestPerPressure = 14;    // "social unrest": unrest points per turn per city at g = 1
    public double SecessionPressure = 0.3;   // minimum g for separatism
    public int SecessionMinDistance = 6;
    public int SecessionChancePct = 35;
    // Laggard bonuses. lag = clamp((-ITR - LagDeadzone) * LagScale, 0, 1)
    public double LagDeadzone = 0.08;
    public double LagScale = 1.6;
    public double CatchUpStrength = 0.85;    // discount = min(MaxDiscount, k * lag * share_of_others_who_know_it)
    public double MaxDiscount = 0.7;
    public double EchoBase = 0.04;           // share of a more advanced contact's science received per turn
    public double EchoOpenBorders = 0.06;    // extra share with an open borders agreement
    public double EchoCap = 0.6;             // cap relative to own science
}

public sealed class Unit
{
    public int Id, Owner, Type, Tile, Hp = 100, Moves, WorkTurns, Goto = -1;
    public WorkKind Work;
    public bool Fortified;
    public UnitDef Def => Rules.Units[Type];
    public int MaxMoves => Def.Moves * Rules.MovePoint;
}

public sealed class City
{
    public int Id, Owner, Tile, Pop = 1, Food, Prod, Culture, Unrest, DisorderTurns, FoundedTurn;
    public string Name = "";
    public bool Capital;
    public List<string> Queue = new();
    public HashSet<string> Buildings = new();
    // derived each refresh
    public List<int> Worked = new();
    public int FoodRate, ProdRate, Commerce, Happy, Unhappy;
    public double SciRate, GoldRate, CulRate, Maint, BuildingUpkeep;
    public int Radius => Rules.RadiusForCulture(Culture);
}

public sealed class Player
{
    public int Id, Color;
    public string Name = "";
    public bool IsAI, Alive = true, Neutral, EndedTurn;
    public int Gold = 30, Slider = 70, ResearchProgress;
    public string? Research;
    public HashSet<string> Known = new();
    public int[] Future = new int[4];
    public long TechPoints;
    public double SciFrac;
    /// <summary>Handicap: multiplier on production and commerce (1 = normal). Used for AI difficulty and balance experiments.</summary>
    public double Bonus = 1;
    public bool Connected = true;
    // fog-of-war memory
    public ulong[] KnownTiles = Array.Empty<ulong>();
    public Dictionary<int, CityDto> KnownCities = new();
    // anti-snowball state (recomputed at the end of every global turn)
    public double Itr, Pressure, Lag, Sprawl, SciMult = 1, CulMult = 1, UpkeepMult = 1;
    // derived economy
    public double SciRate, EchoBonus, Income, UnitUpkeep, BuildingUpkeep, CityMaint, RoadUpkeep, StrainCost;
    public int GoldRate;

    public long ScienceLevel => TechPoints + ResearchProgress;
    public bool Has(string? tech) => Rules.HasTech(Known, Future, tech);
    public int TechCount => Known.Count + Future.Sum();
}

public sealed class Relation { public bool AtWar, OpenBorders, Met; public int SinceTurn; }
public sealed class Proposal { public int Id, From, To; public ProposalKind Kind; }

public sealed class GameState
{
    public GameSettings Settings = new();
    public BalanceConfig Balance = new();
    public int W, H, Turn = 1, CurrentPlayer, NextId = 1;
    public ulong Rng = 0x9E3779B97F4A7C15;
    public TileData[] Tiles = Array.Empty<TileData>();
    public Dictionary<int, Unit> Units = new();
    public Dictionary<int, City> Cities = new();
    public List<Player> Players = new();
    public Dictionary<int, Relation> Relations = new();
    public List<Proposal> Proposals = new();
}

public sealed class GameEvent
{
    public EventDto Dto = new();
    /// <summary>Tiles that make the event visible to whoever sees one of them; null = no tile filter.</summary>
    public int[]? Tiles;
    /// <summary>If >= 0 the event is delivered only to this player.</summary>
    public int OnlyPlayer = -1;
}
