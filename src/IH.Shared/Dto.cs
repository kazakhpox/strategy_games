namespace IH.Shared;

public enum MsgType : byte
{
    // client -> server
    Hello = 1, ListGames, CreateGame, JoinGame, LeaveGame, Ready, StartGame, Command, Chat, Ping,
    // server -> client
    Welcome = 64, LobbyList, LobbyState, GameStart, Delta, CmdResult, ChatFrom, Pong, Error,
}

public enum CmdType : byte
{
    None, MoveUnit, FoundCity, Work, Fortify, Disband, SetProduction, RemoveQueue, Buy,
    SetResearch, SetSlider, DeclareWar, Propose, Respond, CancelBorders, EndTurn, Wake, Upgrade,
}

public enum EventKind : byte
{
    UnitMoved = 1,   // A=unit, B=from, C=to
    Combat,          // A=attacker tile, B=defender tile, C=attacker owner, D=defender owner, S="1" if attacker won
    CityFounded,     // A=city, B=owner, S=name
    CityCaptured,    // A=city, B=new owner, C=old owner, S=name
    TechDone,        // S=tech id
    BuildDone,       // A=city, S=item
    CityGrew,        // A=city
    WarDeclared,     // A=who, B=on whom
    PeaceMade,       // A, B
    BordersOpened,   // A, B
    Protest,         // A=city, S=name
    Secession,       // A=city, B=old owner, S=name
    PlayerDefeated,  // A=player
    Bankrupt,        // S=detail
    IdleCity,        // A=city, S=name
    Proposal,        // A=from, B=kind
    TurnStarted,     // A=turn
    Info,            // S=text
}

public enum ProposalKind : byte { Peace, OpenBorders }

public sealed class Empty : IWire { public void Sync(Wire w) { } }

public sealed class GameCommand : IWire
{
    public byte Type; public int A, B, C; public string S = "";
    public GameCommand() { }
    public GameCommand(CmdType t, int a = 0, int b = 0, int c = 0, string s = "") { Type = (byte)t; A = a; B = b; C = c; S = s; }
    public CmdType Kind => (CmdType)Type;
    public void Sync(Wire w) { w.By(ref Type); w.I(ref A); w.I(ref B); w.I(ref C); w.S(ref S); }
}

public sealed class GameSettings : IWire
{
    public string Name = "Новая игра";
    public int MapW = 56, MapH = 36, AiCount = 2, MaxPlayers = 4, TurnTimerSec = 0, Seed = 0;
    public bool Simultaneous = true;
    public void Sync(Wire w)
    {
        w.S(ref Name); w.I(ref MapW); w.I(ref MapH); w.I(ref AiCount); w.I(ref MaxPlayers);
        w.I(ref TurnTimerSec); w.I(ref Seed); w.B(ref Simultaneous);
    }
}

// ------------------------------------------------------------------ lobby
public sealed class HelloMsg : IWire
{
    public int Proto = Codec.ProtocolVersion; public string Name = ""; public string Token = "";
    public void Sync(Wire w) { w.I(ref Proto); w.S(ref Name); w.S(ref Token); }
}

public sealed class WelcomeMsg : IWire
{
    public string Token = ""; public bool Resumed;
    public void Sync(Wire w) { w.S(ref Token); w.B(ref Resumed); }
}

public sealed class GameInfo : IWire
{
    public int Id, Players, MaxPlayers, Turn; public string Name = ""; public bool Started;
    public void Sync(Wire w) { w.I(ref Id); w.I(ref Players); w.I(ref MaxPlayers); w.I(ref Turn); w.S(ref Name); w.B(ref Started); }
}

public sealed class LobbyListMsg : IWire
{
    public GameInfo[] Games = Array.Empty<GameInfo>();
    public void Sync(Wire w) { w.A(ref Games); }
}

public sealed class SlotDto : IWire
{
    public string Name = ""; public bool Ready, Connected, AI;
    public void Sync(Wire w) { w.S(ref Name); w.B(ref Ready); w.B(ref Connected); w.B(ref AI); }
}

public sealed class LobbyStateMsg : IWire
{
    public GameInfo Info = new(); public GameSettings Settings = new();
    public SlotDto[] Slots = Array.Empty<SlotDto>(); public int YourSlot, HostSlot;
    public void Sync(Wire w) { Info.Sync(w); Settings.Sync(w); w.A(ref Slots); w.I(ref YourSlot); w.I(ref HostSlot); }
}

public sealed class IntMsg : IWire { public int V; public void Sync(Wire w) { w.I(ref V); } }
public sealed class BoolMsg : IWire { public bool V; public void Sync(Wire w) { w.B(ref V); } }
public sealed class TextMsg : IWire { public int From = -1; public string Text = ""; public void Sync(Wire w) { w.I(ref From); w.S(ref Text); } }
public sealed class PingMsg : IWire { public long T; public void Sync(Wire w) { w.L(ref T); } }

public sealed class CommandMsg : IWire
{
    public int Seq; public GameCommand Cmd = new();
    public void Sync(Wire w) { w.I(ref Seq); Cmd.Sync(w); }
}

public sealed class CmdResultMsg : IWire
{
    public int Seq; public bool Ok; public string Error = "";
    public void Sync(Wire w) { w.I(ref Seq); w.B(ref Ok); w.S(ref Error); }
}

public sealed class GameStartMsg : IWire
{
    public int GameId, W, H, YourId; public GameSettings Settings = new();
    public void Sync(Wire w) { w.I(ref GameId); w.I(ref W); w.I(ref H); w.I(ref YourId); Settings.Sync(w); }
}

// ------------------------------------------------------------- game state
public sealed class TileDto : IWire
{
    public int Idx; public ulong Pack;
    public void Sync(Wire w) { w.I(ref Idx); w.U(ref Pack); }
}

public sealed class UnitDto : IWire
{
    public int Id, Owner, Type, Tile, Hp, Moves, WorkTurns, Goto = -1; public byte Work; public bool Fortified;
    public void Sync(Wire w)
    {
        w.I(ref Id); w.I(ref Owner); w.I(ref Type); w.I(ref Tile); w.I(ref Hp); w.I(ref Moves);
        w.I(ref WorkTurns); w.I(ref Goto); w.By(ref Work); w.B(ref Fortified);
    }
    public UnitDef Def => Rules.Units[Type];
}

public sealed class CityDetail : IWire
{
    public int Food, FoodNeed, FoodRate, Prod, ProdRate, Commerce, Science, Gold, Culture, CultureRate,
        Happy, Unhappy, Unrest, DisorderTurns, Maint;
    public string[] Queue = Array.Empty<string>(), Buildings = Array.Empty<string>();
    public int[] Worked = Array.Empty<int>();
    public void Sync(Wire w)
    {
        w.I(ref Food); w.I(ref FoodNeed); w.I(ref FoodRate); w.I(ref Prod); w.I(ref ProdRate); w.I(ref Commerce);
        w.I(ref Science); w.I(ref Gold); w.I(ref Culture); w.I(ref CultureRate); w.I(ref Happy); w.I(ref Unhappy);
        w.I(ref Unrest); w.I(ref DisorderTurns); w.I(ref Maint); w.SA(ref Queue); w.SA(ref Buildings); w.IA(ref Worked);
    }
}

public sealed class CityDto : IWire
{
    public int Id, Owner, Tile, Pop, Radius; public string Name = ""; public bool Capital, Disorder;
    public CityDetail? Detail; // only for the owner
    public void Sync(Wire w)
    {
        w.I(ref Id); w.I(ref Owner); w.I(ref Tile); w.I(ref Pop); w.I(ref Radius); w.S(ref Name);
        w.B(ref Capital); w.B(ref Disorder); w.O(ref Detail);
    }
}

/// <summary>Public information about a player, as seen by the receiving player.</summary>
public sealed class PlayerDto : IWire
{
    public int Id, Color, Score, Cities, Era, Techs; public string Name = "";
    public bool AI, Alive, Connected, EndedTurn, Met, Neutral, AtWar, OpenBorders;
    public void Sync(Wire w)
    {
        w.I(ref Id); w.I(ref Color); w.I(ref Score); w.I(ref Cities); w.I(ref Era); w.I(ref Techs); w.S(ref Name);
        w.B(ref AI); w.B(ref Alive); w.B(ref Connected); w.B(ref EndedTurn); w.B(ref Met); w.B(ref Neutral);
        w.B(ref AtWar); w.B(ref OpenBorders);
    }
}

public sealed class ProposalDto : IWire
{
    public int Id, From, To; public byte Kind;
    public void Sync(Wire w) { w.I(ref Id); w.I(ref From); w.I(ref To); w.By(ref Kind); }
}

public sealed class DemoDto : IWire
{
    public string Key = ""; public int Mine, Best, Avg, Worst, Rank;
    public void Sync(Wire w) { w.S(ref Key); w.I(ref Mine); w.I(ref Best); w.I(ref Avg); w.I(ref Worst); w.I(ref Rank); }
}

public sealed class TechCostDto : IWire
{
    public string Id = ""; public int Cost, BaseCost;
    public void Sync(Wire w) { w.S(ref Id); w.I(ref Cost); w.I(ref BaseCost); }
}

/// <summary>Private data of the receiving player, including the Anti-Snowball breakdown.</summary>
public sealed class MeDto : IWire
{
    public int Gold, GoldRate, SciRate, Slider, ResearchProgress, EchoBonus;
    public int Income, UnitUpkeep, BuildingUpkeep, CityMaint, RoadUpkeep, StrainCost;
    public string Research = "";
    public string[] Known = Array.Empty<string>();
    public int[] Future = new int[4];
    public float Itr, Pressure, Lag, SciMult = 1, CulMult = 1, UpkeepMult = 1, Sprawl;
    public TechCostDto[] Available = Array.Empty<TechCostDto>();
    public ProposalDto[] Proposals = Array.Empty<ProposalDto>();
    public DemoDto[] Demo = Array.Empty<DemoDto>();
    public void Sync(Wire w)
    {
        w.I(ref Gold); w.I(ref GoldRate); w.I(ref SciRate); w.I(ref Slider); w.I(ref ResearchProgress); w.I(ref EchoBonus);
        w.I(ref Income); w.I(ref UnitUpkeep); w.I(ref BuildingUpkeep); w.I(ref CityMaint); w.I(ref RoadUpkeep); w.I(ref StrainCost);
        w.S(ref Research); w.SA(ref Known); w.IA(ref Future);
        w.F(ref Itr); w.F(ref Pressure); w.F(ref Lag); w.F(ref SciMult); w.F(ref CulMult); w.F(ref UpkeepMult); w.F(ref Sprawl);
        w.A(ref Available); w.A(ref Proposals); w.A(ref Demo);
    }
}

public sealed class EventDto : IWire
{
    public byte Kind; public int A, B, C, D; public string S = "";
    public EventKind Type => (EventKind)Kind;
    public void Sync(Wire w) { w.By(ref Kind); w.I(ref A); w.I(ref B); w.I(ref C); w.I(ref D); w.S(ref S); }
}

/// <summary>
/// State update for one player. Contains only what changed since the previous message
/// (or everything the player knows when <see cref="Full"/> is set), filtered by fog of war.
/// </summary>
public sealed class DeltaMsg : IWire
{
    public bool Full;
    public int Turn, CurrentPlayer = -1, TimerLeft = -1;
    public TileDto[] Tiles = Array.Empty<TileDto>();
    public UnitDto[] Units = Array.Empty<UnitDto>();
    public int[] RemovedUnits = Array.Empty<int>();
    public CityDto[] Cities = Array.Empty<CityDto>();
    public PlayerDto[] Players = Array.Empty<PlayerDto>();
    public MeDto? Me;
    public EventDto[] Events = Array.Empty<EventDto>();

    public void Sync(Wire w)
    {
        w.B(ref Full); w.I(ref Turn); w.I(ref CurrentPlayer); w.I(ref TimerLeft);
        w.A(ref Tiles); w.A(ref Units); w.IA(ref RemovedUnits); w.A(ref Cities); w.A(ref Players);
        w.O(ref Me); w.A(ref Events);
    }

    public bool IsEmpty => !Full && Tiles.Length == 0 && Units.Length == 0 && RemovedUnits.Length == 0
        && Cities.Length == 0 && Players.Length == 0 && Me == null && Events.Length == 0;
}
