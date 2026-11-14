using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;
using Resource = IH.Shared.Resource;
using TileData = IH.Shared.TileData;

namespace IH.Client;

/// <summary>Overview map rendered into a tiny texture (2 px per tile); click or drag to move the camera.</summary>
public partial class Minimap : Control
{
    private readonly GameScreen _screen;
    private readonly Image _img;
    private readonly ImageTexture _tex;
    private bool _dirty = true, _drag;

    public Minimap(GameScreen screen)
    {
        _screen = screen;
        var g = screen.W.Grid;
        _img = Image.CreateEmpty(g.W * 2 + 1, g.H, false, Image.Format.Rgb8);
        _tex = ImageTexture.CreateFromImage(_img);
        TextureFilter = TextureFilterEnum.Nearest;
        float w = 250f;
        CustomMinimumSize = new Vector2(w, Mathf.Clamp(w * g.H * 1.5f / (g.W * 1.732f), 110, 220));
        MouseFilter = MouseFilterEnum.Stop;
    }

    public void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty) return;
        _dirty = false;
        var w = _screen.W;
        var unknown = new Color(0.03f, 0.04f, 0.07f);
        for (int i = 0; i < w.Tiles.Length; i++)
        {
            int x = i % w.Grid.W, y = i / w.Grid.W;
            Color c = unknown;
            if (w.Vis[i] > 0)
            {
                var t = w.Tiles[i];
                c = MapView.TerrainColor(t);
                if (t.Owner >= 0) c = c.Lerp(_screen.Map.OwnerColor(t.Owner), 0.6f);
                if (t.CityId >= 0) c = Colors.White;
                if (w.Vis[i] == 1) c = c.Darkened(0.4f);
            }
            int px = x * 2 + (y & 1);
            _img.SetPixel(px, y, c);
            _img.SetPixel(px + 1, y, c);
        }
        foreach (var u in w.Units.Values)
            if (u.Owner == w.MyId) { int px = u.Tile % w.Grid.W * 2 + (u.Tile / w.Grid.W & 1); _img.SetPixel(px, u.Tile / w.Grid.W, Colors.Yellow); }
        _tex.Update(_img);
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawTextureRect(_tex, new Rect2(Vector2.Zero, Size), false);
        var map = _screen.Map;
        var total = map.MapPixelSize;
        var view = map.ViewSize;
        var topLeft = (map.Cam - view / 2) / total * Size;
        var rect = new Rect2(topLeft, view / total * Size).Intersection(new Rect2(Vector2.Zero, Size));
        if (rect.Size.X > 1 && rect.Size.Y > 1) DrawRect(rect, Colors.White, false, 1.5f);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { _drag = mb.Pressed; if (mb.Pressed) Jump(mb.Position); AcceptEvent(); }
        else if (e is InputEventMouseMotion mm && _drag) { Jump(mm.Position); AcceptEvent(); }
    }

    private void Jump(Vector2 local)
    {
        var map = _screen.Map;
        map.Cam = local / Size * map.MapPixelSize;
        map.ApplyCamera();
    }
}
