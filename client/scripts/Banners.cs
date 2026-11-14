using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>
/// City name plates drawn in screen space above the 3D map: population in the owner's colour, name, faith,
/// garrison, and for the player's own cities what is being built and how far growth and production have got.
/// </summary>
public partial class CityBanners : Control
{
    private readonly GameScreen _screen;
    private readonly List<(Rect2 rect, int city)> _hit = new();
    private string _signature = "";

    public CityBanners(GameScreen screen)
    {
        _screen = screen;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Id of the city whose plate is under the point, or -1.</summary>
    public int CityAt(Vector2 at)
    {
        for (int i = _hit.Count - 1; i >= 0; i--)
            if (_hit[i].rect.HasPoint(at)) return _hit[i].city;
        return -1;
    }

    public override void _Process(double delta)
    {
        // redraw only when the camera or the world has changed, so an idle game costs nothing
        var map = _screen.Map;
        string sig = $"{map.Cam.X:0.00}|{map.Cam.Y:0.00}|{map.Zoom:0.000}|{map.Yaw:0.000}|{_screen.W.Revision}|{Size.X}|{Size.Y}";
        if (sig == _signature) return;
        _signature = sig;
        QueueRedraw();
    }

    public override void _Draw()
    {
        _hit.Clear();
        var w = _screen.W;
        var map = _screen.Map;
        int lod = map.Lod;
        int fs = lod >= 2 ? 14 : lod == 1 ? 12 : 10;
        var font = Ui.Bold;
        float h = fs + 10;
        foreach (var c in w.Cities.Values.OrderBy(c => c.Owner == w.MyId).ThenBy(c => c.Id))
        {
            if (w.Vis[c.Tile] == 0 || !map.Project(c.Tile, lod >= 2 ? 1.75f : 1.2f, out var anchor)) continue;
            if (anchor.X < -200 || anchor.Y < -80 || anchor.X > Size.X + 200 || anchor.Y > Size.Y + 80) continue;
            var color = map.OwnerColor(c.Owner);
            bool mine = c.Owner == w.MyId;
            string pop = c.Pop.ToString();
            int garrison = w.UnitsAt(c.Tile).Count(u => u.Def.Str > 0);
            bool faith = c.Religion != 0 && lod >= 1;
            float nameW = font.GetStringSize(c.Name, HorizontalAlignment.Left, -1, fs).X;
            float popW = Math.Max(h, font.GetStringSize(pop, HorizontalAlignment.Left, -1, fs).X + 12);
            float star = c.Capital ? fs + 2 : 0, rel = faith ? fs + 1 : 0;
            string guard = garrison > 0 && lod >= 1 ? garrison.ToString() : "";
            float guardW = guard.Length > 0 ? fs + 3 + font.GetStringSize(guard, HorizontalAlignment.Left, -1, fs - 2).X : 0;
            float width = popW + 7 + star + nameW + (rel > 0 ? rel + 4 : 0) + (guardW > 0 ? guardW + 5 : 0) + 10;
            var rect = new Rect2(anchor.X - width / 2, anchor.Y - h, width, h);
            if (w.Vis[c.Tile] == 1) color = color.Darkened(0.3f);

            // the plate: dark pill with the owner's colour as a border and as the population cap
            var bg = Ui.Box(c.Disorder ? Ui.Col(0x3A141A, 0.95f) : Ui.Col(0x0B101C, 0.9f), c.Disorder ? Ui.Bad : color, (int)(h / 2), 0, 0, mine ? 2 : 1);
            DrawStyleBox(bg, rect);
            var cap = Ui.Box(color, null, (int)(h / 2), 0, 0);
            cap.CornerRadiusTopRight = cap.CornerRadiusBottomRight = 3;
            DrawStyleBox(cap, new Rect2(rect.Position, new Vector2(popW, h)));
            float baseline = rect.Position.Y + (h - font.GetHeight(fs)) / 2 + font.GetAscent(fs);
            var popSize = font.GetStringSize(pop, HorizontalAlignment.Left, -1, fs);
            DrawString(font, new Vector2(rect.Position.X + (popW - popSize.X) / 2, baseline), pop, HorizontalAlignment.Left, -1, fs,
                color.Luminance > 0.55f ? new Color(0.06f, 0.08f, 0.12f) : Colors.White);
            float x = rect.Position.X + popW + 7;
            if (c.Capital) { Icon.Paint(this, Ico.Star, new Rect2(x, rect.Position.Y + (h - fs) / 2, fs, fs), Ui.Gold); x += star; }
            DrawString(font, new Vector2(x, baseline), c.Name, HorizontalAlignment.Left, -1, fs, c.Disorder ? Ui.Col(0xFFB3AC) : Ui.Text);
            x += nameW;
            if (faith) { x += 4; Icon.Paint(this, Ico.Faith, new Rect2(x, rect.Position.Y + (h - fs + 2) / 2, fs - 2, fs - 2), Ui.Faith); x += rel; }
            if (guardW > 0)
            {
                x += 5;
                Icon.Paint(this, Ico.Shield, new Rect2(x, rect.Position.Y + (h - fs + 2) / 2, fs - 2, fs - 2), Ui.Dim);
                DrawString(font, new Vector2(x + fs + 1, baseline), guard, HorizontalAlignment.Left, -1, fs - 2, Ui.Dim);
            }
            _hit.Add((rect, c.Id));

            // own cities: growth and production under the plate
            if (!mine || c.Detail is not { } d || lod == 0) continue;
            float bx = rect.Position.X + 8, bw = width - 16, by = rect.End.Y + 2;
            DrawRect(new Rect2(bx, by, bw, 3), Ui.Col(0x0B101C, 0.85f));
            DrawRect(new Rect2(bx, by, bw * Mathf.Clamp(d.Food / (float)Math.Max(1, d.FoodNeed), 0, 1), 3), d.FoodRate < 0 ? Ui.Bad : Ui.Food);
            by += 4;
            string item = d.Queue.Length > 0 ? d.Queue[0] : "";
            int cost = item.Length > 0 && item != Rules.Wealth ? Rules.ItemCost(item) : 0;
            DrawRect(new Rect2(bx, by, bw, 3), Ui.Col(0x0B101C, 0.85f));
            if (cost > 0) DrawRect(new Rect2(bx, by, bw * Mathf.Clamp(d.Prod / (float)cost, 0, 1), 3), Ui.Prod);
            if (lod < 2) continue;
            string what = d.DisorderTurns > 0 ? $"беспорядки: {d.DisorderTurns} х." : item.Length == 0 ? "простаивает"
                : item == Rules.Wealth ? "богатство" : $"{Rules.ItemName(item)} · {(d.ProdRate > 0 ? Math.Max(1, (cost - d.Prod + d.ProdRate - 1) / d.ProdRate) + " х." : "∞")}";
            var plain = ThemeDB.FallbackFont;
            float ww = plain.GetStringSize(what, HorizontalAlignment.Left, -1, 11).X;
            var tag = new Rect2(anchor.X - ww / 2 - 6, by + 5, ww + 12, 16);
            DrawStyleBox(Ui.Box(Ui.Col(0x0B101C, 0.78f), null, 8, 0, 0), tag);
            DrawString(plain, new Vector2(tag.Position.X + 6, tag.Position.Y + 12), what, HorizontalAlignment.Left, -1, 11,
                d.DisorderTurns > 0 || item.Length == 0 ? Ui.Col(0xFF9C92) : Ui.Col(0xC8D0E0));
        }
    }
}
