using IH.Core;
using IH.Shared;

namespace IH.Server;

/// <summary>
/// Remembers what one client has already been told and produces minimal delta updates.
/// Only data inside the player's fog-of-war view ever reaches this class.
/// </summary>
public sealed class DeltaTracker
{
    private ulong[] _tiles = Array.Empty<ulong>();
    private readonly Dictionary<int, byte[]> _units = new(), _cities = new(), _players = new();
    private byte[]? _me;
    private bool _full = true;
    private int _turn = -1, _current = -2;

    /// <summary>Forces the next update to be a complete snapshot (first join, reconnect).</summary>
    public void Reset()
    {
        _tiles = Array.Empty<ulong>(); _units.Clear(); _cities.Clear(); _players.Clear();
        _me = null; _full = true; _turn = -1; _current = -2;
    }

    private static bool Changed(Dictionary<int, byte[]> sent, int id, IWire dto)
    {
        var bytes = Wire.ToBytes(dto);
        if (sent.TryGetValue(id, out var old) && old.AsSpan().SequenceEqual(bytes)) return false;
        sent[id] = bytes;
        return true;
    }

    public DeltaMsg? Build(Game g, int pid, PlayerView view, List<EventDto> events, int timerLeft)
    {
        var p = g.P(pid);
        var d = new DeltaMsg { Full = _full, Turn = g.S.Turn, CurrentPlayer = g.S.Settings.Simultaneous ? -1 : g.S.CurrentPlayer, TimerLeft = timerLeft };
        if (_tiles.Length != p.KnownTiles.Length) _tiles = new ulong[p.KnownTiles.Length];

        var tiles = new List<TileDto>();
        for (int i = 0; i < _tiles.Length; i++)
        {
            ulong k = p.KnownTiles[i];
            if (k == _tiles[i]) continue;
            _tiles[i] = k;
            tiles.Add(new TileDto { Idx = i, Pack = k });
        }
        d.Tiles = tiles.ToArray();

        var seen = new HashSet<int>();
        var units = new List<UnitDto>();
        foreach (var u in view.Units)
        {
            seen.Add(u.Id);
            if (Changed(_units, u.Id, u)) units.Add(u);
        }
        d.Units = units.ToArray();
        var removed = _units.Keys.Where(id => !seen.Contains(id)).ToArray();
        foreach (int id in removed) _units.Remove(id);
        d.RemovedUnits = removed;

        d.Cities = p.KnownCities.Values.Where(c => Changed(_cities, c.Id, c)).ToArray();
        d.Players = view.Players.Where(o => Changed(_players, o.Id, o)).ToArray();

        var me = Wire.ToBytes(view.Me);
        if (_me == null || !_me.AsSpan().SequenceEqual(me)) { _me = me; d.Me = view.Me; }
        d.Events = events.ToArray();

        bool header = _turn != d.Turn || _current != d.CurrentPlayer;
        _turn = d.Turn; _current = d.CurrentPlayer; _full = false;
        return d.IsEmpty && !header ? null : d;
    }
}
