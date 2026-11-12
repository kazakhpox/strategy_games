namespace IH.Shared;

/// <summary>
/// Client-side mirror of what the server has revealed to this player. It holds no game logic:
/// it only applies deltas. Used by the Godot client and by the headless bot.
/// </summary>
public sealed class ClientWorld
{
    public readonly HexGrid Grid;
    public readonly int MyId;
    public readonly GameSettings Settings;
    public readonly TileData[] Tiles;
    /// <summary>0 = unexplored, 1 = explored (fogged), 2 = currently visible.</summary>
    public readonly byte[] Vis;
    public readonly Dictionary<int, UnitDto> Units = new();
    public readonly Dictionary<int, CityDto> Cities = new();
    public readonly Dictionary<int, PlayerDto> Players = new();
    public MeDto Me = new();
    public int Turn, CurrentPlayer = -1, TimerLeft = -1;
    public long Revision;

    public ClientWorld(GameStartMsg start)
    {
        Grid = new HexGrid(start.W, start.H);
        MyId = start.YourId;
        Settings = start.Settings;
        Tiles = new TileData[Grid.Count];
        Vis = new byte[Grid.Count];
        for (int i = 0; i < Tiles.Length; i++) { Tiles[i].Owner = -1; Tiles[i].CityId = -1; }
    }

    public void Apply(DeltaMsg d)
    {
        if (d.Full)
        {
            Units.Clear(); Cities.Clear(); Players.Clear();
            Array.Clear(Vis);
        }
        Turn = d.Turn; CurrentPlayer = d.CurrentPlayer; TimerLeft = d.TimerLeft;
        foreach (var t in d.Tiles)
        {
            if (!Grid.Valid(t.Idx)) continue;
            Tiles[t.Idx] = TileData.Unpack(t.Pack);
            Vis[t.Idx] = (t.Pack & TileData.VisibleBit) != 0 ? (byte)2 : (byte)1;
        }
        foreach (int id in d.RemovedUnits) Units.Remove(id);
        foreach (var u in d.Units) Units[u.Id] = u;
        foreach (var c in d.Cities) Cities[c.Id] = c;
        foreach (var p in d.Players) Players[p.Id] = p;
        if (d.Me != null) Me = d.Me;
        Revision++;
    }

    public bool MyTurn => Players.TryGetValue(MyId, out var me) && me.Alive && !me.EndedTurn
        && (Settings.Simultaneous || CurrentPlayer == MyId);

    public IEnumerable<UnitDto> UnitsAt(int tile) => Units.Values.Where(u => u.Tile == tile);
    public CityDto? CityAt(int tile) => Tiles[tile].CityId >= 0 && Cities.TryGetValue(Tiles[tile].CityId, out var c) ? c : null;
    public bool Knows(string? tech) => Rules.HasTech(Me.Known, Me.Future, tech);

    /// <summary>Local path prediction with the same cost rules the server uses, limited to what the player can see.</summary>
    public List<int>? PredictPath(UnitDto u, int target)
    {
        if (!Grid.Valid(target) || Vis[target] == 0) return null;
        return Grid.FindPath(u.Tile, target, (from, to) =>
        {
            if (Vis[to] == 0) return -1;
            int c = Rules.MoveCost(Tiles[from], Tiles[to]);
            if (c < 0) return -1;
            int owner = Tiles[to].Owner;
            if (owner >= 0 && owner != MyId && Players.TryGetValue(owner, out var p) && !p.AtWar && !p.OpenBorders && !p.Neutral)
                return -1;
            return c;
        }, 6000);
    }

    public string PlayerName(int id) => Players.TryGetValue(id, out var p) ? p.Name : id < 0 ? "—" : $"Игрок {id}";

    /// <summary>Human-readable (Russian) text for a server event; null for purely visual events.</summary>
    public string? Describe(EventDto e)
    {
        switch (e.Type)
        {
            case EventKind.Combat:
                bool won = e.S == "1";
                if (e.C == MyId) return won ? $"Победа в бою против «{PlayerName(e.D)}»" : $"Наш юнит погиб, атакуя «{PlayerName(e.D)}»";
                if (e.D == MyId) return won ? $"Мы потеряли юнит в обороне от «{PlayerName(e.C)}»" : $"Атака «{PlayerName(e.C)}» отбита";
                return null;
            case EventKind.CityFounded: return $"«{PlayerName(e.B)}» основывает город {e.S}";
            case EventKind.CityCaptured: return $"«{PlayerName(e.B)}» захватывает город {e.S} у «{PlayerName(e.C)}»";
            case EventKind.TechDone: return $"Изучено: {Rules.Tech(e.S)?.Name ?? e.S}";
            case EventKind.BuildDone:
                return $"{(Cities.TryGetValue(e.A, out var c) ? c.Name : "Город")}: построено — {Rules.ItemName(e.S)}";
            case EventKind.WarDeclared: return $"«{PlayerName(e.A)}» объявляет войну «{PlayerName(e.B)}»!";
            case EventKind.PeaceMade: return $"«{PlayerName(e.A)}» и «{PlayerName(e.B)}» заключили мир";
            case EventKind.BordersOpened: return $"«{PlayerName(e.A)}» и «{PlayerName(e.B)}» открыли границы";
            case EventKind.Protest: return $"Социальное брожение: протесты в городе {e.S}!";
            case EventKind.Secession: return $"Сепаратизм: город {e.S} отделился от «{PlayerName(e.B)}»!";
            case EventKind.PlayerDefeated: return $"Цивилизация «{PlayerName(e.A)}» уничтожена";
            case EventKind.Bankrupt: return $"Казна пуста: {e.S}";
            case EventKind.IdleCity: return $"Город {e.S} простаивает — выберите производство";
            case EventKind.Proposal:
                return $"«{PlayerName(e.A)}» предлагает: {(e.B == (int)ProposalKind.Peace ? "мир" : "открытые границы")}";
            case EventKind.Info: return e.S;
            default: return null;
        }
    }
}
