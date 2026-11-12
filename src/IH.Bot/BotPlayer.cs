using IH.Shared;

namespace IH.Bot;

/// <summary>Headless scripted player used for integration and stress tests. Plays only through the network protocol.</summary>
public sealed class BotPlayer
{
    public readonly NetClient Net = new();
    public readonly List<int> Rtts = new();
    public int Deltas, Rejected, TurnsPlayed;
    public string? LastError;
    /// <summary>When set the bot keeps the connection alive but issues no commands.</summary>
    public bool Paused;
    private readonly Random _rnd;
    private int _actedTurn = -1, _actedCurrent = -2;

    public BotPlayer(string name, int seed)
    {
        Net.Name = name;
        _rnd = new Random(seed);
        Net.DeltaApplied += _ => Deltas++;
        Net.CommandResult += (_, ok, _, rtt) => { if (!ok) Rejected++; if (rtt >= 0) Rtts.Add(rtt); };
        Net.ErrorReceived += e => LastError = e;
    }

    public ClientWorld? World => Net.World;

    /// <summary>Call frequently. Issues this turn's commands once the server says it is our turn.</summary>
    public void Update()
    {
        Net.Poll();
        var w = Net.World;
        if (Paused || w == null || Net.State != NetState.InGame || Net.PendingCommands > 0 || !w.MyTurn) return;
        if (_actedTurn == w.Turn && _actedCurrent == w.CurrentPlayer) return;
        _actedTurn = w.Turn; _actedCurrent = w.CurrentPlayer;
        TurnsPlayed++;

        if (w.Me.Research.Length == 0 && w.Me.Available.Length > 0)
            Net.SendCommand(CmdType.SetResearch, s: w.Me.Available.OrderBy(t => t.Cost).First().Id);

        var myCities = w.Cities.Values.Where(c => c.Owner == w.MyId).ToList();
        foreach (var c in myCities)
            if (c.Detail != null && c.Detail.Queue.Length == 0)
            {
                string item = _rnd.Next(4) switch { 0 => "u:settler", 1 => "u:worker", _ => "u:warrior" };
                Net.SendCommand(CmdType.SetProduction, c.Id, 0, 0, item);
            }

        foreach (var u in w.Units.Values.Where(u => u.Owner == w.MyId).ToList())
        {
            if (u.Def.Role == UnitRole.Settler && (myCities.Count == 0 || _rnd.Next(4) == 0))
            {
                Net.SendCommand(CmdType.FoundCity, u.Id);
                continue;
            }
            if (u.Def.Role == UnitRole.Worker && _rnd.Next(3) == 0)
            {
                Net.SendCommand(CmdType.Work, u.Id, _rnd.Next(1, 5));
                continue;
            }
            var options = w.Grid.Range(u.Tile, 3).Where(t => t != u.Tile && w.Vis[t] > 0 && w.Tiles[t].Passable).ToList();
            if (options.Count > 0) Net.SendCommand(CmdType.MoveUnit, u.Id, options[_rnd.Next(options.Count)]);
        }
        Net.SendCommand(CmdType.EndTurn);
    }
}
