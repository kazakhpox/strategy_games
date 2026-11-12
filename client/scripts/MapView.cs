using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

/// <summary>
/// Hex map renderer and map input. Terrain is drawn into static chunks (redrawn only when their tiles change),
/// so panning and zooming cost nothing; units live on a separate overlay and are interpolated between tiles.
/// </summary>
public partial class MapView : Node2D
{
    public const float R = 36f;
    public const float RowH = 1.5f * R;
    public static readonly float HexW = Mathf.Sqrt(3f) * R;
    private const int ChunkSize = 12;
    private const float MoveSpeed = 520f;

    public static readonly Vector2[] Corner = Enumerable.Range(0, 6)
        .Select(k => new Vector2(Mathf.Cos(Mathf.DegToRad(60 * k - 30)), Mathf.Sin(Mathf.DegToRad(60 * k - 30))) * R).ToArray();
    // directions E, NE, NW, W, SW, SE and the corner pair forming the shared edge
    private static readonly (int dx, int dy)[] EvenRow = { (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1) };
    private static readonly (int dx, int dy)[] OddRow = { (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1) };
    public static readonly (int a, int b)[] Edge = { (0, 1), (5, 0), (4, 5), (3, 4), (2, 3), (1, 2) };

    public readonly ClientWorld W;
    private readonly GameScreen _screen;
    private readonly List<TerrainChunk> _chunks = new();
    private readonly int _chunkCols;
    private readonly MapOverlay _overlay;
    private readonly Dictionary<int, UnitVis> _vis = new();
    private readonly Dictionary<int, int> _moveSeq = new();
    private readonly List<(Vector2 pos, double until, Color color)> _flashes = new();

    public float Zoom = 1f;
    public Vector2 Cam;
    public int Lod = 2;
    public int SelectedUnit = -1, HoverTile = -1;
    public List<int>? HoverPath;
    public int[] Highlight = Array.Empty<int>();
    private bool _overlayDirty = true, _dragging;
    private Vector2 _pressPos;
    private bool _pressed;

    private sealed class UnitVis
    {
        public Vector2 Pos;
        public readonly Queue<Vector2> Path = new();
        public int Tile, Predicted = -1;
        public double Deadline;
    }

    public MapView(GameScreen screen, ClientWorld world)
    {
        _screen = screen; W = world;
        _chunkCols = (W.Grid.W + ChunkSize - 1) / ChunkSize;
        int rows = (W.Grid.H + ChunkSize - 1) / ChunkSize;
        for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < _chunkCols; cx++)
            {
                var c = new TerrainChunk(this, cx * ChunkSize, cy * ChunkSize, ChunkSize);
                _chunks.Add(c);
                AddChild(c);
            }
        _overlay = new MapOverlay(this);
        AddChild(_overlay);
        Cam = Center(W.Grid.Count / 2);
    }

    // ----------------------------------------------------------- geometry
    public Vector2 Center(int idx)
    {
        int x = idx % W.Grid.W, y = idx / W.Grid.W;
        return new Vector2(HexW * (x + 0.5f * (y & 1)), RowH * y);
    }

    public int Neighbor(int idx, int dir)
    {
        int x = idx % W.Grid.W, y = idx / W.Grid.W;
        var (dx, dy) = (y & 1) == 0 ? EvenRow[dir] : OddRow[dir];
        int nx = x + dx, ny = y + dy;
        return nx < 0 || ny < 0 || nx >= W.Grid.W || ny >= W.Grid.H ? -1 : ny * W.Grid.W + nx;
    }

    public int TileAt(Vector2 world)
    {
        int row = Mathf.Clamp(Mathf.RoundToInt(world.Y / RowH), 0, W.Grid.H - 1);
        int col = Mathf.Clamp(Mathf.RoundToInt(world.X / HexW - 0.5f * (row & 1)), 0, W.Grid.W - 1);
        int best = row * W.Grid.W + col;
        float bestD = Center(best).DistanceSquaredTo(world);
        foreach (int n in W.Grid.Nb[best])
        {
            float d = Center(n).DistanceSquaredTo(world);
            if (d < bestD) { bestD = d; best = n; }
        }
        return bestD <= R * R ? best : -1;
    }

    public Vector2 ScreenToWorld(Vector2 screen) => (screen - Position) / Zoom;
    public Vector2 MapPixelSize => new(HexW * (W.Grid.W + 0.5f), RowH * W.Grid.H);

    public void CenterOn(int tile) { Cam = Center(tile); ApplyCamera(); }

    public bool OnScreen(int tile)
    {
        var p = Center(tile) * Zoom + Position;
        var size = GetViewportRect().Size;
        return p.X > 80 && p.Y > 80 && p.X < size.X - 80 && p.Y < size.Y - 180;
    }

    public void ApplyCamera()
    {
        var size = GetViewportRect().Size;
        float minZoom = Mathf.Max(0.12f, Mathf.Min(size.X / (MapPixelSize.X + 200), size.Y / (MapPixelSize.Y + 200)) * 0.9f);
        Zoom = Mathf.Clamp(Zoom, Mathf.Min(minZoom, 1f), 2.2f);
        Cam = new Vector2(Mathf.Clamp(Cam.X, 0, MapPixelSize.X), Mathf.Clamp(Cam.Y, 0, MapPixelSize.Y));
        Scale = new Vector2(Zoom, Zoom);
        Position = size / 2 - Cam * Zoom;
        int lod = Zoom >= 0.7f ? 2 : Zoom >= 0.38f ? 1 : 0;
        if (lod != Lod)
        {
            Lod = lod;
            foreach (var c in _chunks) c.QueueRedraw();
        }
        _overlayDirty = true;
        _screen.Minimap?.QueueRedraw();
    }

    // -------------------------------------------------------------- state
    public void OnDelta(DeltaMsg d)
    {
        if (d.Full) foreach (var c in _chunks) c.QueueRedraw();
        foreach (var t in d.Tiles)
        {
            if (!W.Grid.Valid(t.Idx)) continue;
            MarkDirty(t.Idx);
            foreach (int n in W.Grid.Nb[t.Idx]) MarkDirty(n);
        }
        foreach (var u in W.Units.Values)
        {
            if (!_vis.TryGetValue(u.Id, out var v)) { _vis[u.Id] = new UnitVis { Pos = Center(u.Tile), Tile = u.Tile }; continue; }
            if (u.Tile == v.Tile) continue;
            v.Tile = u.Tile;
            if (v.Predicted != u.Tile) { v.Path.Clear(); v.Path.Enqueue(Center(u.Tile)); }
            v.Predicted = -1;
        }
        foreach (int id in _vis.Keys.Where(id => !W.Units.ContainsKey(id)).ToList()) _vis.Remove(id);
        if (SelectedUnit >= 0 && !W.Units.ContainsKey(SelectedUnit)) SelectedUnit = -1;
        foreach (var e in d.Events)
            if (e.Type == EventKind.Combat && W.Grid.Valid(e.B))
                _flashes.Add((Center(e.B), Now + 0.7, e.S == "1" ? new Color(1, 0.5f, 0.2f) : new Color(0.8f, 0.8f, 1f)));
        HoverPath = null;
        _overlayDirty = true;
    }

    private void MarkDirty(int idx)
    {
        int cx = idx % W.Grid.W / ChunkSize, cy = idx / W.Grid.W / ChunkSize;
        _chunks[cy * _chunkCols + cx].QueueRedraw();
    }

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Client-side prediction: start moving right away, the server confirms or corrects later.</summary>
    public void OrderMove(UnitDto u, int target)
    {
        if (u.Tile == target) return;
        var path = W.PredictPath(u, target);
        if (path == null) { _screen.Toast("Туда нет известного пути", true); return; }
        int seq = _screen.Net.SendCommand(CmdType.MoveUnit, u.Id, target);
        _moveSeq[seq] = u.Id;
        if (!_vis.TryGetValue(u.Id, out var v)) return;
        int moves = u.Moves, at = u.Tile, last = u.Tile;
        v.Path.Clear();
        foreach (int step in path)
        {
            if (moves <= 0) break;
            bool hostile = W.Units.Values.Any(o => o.Tile == step && o.Owner != W.MyId)
                           || (W.CityAt(step) is { } c && c.Owner != W.MyId);
            if (hostile) break; // the outcome of a fight is for the server to decide
            moves -= Rules.MoveCost(W.Tiles[at], W.Tiles[step]);
            v.Path.Enqueue(Center(step));
            at = last = step;
        }
        if (last != u.Tile)
        {
            v.Predicted = last;
            v.Deadline = Now + 1.5 + Math.Max(0, _screen.Net.PingMs) / 250.0;
        }
        _overlayDirty = true;
    }

    public void OnCommandResult(int seq, bool ok)
    {
        if (!_moveSeq.Remove(seq, out int unit) || ok) return;
        if (_vis.TryGetValue(unit, out var v)) Reconcile(v);
    }

    private void Reconcile(UnitVis v)
    {
        v.Predicted = -1;
        v.Path.Clear();
        v.Path.Enqueue(Center(v.Tile));
        _overlayDirty = true;
    }

    public override void _Process(double delta)
    {
        var pan = Vector2.Zero;
        if (!_screen.TypingInChat)
        {
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) pan.X -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) pan.X += 1;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) pan.Y -= 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) pan.Y += 1;
        }
        if (pan != Vector2.Zero) { Cam += pan * (float)delta * 900f / Zoom; ApplyCamera(); }

        double now = Now;
        bool moving = false;
        foreach (var v in _vis.Values)
        {
            if (v.Predicted >= 0 && now > v.Deadline) Reconcile(v);
            if (v.Path.Count == 0) continue;
            moving = true;
            float step = MoveSpeed * (float)delta * (v.Path.Count > 3 ? 2f : 1f);
            while (step > 0 && v.Path.Count > 0)
            {
                var target = v.Path.Peek();
                float dist = v.Pos.DistanceTo(target);
                if (dist > HexW * 6) { v.Pos = target; v.Path.Dequeue(); continue; } // far jump: no slow slide across the map
                if (dist <= step) { v.Pos = target; v.Path.Dequeue(); step -= dist; }
                else { v.Pos = v.Pos.MoveToward(target, step); step = 0; }
            }
        }
        _flashes.RemoveAll(f => f.until < now);
        if (moving || _flashes.Count > 0 || _overlayDirty)
        {
            _overlayDirty = false;
            _overlay.QueueRedraw();
            if (moving) OS.LowProcessorUsageMode = false;
        }
        else if (!OS.LowProcessorUsageMode) OS.LowProcessorUsageMode = true;
    }

    // -------------------------------------------------------------- input
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var before = ScreenToWorld(mb.Position);
                Zoom *= mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f;
                ApplyCamera();
                Cam += before - ScreenToWorld(mb.Position);
                ApplyCamera();
            }
            else if (mb.ButtonIndex is MouseButton.Left or MouseButton.Middle)
            {
                if (mb.Pressed) { _pressed = true; _dragging = mb.ButtonIndex == MouseButton.Middle; _pressPos = mb.Position; }
                else
                {
                    if (_pressed && !_dragging && mb.ButtonIndex == MouseButton.Left) _screen.TileClicked(TileAt(ScreenToWorld(mb.Position)));
                    _pressed = false; _dragging = false;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                _screen.TileOrdered(TileAt(ScreenToWorld(mb.Position)));
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_pressed && !_dragging && mm.Position.DistanceTo(_pressPos) > 6) _dragging = true;
            if (_dragging) { Cam -= mm.Relative / Zoom; ApplyCamera(); return; }
            int tile = TileAt(ScreenToWorld(mm.Position));
            if (tile == HoverTile) return;
            HoverTile = tile;
            HoverPath = null;
            if (tile >= 0 && SelectedUnit >= 0 && W.Units.TryGetValue(SelectedUnit, out var u) && u.Owner == W.MyId)
                HoverPath = W.PredictPath(u, tile);
            _screen.HoverChanged(tile);
            _overlayDirty = true;
        }
    }

    public void Select(int unitId) { SelectedUnit = unitId; HoverPath = null; _overlayDirty = true; }
    public void Invalidate() => _overlayDirty = true;

    // ------------------------------------------------------------ drawing
    public static Color TerrainColor(in IH.Shared.TileData t)
    {
        if (t.Relief == Relief.Mountain) return Ui.Col(0x6B6660);
        Color c = t.Terrain switch
        {
            Terrain.Ocean => Ui.Col(0x1B3A6B), Terrain.Coast => Ui.Col(0x2F6FA8), Terrain.Grassland => Ui.Col(0x4E9A3B),
            Terrain.Plains => Ui.Col(0xA8A04A), Terrain.Desert => Ui.Col(0xD9C27A), Terrain.Tundra => Ui.Col(0x8C9A8A),
            _ => Ui.Col(0xE6EEF2),
        };
        if (t.Relief == Relief.Hills) c = c.Darkened(0.14f);
        if (t.Forest) c = c.Lerp(Ui.Col(0x1F5A2A), 0.45f);
        return c;
    }

    public Color OwnerColor(int owner) => W.Players.TryGetValue(owner, out var p) ? Ui.Col(p.Color) : Colors.Gray;

    private static void Text(CanvasItem ci, Vector2 center, string text, int size, Color color)
    {
        var font = ThemeDB.FallbackFont;
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        ci.DrawString(font, new Vector2(center.X - w / 2, center.Y + size * 0.36f), text, HorizontalAlignment.Left, -1, size, color);
    }

    internal void DrawOverlay(MapOverlay o)
    {
        var view = new Rect2(ScreenToWorld(Vector2.Zero), GetViewportRect().Size / Zoom).Grow(R * 2);
        var pts = new Vector2[7];

        foreach (int t in Highlight)
            if (W.Grid.Valid(t)) o.DrawArc(Center(t), R * 0.55f, 0, Mathf.Tau, 20, new Color(1, 1, 1, 0.75f), 2.5f, true);

        // cities
        foreach (var c in W.Cities.Values)
        {
            if (W.Vis[c.Tile] == 0) continue;
            var p = Center(c.Tile);
            if (!view.HasPoint(p)) continue;
            var col = OwnerColor(c.Owner);
            for (int k = 0; k < 7; k++) pts[k] = p + Corner[k % 6] * 0.82f;
            o.DrawColoredPolygon(pts.AsSpan(0, 6).ToArray(), col.Darkened(0.35f));
            o.DrawPolyline(pts, c.Disorder ? Ui.Bad : Colors.White, 3f, true);
            if (Lod >= 1)
            {
                o.DrawRect(new Rect2(p + new Vector2(-9, -9), new Vector2(8, 16)), Colors.White.Darkened(0.1f));
                o.DrawRect(new Rect2(p + new Vector2(1, -3), new Vector2(9, 10)), Colors.White.Darkened(0.2f));
                string label = (c.Capital ? "★ " : "") + c.Name + "  " + c.Pop;
                int fs = Lod >= 2 ? 15 : 22;
                float w = ThemeDB.FallbackFont.GetStringSize(label, HorizontalAlignment.Left, -1, fs).X + 12;
                var box = new Rect2(p.X - w / 2, p.Y + R * 0.78f, w, fs + 8);
                o.DrawRect(box, new Color(0, 0, 0, 0.72f));
                o.DrawRect(new Rect2(box.Position, new Vector2(4, box.Size.Y)), col);
                Text(o, box.GetCenter() + new Vector2(2, 0), label, fs, c.Disorder ? Ui.Bad : Colors.White);
            }
        }

        // planned route of the selected unit
        if (SelectedUnit >= 0 && W.Units.TryGetValue(SelectedUnit, out var sel))
        {
            if (HoverPath is { Count: > 0 })
            {
                var line = new Vector2[HoverPath.Count + 1];
                line[0] = Center(sel.Tile);
                for (int i = 0; i < HoverPath.Count; i++) line[i + 1] = Center(HoverPath[i]);
                o.DrawPolyline(line, new Color(1, 1, 1, 0.8f), 3f, true);
                o.DrawCircle(line[^1], 6, Colors.White);
            }
            if (sel.Goto >= 0 && W.Grid.Valid(sel.Goto))
                o.DrawArc(Center(sel.Goto), 10, 0, Mathf.Tau, 16, Ui.Gold, 3f, true);
        }

        // units: stacks at rest show their top unit, moving units are drawn where they are
        var stacks = new Dictionary<int, List<UnitDto>>();
        var movers = new List<UnitDto>();
        foreach (var u in W.Units.Values)
        {
            if (!_vis.TryGetValue(u.Id, out var v) || !view.HasPoint(v.Pos)) continue;
            if (v.Path.Count > 0 || v.Pos.DistanceSquaredTo(Center(u.Tile)) > 1) { movers.Add(u); continue; }
            if (!stacks.TryGetValue(u.Tile, out var list)) stacks[u.Tile] = list = new List<UnitDto>();
            list.Add(u);
        }
        foreach (var (tile, list) in stacks)
        {
            var top = list.OrderByDescending(u => u.Id == SelectedUnit).ThenByDescending(u => u.Def.Str).ThenBy(u => u.Id).First();
            DrawUnit(o, top, Center(tile), list.Count);
        }
        foreach (var u in movers) DrawUnit(o, u, _vis[u.Id].Pos, 1);

        double now = Now;
        foreach (var (pos, until, color) in _flashes)
        {
            float k = (float)Math.Clamp((until - now) / 0.7, 0, 1);
            o.DrawArc(pos, R * (1.1f - 0.6f * k), 0, Mathf.Tau, 24, new Color(color, k), 6f * k + 1, true);
        }
    }

    private void DrawUnit(MapOverlay o, UnitDto u, Vector2 p, int count)
    {
        var col = OwnerColor(u.Owner);
        bool mine = u.Owner == W.MyId, selected = u.Id == SelectedUnit;
        if (Lod == 0)
        {
            o.DrawCircle(p, 20, col);
            o.DrawArc(p, 20, 0, Mathf.Tau, 12, Colors.White, 4f);
            return;
        }
        float r = 15f;
        bool civilian = u.Def.Role is UnitRole.Settler or UnitRole.Worker;
        if (civilian)
        {
            var diamond = new[] { p + new Vector2(0, -r - 2), p + new Vector2(r + 2, 0), p + new Vector2(0, r + 2), p + new Vector2(-r - 2, 0) };
            o.DrawColoredPolygon(diamond, col);
            o.DrawPolyline(diamond.Append(diamond[0]).ToArray(), selected ? Ui.Gold : Colors.White, selected ? 4f : 2f, true);
        }
        else
        {
            o.DrawCircle(p, r, col);
            o.DrawArc(p, r, 0, Mathf.Tau, 24, selected ? Ui.Gold : Colors.White, selected ? 4f : 2f, true);
        }
        if (selected) o.DrawArc(p, r + 7, 0, Mathf.Tau, 28, new Color(Ui.Gold, 0.6f), 2f, true);
        bool ready = mine && u.Moves > 0 && !u.Fortified && u.Work == 0 && u.Goto < 0;
        string glyph = u.Def.Name.Length >= 2 ? u.Def.Name[..2] : u.Def.Name;
        Text(o, p, glyph, 13, ready || !mine ? Colors.White : new Color(1, 1, 1, 0.6f));
        if (u.Hp < 100)
        {
            o.DrawRect(new Rect2(p.X - r, p.Y + r + 3, 2 * r, 4), new Color(0, 0, 0, 0.8f));
            o.DrawRect(new Rect2(p.X - r, p.Y + r + 3, 2 * r * u.Hp / 100f, 4), u.Hp > 50 ? Ui.Good : Ui.Bad);
        }
        if (u.Fortified) o.DrawRect(new Rect2(p.X + r - 5, p.Y - r - 3, 9, 9), Colors.White);
        if (u.Work != 0) o.DrawCircle(p + new Vector2(r - 2, -r + 1), 5, Ui.Gold);
        if (count > 1)
        {
            o.DrawCircle(p + new Vector2(-r + 1, -r + 1), 8, Colors.Black);
            Text(o, p + new Vector2(-r + 1, -r + 1), count.ToString(), 11, Colors.White);
        }
    }

    private static readonly string[] ResourceGlyph = { "", "П", "С", "Р", "Ж", "Л", "З", "К", "Н", "У" };
    private static readonly int[] ResourceColor = { 0, 0xE8D060, 0xB07A50, 0x70C8E8, 0x9098A8, 0xC89060, 0xFFD700, 0xC8C8C8, 0x303030, 0x70E070 };

    internal void DrawChunk(TerrainChunk ch, int x0, int y0, int size)
    {
        var pts = new Vector2[6];
        var outline = new Vector2[7];
        for (int y = y0; y < Math.Min(y0 + size, W.Grid.H); y++)
            for (int x = x0; x < Math.Min(x0 + size, W.Grid.W); x++)
            {
                int idx = y * W.Grid.W + x;
                byte vis = W.Vis[idx];
                if (vis == 0) continue;
                var t = W.Tiles[idx];
                var c = Center(idx);
                var color = TerrainColor(t);
                if (t.Owner >= 0) color = color.Lerp(OwnerColor(t.Owner), 0.16f);
                float fog = vis == 1 ? 0.45f : 0f;
                if (fog > 0) color = color.Darkened(fog);
                for (int k = 0; k < 6; k++) { pts[k] = c + Corner[k] * 1.02f; outline[k] = c + Corner[k]; }
                outline[6] = outline[0];
                ch.DrawColoredPolygon(pts, color);

                if (Lod >= 1)
                {
                    ch.DrawPolyline(outline, new Color(0, 0, 0, 0.13f), 1f);
                    if (t.Relief == Relief.Mountain)
                    {
                        ch.DrawColoredPolygon(new[] { c + new Vector2(-17, 12), c + new Vector2(0, -18), c + new Vector2(17, 12) }, Ui.Col(0x4F4A46).Darkened(fog));
                        ch.DrawColoredPolygon(new[] { c + new Vector2(-6, -7), c + new Vector2(0, -18), c + new Vector2(6, -7) }, Ui.Col(0xF2F2F2).Darkened(fog));
                    }
                    else if (t.Relief == Relief.Hills)
                    {
                        var hill = color.Darkened(0.3f);
                        ch.DrawArc(c + new Vector2(-9, 9), 10, Mathf.Pi, Mathf.Tau, 8, hill, 2.5f);
                        ch.DrawArc(c + new Vector2(8, 5), 12, Mathf.Pi, Mathf.Tau, 8, hill, 2.5f);
                    }
                    if (t.Forest)
                    {
                        var tree = Ui.Col(0x174A22).Darkened(fog);
                        foreach (var off in new[] { new Vector2(-11, 2), new Vector2(3, -9), new Vector2(11, 6) })
                            ch.DrawColoredPolygon(new[] { c + off + new Vector2(-6, 6), c + off + new Vector2(0, -8), c + off + new Vector2(6, 6) }, tree);
                    }
                    if (t.Road && t.CityId < 0)
                    {
                        var road = Ui.Col(0x6A4A2A).Darkened(fog);
                        bool any = false;
                        for (int d = 0; d < 6; d++)
                        {
                            int n = Neighbor(idx, d);
                            if (n < 0 || W.Vis[n] == 0 || !W.Tiles[n].Road) continue;
                            ch.DrawLine(c, (c + Center(n)) / 2, road, 5f);
                            any = true;
                        }
                        ch.DrawCircle(c, any ? 3f : 5f, road);
                    }
                }
                if (Lod >= 2)
                {
                    switch (t.Improvement)
                    {
                        case Improvement.Farm:
                            for (int i = -1; i <= 1; i++)
                                ch.DrawLine(c + new Vector2(-12, 14 + i * 5), c + new Vector2(12, 14 + i * 5), Ui.Col(0xE6D36A).Darkened(fog), 2f);
                            break;
                        case Improvement.Mine:
                            ch.DrawColoredPolygon(new[] { c + new Vector2(-9, 20), c + new Vector2(0, 8), c + new Vector2(9, 20) }, Ui.Col(0x3A3A3A));
                            break;
                        case Improvement.Cottage:
                            ch.DrawRect(new Rect2(c + new Vector2(-7, 11), new Vector2(14, 9)), Ui.Col(0xE8D8B8).Darkened(fog));
                            ch.DrawColoredPolygon(new[] { c + new Vector2(-9, 11), c + new Vector2(0, 4), c + new Vector2(9, 11) }, Ui.Col(0xA04030).Darkened(fog));
                            break;
                    }
                    if (t.Resource != Resource.None)
                    {
                        var rp = c + new Vector2(0, -R * 0.52f);
                        var rc = Ui.Col(ResourceColor[(int)t.Resource]);
                        ch.DrawCircle(rp, 8.5f, Colors.Black);
                        ch.DrawCircle(rp, 7.5f, rc.Darkened(fog));
                        Text(ch, rp, ResourceGlyph[(int)t.Resource], 10, rc.Luminance > 0.5f ? Colors.Black : Colors.White);
                    }
                }

                if (t.Owner < 0) continue;
                var border = OwnerColor(t.Owner);
                for (int d = 0; d < 6; d++)
                {
                    int n = Neighbor(idx, d);
                    if (n >= 0 && W.Vis[n] > 0 && W.Tiles[n].Owner == t.Owner) continue;
                    ch.DrawLine(c + Corner[Edge[d].a] * 0.93f, c + Corner[Edge[d].b] * 0.93f, border, Lod == 0 ? 6f : 3.5f);
                }
            }
    }
}

public partial class TerrainChunk : Node2D
{
    private readonly MapView _map;
    private readonly int _x0, _y0, _size;
    public TerrainChunk(MapView map, int x0, int y0, int size) { _map = map; _x0 = x0; _y0 = y0; _size = size; }
    public override void _Draw() => _map.DrawChunk(this, _x0, _y0, _size);
}

public partial class MapOverlay : Node2D
{
    private readonly MapView _map;
    public MapOverlay(MapView map) { _map = map; }
    public override void _Draw() => _map.DrawOverlay(this);
}
