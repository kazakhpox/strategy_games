using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

/// <summary>
/// 3D map renderer and map input. Terrain is built into static chunk meshes (rebuilt only when their tiles
/// change); decorations use distance-based visibility as level of detail; units are separate nodes that are
/// interpolated between tiles, with client-side prediction of the player's own moves.
/// </summary>
public partial class MapView : Node3D
{
    public const float R = 1f;
    public const float RowH = 1.5f * R;
    public static readonly float HexW = Mathf.Sqrt(3f) * R;
    private const int ChunkSize = 12;
    private const float MoveSpeed = 9f;
    private const float BaseY = -0.6f, BaseDistance = 20f;
    private static readonly float Pitch = Mathf.DegToRad(52);

    private static readonly Vector3[] Corner = Enumerable.Range(0, 6)
        .Select(k => new Vector3(Mathf.Cos(Mathf.DegToRad(60 * k - 30)), 0, Mathf.Sin(Mathf.DegToRad(60 * k - 30))) * R).ToArray();
    // directions E, NE, NW, W, SW, SE and the corner pair forming the shared edge
    private static readonly (int dx, int dy)[] EvenRow = { (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1) };
    private static readonly (int dx, int dy)[] OddRow = { (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1) };
    // direction whose shared edge runs from corner k to corner k+1
    private static readonly int[] DirOfCorner = { 0, 5, 4, 3, 2, 1 };
    private static readonly (int a, int b)[] Edge = { (0, 1), (5, 0), (4, 5), (3, 4), (2, 3), (1, 2) };

    public readonly ClientWorld W;
    private readonly GameScreen _screen;
    private readonly Camera3D _camera = new() { Fov = 42, Near = 0.5f, Far = 600 };
    private readonly Chunk[] _chunks;
    private readonly int _chunkCols;
    private readonly HashSet<int> _dirtyChunks = new();
    private readonly Dictionary<int, UnitNode> _units = new();
    private readonly Dictionary<int, CityNode> _cities = new();
    private readonly Dictionary<int, int> _moveSeq = new();
    private readonly Dictionary<int, StandardMaterial3D> _ownerMats = new();
    private readonly List<(int tile, double until, Color color)> _flashes = new();
    private readonly ImmediateMesh _lines = new();
    private readonly StandardMaterial3D _terrainMat = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1f };
    private readonly Mesh _soldierMesh = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.36f, Height = 0.62f, RadialSegments = 12 };
    private readonly Mesh _headMesh = new SphereMesh { Radius = 0.17f, Height = 0.34f, RadialSegments = 10, Rings = 5 };
    private readonly Mesh _civilMesh = new BoxMesh { Size = new Vector3(0.46f, 0.4f, 0.46f) };
    private readonly Mesh _hullMesh = new BoxMesh { Size = new Vector3(0.36f, 0.24f, 0.95f) };
    private readonly Mesh _sailMesh = new BoxMesh { Size = new Vector3(0.05f, 0.6f, 0.36f) };
    private readonly Mesh _ringMesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f, Rings = 24, RingSegments = 6 };
    private readonly Mesh _houseMesh = new BoxMesh { Size = Vector3.One };
    private readonly StandardMaterial3D _ringMat = new() { AlbedoColor = new Color(1f, 0.85f, 0.25f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    private readonly StandardMaterial3D _wallMat = new() { AlbedoColor = new Color(0.93f, 0.9f, 0.82f), Roughness = 1f };

    /// <summary>Zoom factor: 1 = default distance, smaller = further away.</summary>
    public float Zoom = 1f;
    /// <summary>Ground point the camera looks at, in map units (x, z).</summary>
    public Vector2 Cam;
    public float Yaw;
    public int Lod = 2;
    public int SelectedUnit = -1, HoverTile = -1;
    public List<int>? HoverPath;
    public int[] Highlight = Array.Empty<int>();
    private bool _dirty = true, _dragging, _pressed;
    private Vector2 _pressPos;

    private sealed class Chunk
    {
        public MeshInstance3D Terrain = null!, Decor = null!;
        public int X0, Y0;
    }

    private sealed class UnitNode
    {
        public Node3D Root = null!;
        public MeshInstance3D Body = null!, Head = null!, Ring = null!;
        public Label3D Label = null!;
        public int Type = -1, Owner = -1;
        public Vector3 Pos;
        public readonly Queue<Vector3> Path = new();
        public int Tile, Predicted = -1;
        public double Deadline;
    }

    private sealed class CityNode
    {
        public Node3D Root = null!;
        public List<MeshInstance3D> Houses = new();
        public int Owner = -1, Pop = -1;
    }

    public MapView(GameScreen screen, ClientWorld world)
    {
        _screen = screen; W = world;
        _chunkCols = (W.Grid.W + ChunkSize - 1) / ChunkSize;
        int rows = (W.Grid.H + ChunkSize - 1) / ChunkSize;
        _chunks = new Chunk[_chunkCols * rows];
        for (int cy = 0; cy < rows; cy++)
            for (int cx = 0; cx < _chunkCols; cx++)
            {
                var c = new Chunk { X0 = cx * ChunkSize, Y0 = cy * ChunkSize };
                c.Terrain = new MeshInstance3D { MaterialOverride = _terrainMat };
                // level of detail: trees, resources and improvements disappear when the camera is far away
                c.Decor = new MeshInstance3D { MaterialOverride = _terrainMat, VisibilityRangeEnd = 46f, VisibilityRangeEndMargin = 4f };
                AddChild(c.Terrain); AddChild(c.Decor);
                _chunks[cy * _chunkCols + cx] = c;
            }

        AddChild(_camera);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-52, 35, 0), LightEnergy = 1.05f, ShadowEnabled = false });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.043f, 0.055f, 0.086f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = 0.6f,
            },
        });
        AddChild(new MeshInstance3D
        {
            Mesh = _lines,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        });
        Cam = Ground(W.Grid.Count / 2);
    }

    // ----------------------------------------------------------- geometry
    private Vector2 Ground(int idx)
    {
        int x = idx % W.Grid.W, y = idx / W.Grid.W;
        return new Vector2(HexW * (x + 0.5f * (y & 1)), RowH * y);
    }

    public static float Height(in TileData t)
    {
        if (t.Terrain == Terrain.Ocean) return -0.16f;
        if (t.Terrain == Terrain.Coast) return -0.08f;
        return t.Relief switch { Relief.Hills => 0.34f, Relief.Mountain => 0.42f, _ => 0.12f };
    }

    public Vector3 Center(int idx)
    {
        var g = Ground(idx);
        return new Vector3(g.X, Height(W.Tiles[idx]), g.Y);
    }

    private int Neighbor(int idx, int dir)
    {
        int x = idx % W.Grid.W, y = idx / W.Grid.W;
        var (dx, dy) = (y & 1) == 0 ? EvenRow[dir] : OddRow[dir];
        int nx = x + dx, ny = y + dy;
        return nx < 0 || ny < 0 || nx >= W.Grid.W || ny >= W.Grid.H ? -1 : ny * W.Grid.W + nx;
    }

    private int TileAtGround(Vector2 p)
    {
        int row = Mathf.Clamp(Mathf.RoundToInt(p.Y / RowH), 0, W.Grid.H - 1);
        int col = Mathf.Clamp(Mathf.RoundToInt(p.X / HexW - 0.5f * (row & 1)), 0, W.Grid.W - 1);
        int best = row * W.Grid.W + col;
        float bestD = Ground(best).DistanceSquaredTo(p);
        foreach (int n in W.Grid.Nb[best])
        {
            float d = Ground(n).DistanceSquaredTo(p);
            if (d < bestD) { bestD = d; best = n; }
        }
        return bestD <= R * R ? best : -1;
    }

    /// <summary>Tile under a screen position: a ray from the camera is intersected with the terrain height.</summary>
    public int TileAt(Vector2 screen)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var dir = _camera.ProjectRayNormal(screen);
        if (dir.Y > -0.01f) return -1;
        float h = 0.12f;
        int tile = -1;
        for (int pass = 0; pass < 3; pass++)
        {
            var hit = origin + dir * ((h - origin.Y) / dir.Y);
            tile = TileAtGround(new Vector2(hit.X, hit.Z));
            if (tile < 0) return -1;
            float th = Height(W.Tiles[tile]);
            if (Mathf.Abs(th - h) < 0.01f) break;
            h = th;
        }
        return tile;
    }

    public Vector2 MapPixelSize => new(HexW * (W.Grid.W + 0.5f), RowH * W.Grid.H);
    private float Distance => BaseDistance / Zoom;
    /// <summary>Approximate size of the ground area in view, for the minimap frame.</summary>
    public Vector2 ViewSize => new(Distance * 1.25f, Distance * 0.95f);

    public Vector2 ScreenPos(int tile) => _camera.UnprojectPosition(Center(tile) + new Vector3(0, 0.3f, 0));

    /// <summary>Screen position of a point lifted above a tile; false if it is behind the camera.</summary>
    public bool Project(int tile, float lift, out Vector2 screen)
    {
        var p = Center(tile) + new Vector3(0, lift, 0);
        screen = _camera.UnprojectPosition(p);
        return !_camera.IsPositionBehind(p);
    }

    public void CenterOn(int tile) { Cam = Ground(tile); ApplyCamera(); }

    /// <summary>Centres on a tile but leaves it shifted on screen: a positive X puts the tile left of the middle.</summary>
    public void CenterOn(int tile, Vector2 screenShift)
    {
        float s = Distance * 0.0016f;
        Cam = Ground(tile) + RightDir * screenShift.X * s - ForwardDir * screenShift.Y * s * 1.5f;
        ApplyCamera();
    }

    public bool OnScreen(int tile)
    {
        var world = Center(tile);
        if (_camera.IsPositionBehind(world)) return false;
        var p = _camera.UnprojectPosition(world);
        var size = GetViewport().GetVisibleRect().Size;
        return p.X > 80 && p.Y > 80 && p.X < size.X - 80 && p.Y < size.Y - 180;
    }

    public void ApplyCamera()
    {
        Zoom = Mathf.Clamp(Zoom, 0.14f, 2.4f);
        Cam = new Vector2(Mathf.Clamp(Cam.X, 0, MapPixelSize.X), Mathf.Clamp(Cam.Y, 0, MapPixelSize.Y));
        var focus = new Vector3(Cam.X, 0, Cam.Y);
        float d = Distance;
        var offset = new Vector3(Mathf.Sin(Yaw) * Mathf.Cos(Pitch), Mathf.Sin(Pitch), Mathf.Cos(Yaw) * Mathf.Cos(Pitch)) * d;
        _camera.Position = focus + offset;
        _camera.LookAt(focus, Vector3.Up);
        int lod = Zoom >= 0.7f ? 2 : Zoom >= 0.38f ? 1 : 0;
        if (lod != Lod) { Lod = lod; _dirty = true; }
        _screen.Minimap?.QueueRedraw();
    }

    // -------------------------------------------------------------- state
    public void OnDelta(DeltaMsg d)
    {
        if (d.Full) for (int i = 0; i < _chunks.Length; i++) _dirtyChunks.Add(i);
        foreach (var t in d.Tiles)
        {
            if (!W.Grid.Valid(t.Idx)) continue;
            MarkDirty(t.Idx);
            foreach (int n in W.Grid.Nb[t.Idx]) MarkDirty(n);
        }
        foreach (var u in W.Units.Values)
        {
            if (!_units.TryGetValue(u.Id, out var v)) { _units[u.Id] = CreateUnit(u); continue; }
            if (u.Tile == v.Tile) continue;
            v.Tile = u.Tile;
            if (v.Predicted != u.Tile) { v.Path.Clear(); v.Path.Enqueue(Center(u.Tile)); }
            v.Predicted = -1;
        }
        foreach (int id in _units.Keys.Where(id => !W.Units.ContainsKey(id)).ToList())
        {
            _units[id].Root.QueueFree();
            _units.Remove(id);
        }
        if (SelectedUnit >= 0 && !W.Units.ContainsKey(SelectedUnit)) SelectedUnit = -1;
        foreach (var e in d.Events)
            if (e.Type == EventKind.Combat && W.Grid.Valid(e.B))
                _flashes.Add((e.B, Now + 0.8, e.S == "1" ? new Color(1, 0.45f, 0.15f) : new Color(0.8f, 0.85f, 1f)));
            else if (e.Type == EventKind.Ranged && W.Grid.Valid(e.B))
                _flashes.Add((e.B, Now + 0.9, new Color(1f, 0.75f, 0.2f))); // shots, spells and tower fire
        HoverPath = null;
        _dirty = true;
    }

    private void MarkDirty(int idx) => _dirtyChunks.Add(idx / W.Grid.W / ChunkSize * _chunkCols + idx % W.Grid.W / ChunkSize);

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Client-side prediction: start moving right away, the server confirms or corrects later.</summary>
    public void OrderMove(UnitDto u, int target)
    {
        if (u.Tile == target) return;
        var path = W.PredictPath(u, target);
        if (path == null) { _screen.Toast("Туда нет известного пути", true); return; }
        int seq = _screen.Net.SendCommand(CmdType.MoveUnit, u.Id, target);
        _moveSeq[seq] = u.Id;
        if (!_units.TryGetValue(u.Id, out var v)) return;
        int moves = u.Moves, at = u.Tile, last = u.Tile;
        var mover = Rules.MoverFor(u.Def, W.Knows);
        v.Path.Clear();
        foreach (int step in path)
        {
            if (moves <= 0) break;
            bool hostile = W.Units.Values.Any(o => o.Tile == step && o.Owner != W.MyId)
                           || (W.CityAt(step) is { } c && c.Owner != W.MyId);
            if (hostile) break; // the outcome of a fight is for the server to decide
            moves -= Rules.MoveCost(W.Tiles[at], W.Tiles[step], mover);
            v.Path.Enqueue(Center(step));
            at = last = step;
        }
        if (last != u.Tile)
        {
            v.Predicted = last;
            v.Deadline = Now + 1.5 + Math.Max(0, _screen.Net.PingMs) / 250.0;
        }
        _dirty = true;
    }

    public void OnCommandResult(int seq, bool ok)
    {
        if (!_moveSeq.Remove(seq, out int unit) || ok) return;
        if (_units.TryGetValue(unit, out var v)) Reconcile(v);
    }

    private void Reconcile(UnitNode v)
    {
        v.Predicted = -1;
        v.Path.Clear();
        v.Path.Enqueue(Center(v.Tile));
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        var pan = Vector2.Zero;
        float turn = 0;
        if (!_screen.TypingInChat)
        {
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) pan.X -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) pan.X += 1;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) pan.Y += 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) pan.Y -= 1;
            if (Input.IsKeyPressed(Key.Q)) turn += 1;
            if (Input.IsKeyPressed(Key.E)) turn -= 1;
        }
        if (pan != Vector2.Zero || turn != 0)
        {
            Yaw += turn * (float)delta * 1.6f;
            Cam += (RightDir * pan.X + ForwardDir * pan.Y) * (float)delta * Distance * 0.9f;
            ApplyCamera();
        }

        if (_dirtyChunks.Count > 0)
        {
            foreach (int c in _dirtyChunks) RebuildChunk(_chunks[c]);
            _dirtyChunks.Clear();
            _dirty = true;
        }

        double now = Now;
        bool moving = false;
        foreach (var v in _units.Values)
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
            v.Root.Position = v.Pos;
        }
        _flashes.RemoveAll(f => f.until < now);
        if (moving || _flashes.Count > 0 || _dirty)
        {
            bool full = _dirty;
            _dirty = false;
            if (full || moving) SyncUnits();
            if (full) SyncCities();
            DrawLines();
            if (moving || _flashes.Count > 0) OS.LowProcessorUsageMode = false;
        }
        else if (!OS.LowProcessorUsageMode) OS.LowProcessorUsageMode = true;
    }

    private Vector2 ForwardDir => new(-Mathf.Sin(Yaw), -Mathf.Cos(Yaw));
    private Vector2 RightDir => new(Mathf.Cos(Yaw), -Mathf.Sin(Yaw));

    // -------------------------------------------------------------- input
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.Pressed && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                Zoom *= mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f;
                ApplyCamera();
            }
            else if (mb.ButtonIndex is MouseButton.Left or MouseButton.Middle)
            {
                if (mb.Pressed) { _pressed = true; _dragging = mb.ButtonIndex == MouseButton.Middle; _pressPos = mb.Position; }
                else
                {
                    if (_pressed && !_dragging && mb.ButtonIndex == MouseButton.Left && !_screen.BannerClicked(mb.Position)) _screen.TileClicked(TileAt(mb.Position));
                    _pressed = false; _dragging = false;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                _screen.TileOrdered(TileAt(mb.Position));
        }
        else if (e is InputEventMouseMotion mm)
        {
            if (_pressed && !_dragging && mm.Position.DistanceTo(_pressPos) > 6) _dragging = true;
            if (_dragging)
            {
                if (Input.IsKeyPressed(Key.Shift) || Input.IsMouseButtonPressed(MouseButton.Middle) && Input.IsKeyPressed(Key.Ctrl))
                    Yaw -= mm.Relative.X * 0.006f;
                else
                {
                    float s = Distance * 0.0016f;
                    Cam += ForwardDir * mm.Relative.Y * s * 1.5f - RightDir * mm.Relative.X * s;
                }
                ApplyCamera();
                return;
            }
            int tile = TileAt(mm.Position);
            if (tile == HoverTile) return;
            HoverTile = tile;
            HoverPath = null;
            if (tile >= 0 && SelectedUnit >= 0 && W.Units.TryGetValue(SelectedUnit, out var u) && u.Owner == W.MyId)
                HoverPath = W.PredictPath(u, tile);
            _screen.HoverChanged(tile);
            _dirty = true;
        }
    }

    public void Select(int unitId) { SelectedUnit = unitId; HoverPath = null; _dirty = true; }
    public void Invalidate() => _dirty = true;

    // ------------------------------------------------------------- colors
    public static Color TerrainColor(in TileData t)
    {
        if (t.Relief == Relief.Mountain) return Ui.Col(0x6B6660);
        Color c = t.Terrain switch
        {
            Terrain.Ocean => Ui.Col(0x1B3A6B), Terrain.Coast => Ui.Col(0x2F6FA8), Terrain.Grassland => Ui.Col(0x4E9A3B),
            Terrain.Plains => Ui.Col(0xA8A04A), Terrain.Desert => Ui.Col(0xD9C27A), Terrain.Tundra => Ui.Col(0x8C9A8A),
            _ => Ui.Col(0xE6EEF2),
        };
        if (t.Relief == Relief.Hills) c = c.Darkened(0.1f);
        if (t.Forest) c = c.Lerp(Ui.Col(0x1F5A2A), 0.35f);
        return c;
    }

    public Color OwnerColor(int owner) => W.Players.TryGetValue(owner, out var p) ? Ui.Col(p.Color) : Colors.Gray;

    private StandardMaterial3D OwnerMat(int owner)
    {
        if (!_ownerMats.TryGetValue(owner, out var m))
            _ownerMats[owner] = m = new StandardMaterial3D { AlbedoColor = OwnerColor(owner), Roughness = 0.8f };
        return m;
    }

    // ------------------------------------------------------- mesh helpers
    /// <summary>Marker colour by resource class: bonus green, strategic red, luxury violet.</summary>
    public static Color ResourceColor(Resource r) => Rules.Res(r).Class switch
    {
        ResClass.Bonus => Ui.Col(0x7BD35A), ResClass.Strategic => Ui.Col(0xE0503C), _ => Ui.Col(0xC77BEA),
    };

    private void RebuildChunk(Chunk ch)
    {
        var terrain = new MeshBuilder();
        var decor = new MeshBuilder();
        for (int y = ch.Y0; y < Math.Min(ch.Y0 + ChunkSize, W.Grid.H); y++)
            for (int x = ch.X0; x < Math.Min(ch.X0 + ChunkSize, W.Grid.W); x++)
            {
                int idx = y * W.Grid.W + x;
                byte vis = W.Vis[idx];
                if (vis == 0) continue;
                var t = W.Tiles[idx];
                var c = Center(idx);
                float fog = vis == 1 ? 0.5f : 0f;
                Color Shade(Color col) => fog > 0 ? col.Darkened(fog) : col;
                var color = TerrainColor(t);
                if (t.Owner >= 0) color = color.Lerp(OwnerColor(t.Owner), 0.14f);
                color = Shade(color);
                var side = color.Darkened(0.35f);

                for (int k = 0; k < 6; k++)
                {
                    var a = c + Corner[k]; var b = c + Corner[(k + 1) % 6];
                    terrain.Tri(c, a, b, Vector3.Up, color);
                    // a wall is only needed where the neighbour is lower or not drawn at all
                    int n = Neighbor(idx, DirOfCorner[k]);
                    float bottom = n >= 0 && W.Vis[n] > 0 ? Height(W.Tiles[n]) : BaseY;
                    if (bottom < c.Y - 0.001f)
                        terrain.Quad(a, b, new Vector3(b.X, bottom, b.Z), new Vector3(a.X, bottom, a.Z), (a + b) / 2 - c, side);
                }

                if (t.Relief == Relief.Mountain)
                {
                    terrain.Pyramid(c, 0.86f, 1.15f, 6, Shade(Ui.Col(0x5A5550)), Mathf.Pi / 6);
                    terrain.Pyramid(c + new Vector3(0, 0.72f, 0), 0.33f, 0.46f, 6, Shade(Ui.Col(0xF4F6F8)), Mathf.Pi / 6);
                }
                else if (t.Relief == Relief.Hills)
                {
                    terrain.Pyramid(c + new Vector3(-0.3f, 0, 0.18f), 0.5f, 0.2f, 7, Shade(TerrainColor(t).Lightened(0.08f)));
                    terrain.Pyramid(c + new Vector3(0.32f, 0, -0.2f), 0.42f, 0.16f, 7, Shade(TerrainColor(t).Lightened(0.04f)));
                }

                // borders: a raised strip along every edge where the owner changes
                if (t.Owner >= 0)
                {
                    var bc = Shade(OwnerColor(t.Owner).Lightened(0.15f));
                    for (int d = 0; d < 6; d++)
                    {
                        int n = Neighbor(idx, d);
                        if (n >= 0 && W.Vis[n] > 0 && W.Tiles[n].Owner == t.Owner) continue;
                        var a = c + Corner[Edge[d].a] * 0.97f + new Vector3(0, 0.035f, 0);
                        var b = c + Corner[Edge[d].b] * 0.97f + new Vector3(0, 0.035f, 0);
                        var a2 = c + Corner[Edge[d].a] * 0.8f + new Vector3(0, 0.035f, 0);
                        var b2 = c + Corner[Edge[d].b] * 0.8f + new Vector3(0, 0.035f, 0);
                        terrain.Quad(a, b, b2, a2, Vector3.Up, bc);
                    }
                }

                if (t.Road && t.CityId < 0)
                {
                    var road = Shade(Ui.Col(0x7A5A36));
                    var up = new Vector3(0, 0.03f, 0);
                    for (int d = 0; d < 6; d++)
                    {
                        int n = Neighbor(idx, d);
                        if (n < 0 || W.Vis[n] == 0 || !W.Tiles[n].Road) continue;
                        var to = (Corner[Edge[d].a] + Corner[Edge[d].b]) / 2;
                        var w = to.Cross(Vector3.Up).Normalized() * 0.09f;
                        terrain.Quad(c + up - w, c + up + w, c + to + up + w, c + to + up - w, Vector3.Up, road);
                    }
                    terrain.Pyramid(c + up, 0.14f, 0.01f, 6, road);
                }

                // ---- decorations (hidden at a distance)
                if (t.Forest)
                {
                    var leaf = Shade(Ui.Col(0x1E5C2B));
                    var trunkTop = new Vector3(0, 0.1f, 0);
                    foreach (var off in new[] { new Vector3(-0.42f, 0, 0.1f), new Vector3(0.1f, 0, -0.42f), new Vector3(0.4f, 0, 0.3f), new Vector3(-0.05f, 0, 0.45f) })
                    {
                        // a clearing around the resource, so the trees do not hide it
                        if (t.Resource != Resource.None && (off - new Vector3(-0.34f, 0, -0.32f)).Length() < 0.5f) continue;
                        decor.Pyramid(c + off + trunkTop, 0.21f, 0.5f, 5, leaf);
                        decor.Pyramid(c + off + trunkTop * 3.4f, 0.15f, 0.36f, 5, leaf.Lightened(0.08f));
                    }
                }
                switch (t.Improvement)
                {
                    case Improvement.Farm:
                        for (int i = -1; i <= 1; i++)
                            decor.Box(c + new Vector3(0.05f, 0.03f, 0.38f + i * 0.16f), new Vector3(0.8f, 0.05f, 0.09f), Shade(Ui.Col(0xE6D36A)));
                        break;
                    case Improvement.Mine:
                        decor.Pyramid(c + new Vector3(0.3f, 0, 0.35f), 0.2f, 0.28f, 4, Shade(Ui.Col(0x2E2E2E)));
                        decor.Box(c + new Vector3(0.05f, 0.08f, 0.4f), new Vector3(0.16f, 0.16f, 0.16f), Shade(Ui.Col(0x6A5038)));
                        break;
                    case Improvement.Plantation:
                        for (int i = -1; i <= 1; i++)
                            for (int j = 0; j < 2; j++)
                                decor.Pyramid(c + new Vector3(0.05f + i * 0.24f, 0, 0.32f + j * 0.2f), 0.09f, 0.16f, 5, Shade(Ui.Col(0x3FA34A)));
                        break;
                    case Improvement.Quarry:
                        decor.Box(c + new Vector3(0.1f, 0.07f, 0.4f), new Vector3(0.34f, 0.14f, 0.26f), Shade(Ui.Col(0xB8B8B0)));
                        decor.Box(c + new Vector3(0.36f, 0.05f, 0.22f), new Vector3(0.18f, 0.1f, 0.18f), Shade(Ui.Col(0x9A9A92)));
                        break;
                    case Improvement.Pasture:
                        foreach (var (px, pz, sx, sz) in new[] { (0.1f, 0.2f, 0.6f, 0.04f), (0.1f, 0.6f, 0.6f, 0.04f), (-0.2f, 0.4f, 0.04f, 0.4f), (0.4f, 0.4f, 0.04f, 0.4f) })
                            decor.Box(c + new Vector3(px, 0.07f, pz), new Vector3(sx, 0.1f, sz), Shade(Ui.Col(0x8A6A44)));
                        break;
                    case Improvement.Camp:
                        decor.Pyramid(c + new Vector3(0.22f, 0, 0.4f), 0.2f, 0.26f, 4, Shade(Ui.Col(0xD8C49A)), Mathf.Pi / 4);
                        break;
                    case Improvement.FishingBoats:
                        decor.Box(c + new Vector3(0.25f, 0.05f, 0.35f), new Vector3(0.3f, 0.08f, 0.12f), Shade(Ui.Col(0x7A5232)));
                        decor.Box(c + new Vector3(-0.2f, 0.05f, 0.45f), new Vector3(0.12f, 0.08f, 0.28f), Shade(Ui.Col(0x7A5232)));
                        decor.Box(c + new Vector3(0.25f, 0.2f, 0.35f), new Vector3(0.03f, 0.26f, 0.03f), Shade(Colors.White));
                        break;
                    case Improvement.OilWell:
                        decor.Pyramid(c + new Vector3(0.25f, 0, 0.38f), 0.14f, 0.55f, 4, Shade(Ui.Col(0x2A2A2A)), Mathf.Pi / 4);
                        decor.Box(c + new Vector3(0.25f, 0.04f, 0.38f), new Vector3(0.34f, 0.08f, 0.34f), Shade(Ui.Col(0x55504A)));
                        break;
                    case Improvement.Cottage:
                        decor.Box(c + new Vector3(0.25f, 0.11f, 0.4f), new Vector3(0.3f, 0.22f, 0.26f), Shade(Ui.Col(0xE8D8B8)));
                        decor.Pyramid(c + new Vector3(0.25f, 0.22f, 0.4f), 0.27f, 0.2f, 4, Shade(Ui.Col(0xA04030)), Mathf.Pi / 4);
                        break;
                }
                if (t.Resource != Resource.None)
                {
                    decor.Tint = Shade;
                    Models.Resource(decor, t.Resource, c + new Vector3(-0.34f, 0.005f, -0.32f), ResourceColor(t.Resource));
                    decor.Tint = null;
                }
            }
        ch.Terrain.Mesh = terrain.Commit();
        ch.Decor.Mesh = decor.Commit();
    }

    // ------------------------------------------------------ units, cities
    private UnitNode CreateUnit(UnitDto u)
    {
        var n = new UnitNode { Tile = u.Tile, Pos = Center(u.Tile), Root = new Node3D() };
        n.Body = new MeshInstance3D();
        n.Head = new MeshInstance3D { Mesh = _headMesh, Position = new Vector3(0, 0.74f, 0) };
        n.Ring = new MeshInstance3D { Mesh = _ringMesh, MaterialOverride = _ringMat, Position = new Vector3(0, 0.06f, 0), Visible = false };
        n.Label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FontSize = 40, OutlineSize = 12,
            PixelSize = 0.0075f, Position = new Vector3(0, 1.2f, 0), RenderPriority = 2, OutlineRenderPriority = 1,
        };
        n.Root.AddChild(n.Body); n.Root.AddChild(n.Head); n.Root.AddChild(n.Ring); n.Root.AddChild(n.Label);
        n.Root.Position = n.Pos;
        AddChild(n.Root);
        return n;
    }

    private void SyncUnits()
    {
        // stacks at rest show only their top unit; moving units are always shown
        var top = new Dictionary<int, (UnitDto unit, int count)>();
        foreach (var u in W.Units.Values)
        {
            if (!_units.TryGetValue(u.Id, out var v) || v.Path.Count > 0) continue;
            if (!top.TryGetValue(u.Tile, out var cur)) { top[u.Tile] = (u, 1); continue; }
            bool better = u.Id == SelectedUnit || (cur.unit.Id != SelectedUnit && (u.Def.Str > cur.unit.Def.Str || (u.Def.Str == cur.unit.Def.Str && u.Id < cur.unit.Id)));
            top[u.Tile] = (better ? u : cur.unit, cur.count + 1);
        }
        foreach (var u in W.Units.Values)
        {
            if (!_units.TryGetValue(u.Id, out var v)) continue;
            bool moving = v.Path.Count > 0;
            int count = 1;
            bool shown = moving || (top.TryGetValue(u.Tile, out var t) && t.unit.Id == u.Id && (count = t.count) > 0);
            v.Root.Visible = shown;
            if (!shown) continue;
            if (!moving) { v.Pos = Center(u.Tile); v.Root.Position = v.Pos; }

            if (v.Type != u.Type || v.Owner != u.Owner)
            {
                v.Type = u.Type; v.Owner = u.Owner;
                v.Body.Mesh = Models.Unit(u.Def, u.Owner, OwnerColor(u.Owner));
                v.Body.MaterialOverride = _terrainMat;
                v.Body.Position = new Vector3(0, u.Def.X.Flying ? 0.35f : 0, 0);
                v.Body.Scale = Vector3.One * 1.15f;
                v.Head.Visible = false;
            }
            bool mine = u.Owner == W.MyId, selected = u.Id == SelectedUnit;
            v.Ring.Visible = selected;
            v.Label.Visible = (Lod >= 1 && W.Tiles[u.Tile].CityId < 0) || selected;
            string text = u.Def.Name.Length > 3 && Lod < 2 ? u.Def.Name[..2] : u.Def.Name;
            if (count > 1) text += $" ×{count}";
            if (u.Hp < 100) text += $"  {u.Hp}%";
            if (u.Fortified) text += " ▣";
            if (u.Work != 0) text += " ⚒";
            if (u.Expires > 0) text += $" ⏳{u.Expires}";
            if (u.Shield > 0) text += " ⛨";
            if (mine && u.Def.X.Stealth) text += " ◌";
            if (!u.Def.Sea && W.Tiles[u.Tile].IsWater) text += " ⛵";
            v.Label.Text = text;
            bool ready = mine && u.Moves > 0 && !u.Fortified && u.Work == 0 && u.Goto < 0;
            v.Label.Modulate = selected ? Ui.Gold : u.Hp < 50 ? Ui.Bad : ready || !mine ? Colors.White : new Color(0.75f, 0.75f, 0.78f);
        }
    }

    private void SyncCities()
    {
        foreach (var c in W.Cities.Values)
        {
            if (W.Vis[c.Tile] == 0) continue;
            if (!_cities.TryGetValue(c.Id, out var n))
            {
                n = new CityNode { Root = new Node3D { Position = Center(c.Tile) } };
                AddChild(n.Root);
                _cities[c.Id] = n;
            }
            if (n.Owner != c.Owner || n.Pop != c.Pop)
            {
                n.Owner = c.Owner; n.Pop = c.Pop;
                foreach (var h in n.Houses) h.QueueFree();
                n.Houses.Clear();
                // a ring of houses that grows with the population, so the unit standing in the centre stays visible
                int houses = Math.Clamp(3 + c.Pop, 4, 12);
                var rnd = new Random(c.Id * 7919);
                for (int i = 0; i < houses; i++)
                {
                    float ang = Mathf.Tau * i / houses + 0.3f;
                    float hgt = 0.28f + (float)rnd.NextDouble() * 0.3f + Math.Min(0.5f, c.Pop * 0.035f);
                    float wid = 0.2f + (float)rnd.NextDouble() * 0.1f;
                    var house = new MeshInstance3D
                    {
                        Mesh = _houseMesh, Scale = new Vector3(wid, hgt, wid),
                        Position = new Vector3(Mathf.Cos(ang) * 0.6f, hgt / 2, Mathf.Sin(ang) * 0.6f),
                        MaterialOverride = i % 3 == 0 ? OwnerMat(c.Owner) : _wallMat,
                    };
                    n.Root.AddChild(house);
                    n.Houses.Add(house);
                }
                var banner = new MeshInstance3D
                {
                    Mesh = _houseMesh, Scale = new Vector3(0.06f, 1.5f, 0.06f), Position = new Vector3(0.62f, 0.75f, -0.5f),
                    MaterialOverride = OwnerMat(c.Owner),
                };
                var flag = new MeshInstance3D
                {
                    Mesh = _houseMesh, Scale = new Vector3(0.42f, 0.26f, 0.04f), Position = new Vector3(0.84f, 1.36f, -0.5f),
                    MaterialOverride = OwnerMat(c.Owner),
                };
                n.Root.AddChild(banner); n.Root.AddChild(flag);
                n.Houses.Add(banner); n.Houses.Add(flag);
            }
        }
    }

    // --------------------------------------------- selection, route, FX
    private void HexOutline(int tile, float scale, Color color, float lift)
    {
        var c = Center(tile) + new Vector3(0, lift, 0);
        for (int k = 0; k < 6; k++)
        {
            _lines.SurfaceSetColor(color); _lines.SurfaceAddVertex(c + Corner[k] * scale);
            _lines.SurfaceSetColor(color); _lines.SurfaceAddVertex(c + Corner[(k + 1) % 6] * scale);
        }
    }

    private void DrawLines()
    {
        _lines.ClearSurfaces();
        bool any = false;
        void Begin() { if (!any) { _lines.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; } }

        foreach (int t in Highlight)
            if (W.Grid.Valid(t)) { Begin(); HexOutline(t, 0.72f, new Color(1, 1, 1, 0.9f), 0.08f); HexOutline(t, 0.68f, new Color(1, 1, 1, 0.9f), 0.08f); }
        if (HoverTile >= 0 && W.Vis[HoverTile] > 0) { Begin(); HexOutline(HoverTile, 0.98f, new Color(1, 1, 1, 0.55f), 0.06f); }

        if (SelectedUnit >= 0 && W.Units.TryGetValue(SelectedUnit, out var sel))
        {
            if (HoverPath is { Count: > 0 })
            {
                Begin();
                var prev = Center(sel.Tile) + new Vector3(0, 0.15f, 0);
                var col = new Color(1, 1, 1, 0.95f);
                foreach (int step in HoverPath)
                {
                    var next = Center(step) + new Vector3(0, 0.15f, 0);
                    // three parallel lines read as one thick route
                    foreach (var off in new[] { Vector3.Zero, new Vector3(0.03f, 0, 0.03f), new Vector3(-0.03f, 0, -0.03f) })
                    {
                        _lines.SurfaceSetColor(col); _lines.SurfaceAddVertex(prev + off);
                        _lines.SurfaceSetColor(col); _lines.SurfaceAddVertex(next + off);
                    }
                    prev = next;
                }
                HexOutline(HoverPath[^1], 0.45f, col, 0.15f);
            }
            if (sel.Goto >= 0 && W.Grid.Valid(sel.Goto)) { Begin(); HexOutline(sel.Goto, 0.5f, Ui.Gold, 0.12f); HexOutline(sel.Goto, 0.4f, Ui.Gold, 0.12f); }
        }

        double now = Now;
        foreach (var (tile, until, color) in _flashes)
        {
            float k = (float)Math.Clamp((until - now) / 0.8, 0, 1);
            Begin();
            for (int ring = 0; ring < 4; ring++)
                HexOutline(tile, 1.25f - 0.8f * k + ring * 0.05f, new Color(color, k), 0.3f + (1 - k) * 0.6f);
        }
        if (any) _lines.SurfaceEnd();
    }
}
