using System;
using Godot;

namespace IH.Client;

/// <summary>Palette, theme and small helpers for building the interface in code.</summary>
public static class Ui
{
    // ---- palette: deep navy surfaces, warm gold accent, one colour per yield
    public static readonly Color Bg = Col(0x0B0F1A);
    public static readonly Color PanelBg = Col(0x131A2A, 0.95f);
    public static readonly Color PanelHi = Col(0x1C2539);
    public static readonly Color Line = Col(0x2C3854);
    public static readonly Color PanelEdge = Col(0x8C7A45);
    public static readonly Color Text = Col(0xE8ECF4);
    public static readonly Color Dim = Col(0x9AA4B8);
    public static readonly Color Gold = Col(0xF2CF63);
    public static readonly Color Science = Col(0x6FC3FF);
    public static readonly Color Culture = Col(0xC79BFF);
    public static readonly Color Faith = Col(0xF6EFC8);
    public static readonly Color Favor = Col(0x62DCCB);
    public static readonly Color Food = Col(0x8FD96A);
    public static readonly Color Prod = Col(0xEFA35A);
    public static readonly Color Power = Col(0xB7A0FF);
    public static readonly Color Good = Col(0x74DB8C);
    public static readonly Color Bad = Col(0xFF6F63);

    public static Color Col(int rgb, float a = 1) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

    private static Font? _bold;
    /// <summary>The default font, thickened for headings and numbers.</summary>
    public static Font Bold => _bold ??= new FontVariation { BaseFont = ThemeDB.FallbackFont, VariationEmbolden = 0.55f };

    public static StyleBoxFlat Box(Color bg, Color? edge = null, int radius = 6, int padX = 10, int padY = 5, int border = 1)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = edge ?? Colors.Transparent };
        sb.SetBorderWidthAll(edge == null ? 0 : border);
        sb.SetCornerRadiusAll(radius);
        sb.ContentMarginLeft = sb.ContentMarginRight = padX;
        sb.ContentMarginTop = sb.ContentMarginBottom = padY;
        return sb;
    }

    /// <summary>One theme for the whole game: every stock control picks its look from here.</summary>
    public static Theme BuildTheme()
    {
        var t = new Theme { DefaultFontSize = 14 };
        var empty = new StyleBoxEmpty();

        void Button(string type, Color bg, Color hover, Color pressed, Color? edge, Color font, Color? edgeHover = null)
        {
            t.SetStylebox("normal", type, Box(bg, edge));
            t.SetStylebox("hover", type, Box(hover, edgeHover ?? edge));
            t.SetStylebox("pressed", type, Box(pressed, edgeHover ?? edge));
            t.SetStylebox("disabled", type, Box(new Color(bg, bg.A * 0.45f), edge == null ? null : new Color(edge.Value, 0.35f)));
            t.SetStylebox("focus", type, empty);
            t.SetColor("font_color", type, font);
            t.SetColor("font_hover_color", type, font.Lightened(0.25f));
            t.SetColor("font_pressed_color", type, font);
            t.SetColor("font_focus_color", type, font);
            t.SetColor("font_disabled_color", type, new Color(font, 0.38f));
        }
        Button("Button", Col(0x222C44), Col(0x2D3A59), Col(0x1A2236), Line, Text, Col(0x53658F));
        Button("OptionButton", Col(0x222C44), Col(0x2D3A59), Col(0x1A2236), Line, Text, Col(0x53658F));
        // the one action that moves the game forward
        t.SetTypeVariation("Primary", "Button");
        Button("Primary", Col(0xC9A545), Col(0xE2BC55), Col(0xA98A38), Col(0xF6DE8E), Col(0x1B1508));
        t.SetFont("font", "Primary", Bold);
        // rows in lists: quiet until hovered
        t.SetTypeVariation("Flat", "Button");
        Button("Flat", Col(0x1A2236, 0.6f), Col(0x2A3654), Col(0x182033), null, Text);
        // tabs of the advisor window
        t.SetTypeVariation("Tab", "Button");
        Button("Tab", Colors.Transparent, Col(0x222C44), Col(0x1A2236), null, Dim);
        t.SetTypeVariation("TabOn", "Button");
        Button("TabOn", Col(0x2A3552), Col(0x2A3552), Col(0x2A3552), PanelEdge, Gold);
        t.SetTypeVariation("Danger", "Button");
        Button("Danger", Col(0x4A2026), Col(0x64282F), Col(0x3A181D), Col(0x8A3B40), Col(0xFFC9C4));

        t.SetColor("font_color", "Label", Text);
        t.SetStylebox("panel", "PanelContainer", Box(PanelBg, Line, 8, 10, 8));

        t.SetStylebox("normal", "LineEdit", Box(Col(0x0E1422), Line, 6, 10, 6));
        t.SetStylebox("focus", "LineEdit", Box(Col(0x0E1422), PanelEdge, 6, 10, 6));
        t.SetColor("font_color", "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", new Color(Dim, 0.6f));
        t.SetColor("caret_color", "LineEdit", Gold);

        t.SetStylebox("panel", "PopupMenu", Box(Col(0x172033), PanelEdge, 6, 6, 6));
        t.SetStylebox("hover", "PopupMenu", Box(Col(0x2D3A59), null, 4, 6, 2));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Gold);

        t.SetStylebox("panel", "TooltipPanel", Box(Col(0x0C111D, 0.98f), PanelEdge, 6, 10, 7));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetFontSize("font_size", "TooltipLabel", 13);

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Line, Thickness = 1 });
        t.SetConstant("separation", "HSeparator", 6);
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Line, Thickness = 1, Vertical = true });

        foreach (string bar in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", bar, Box(Col(0x0E1422, 0.5f), null, 4, 3, 3));
            t.SetStylebox("grabber", bar, Box(Col(0x3A486B), null, 4, 3, 3));
            t.SetStylebox("grabber_highlight", bar, Box(Col(0x53658F), null, 4, 3, 3));
            t.SetStylebox("grabber_pressed", bar, Box(PanelEdge, null, 4, 3, 3));
        }
        t.SetColor("font_color", "CheckBox", Text);
        t.SetColor("font_hover_color", "CheckBox", Gold);
        t.SetColor("font_pressed_color", "CheckBox", Text);
        t.SetStylebox("focus", "CheckBox", empty);
        return t;
    }

    public static Label Lbl(string text, int size = 14, Color? color = null, bool bold = false)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color != null) l.AddThemeColorOverride("font_color", color.Value);
        if (bold) l.AddThemeFontOverride("font", Bold);
        return l;
    }

    /// <summary>A label that wraps inside the given width.</summary>
    public static Label Wrap(string text, float width, int size = 13, Color? color = null)
    {
        var l = Lbl(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(width, 0);
        return l;
    }

    public static Button Btn(string text, Action onPress, string? tooltip = null, int size = 14, string? variation = null)
    {
        var b = new Button { Text = text, TooltipText = tooltip ?? "", FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", size);
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += onPress;
        return b;
    }

    /// <summary>A list row: name on the left, a dim detail (price, time) on the right, optionally an icon in front.</summary>
    public static Button RowBtn(string left, string right, Action onPress, string? tooltip = null, Ico icon = Ico.None, Color? iconColor = null, int size = 13)
    {
        var b = Btn("", onPress, tooltip, size, "Flat");
        b.CustomMinimumSize = new Vector2(0, size + 15);
        var row = H(6);
        row.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, 0);
        row.OffsetLeft = 9; row.OffsetRight = -9;
        if (icon != Ico.None) row.AddChild(new Icon(icon, iconColor ?? Dim, size + 2));
        var name = Lbl(left, size);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.SizeFlagsVertical = Control.SizeFlags.Fill;
        row.AddChild(name);
        if (right.Length > 0)
        {
            var detail = Lbl(right, size - 1, Dim);
            detail.VerticalAlignment = VerticalAlignment.Center;
            detail.SizeFlagsVertical = Control.SizeFlags.Fill;
            row.AddChild(detail);
        }
        foreach (var k in row.GetChildren()) if (k is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        b.AddChild(row);
        return b;
    }

    public static PanelContainer Panel(int margin = 8, Color? bg = null, Color? edge = null, int radius = 8)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        p.AddThemeStyleboxOverride("panel", Box(bg ?? PanelBg, edge ?? Line, radius, margin, margin));
        return p;
    }

    /// <summary>A lighter block inside a panel that groups related facts.</summary>
    public static PanelContainer Card(Control content, int margin = 8, Color? bg = null, Color? edge = null)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        p.AddThemeStyleboxOverride("panel", Box(bg ?? Col(0x1A2236, 0.9f), edge, 6, margin, margin));
        p.AddChild(content);
        return p;
    }

    /// <summary>A panel that behaves like a button: lights up under the cursor and runs the action on click.</summary>
    public static PanelContainer Clickable(Control content, Action onPress, string tooltip = "", Color? edge = null, int margin = 7)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = tooltip, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        var normal = Box(Col(0x151C2D, 0.94f), edge ?? Line, 7, margin + 2, margin);
        var hover = Box(Col(0x222C46, 0.97f), edge ?? Col(0x53658F), 7, margin + 2, margin);
        p.AddThemeStyleboxOverride("panel", normal);
        p.MouseEntered += () => p.AddThemeStyleboxOverride("panel", hover);
        p.MouseExited += () => p.AddThemeStyleboxOverride("panel", normal);
        p.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { onPress(); p.AcceptEvent(); }
        };
        content.MouseFilter = Control.MouseFilterEnum.Ignore;
        IgnoreMouse(content);
        p.AddChild(content);
        return p;
    }

    public static void IgnoreMouse(Node n)
    {
        foreach (var k in n.GetChildren())
        {
            if (k is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
            IgnoreMouse(k);
        }
    }

    /// <summary>Small rounded tag with a tinted background.</summary>
    public static PanelContainer Pill(string text, Color color, int size = 12, string tooltip = "")
    {
        var p = new PanelContainer { MouseFilter = tooltip.Length > 0 ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore, TooltipText = tooltip };
        p.AddThemeStyleboxOverride("panel", Box(new Color(color, 0.16f), new Color(color, 0.55f), 9, 8, 2));
        var l = Lbl(text, size, color);
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
        p.AddChild(l);
        return p;
    }

    /// <summary>Section caption: small gold capitals with a hairline running to the right edge.</summary>
    public static Control Section(string text)
    {
        var l = Lbl(text.ToUpperInvariant(), 12, Col(0xC9B36A), true);
        var line = new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var h = H(10, l, line);
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_top", 8);
        m.AddChild(h);
        return m;
    }

    public static VBoxContainer V(int sep = 4, params Control[] kids)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        foreach (var k in kids) v.AddChild(k);
        return v;
    }

    public static HBoxContainer H(int sep = 6, params Control[] kids)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        foreach (var k in kids) h.AddChild(k);
        return h;
    }

    public static HFlowContainer Flow(int sep = 6, params Control[] kids)
    {
        var f = new HFlowContainer();
        f.AddThemeConstantOverride("h_separation", sep);
        f.AddThemeConstantOverride("v_separation", sep);
        foreach (var k in kids) f.AddChild(k);
        return f;
    }

    public static Control Spacer(bool horizontal = true)
    {
        var c = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (horizontal) c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; else c.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return c;
    }

    /// <summary>Places a control in a corner or along an edge of the screen and keeps it there as its size changes.</summary>
    public static MarginContainer Dock(Control child, Control.SizeFlags h, Control.SizeFlags v, int left = 8, int top = 8, int right = 8, int bottom = 8)
    {
        var m = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        m.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        m.AddThemeConstantOverride("margin_left", left);
        m.AddThemeConstantOverride("margin_top", top);
        m.AddThemeConstantOverride("margin_right", right);
        m.AddThemeConstantOverride("margin_bottom", bottom);
        child.SizeFlagsHorizontal = h;
        child.SizeFlagsVertical = v;
        m.AddChild(child);
        return m;
    }

    public static void Clear(Node n)
    {
        foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); }
    }

    public static string Signed(int v) => v >= 0 ? $"+{v}" : $"−{-v}";
}

public enum Ico
{
    None, Gold, Science, Culture, Faith, Favor, Food, Prod, Power, Happy, Unrest, Sword, Shield, Moves, Heart, Pop, Turn,
    Warn, Check, City, Chart, Help, Branch, Ship, Star, Close, Bow, Sleep, Scroll,
}

/// <summary>Icons drawn from simple shapes, so the game ships no image files and they stay crisp at any size.</summary>
public partial class Icon : Control
{
    private Ico _kind;
    private Color _color;

    public Icon(Ico kind, Color color, float size = 16)
    {
        _kind = kind; _color = color;
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Set(Ico kind, Color color) { _kind = kind; _color = color; QueueRedraw(); }

    public override void _Draw() => Paint(this, _kind, new Rect2(Vector2.Zero, Size), _color);

    /// <summary>Draws an icon into any rectangle of any canvas item (banners and badges reuse it).</summary>
    public static void Paint(CanvasItem ci, Ico kind, Rect2 r, Color col)
    {
        float s = Math.Min(r.Size.X, r.Size.Y);
        var o = r.Position + (r.Size - new Vector2(s, s)) / 2;
        Vector2 P(float x, float y) => o + new Vector2(x, y) * s;
        void Poly(params float[] xy)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = P(xy[i * 2], xy[i * 2 + 1]);
            ci.DrawColoredPolygon(pts, col);
        }
        void Rect(float x, float y, float w, float h, Color? c = null) => ci.DrawRect(new Rect2(P(x, y), new Vector2(w, h) * s), c ?? col);
        void Stroke(float w, params float[] xy)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = P(xy[i * 2], xy[i * 2 + 1]);
            ci.DrawPolyline(pts, col, Math.Max(1.2f, w * s), true);
        }
        var c0 = P(0.5f, 0.5f);
        var dark = new Color(0, 0, 0, 0.55f);
        switch (kind)
        {
            case Ico.Gold:
                ci.DrawCircle(c0, 0.46f * s, col.Darkened(0.35f));
                ci.DrawCircle(c0, 0.36f * s, col);
                ci.DrawArc(c0, 0.2f * s, 0, Mathf.Tau, 20, col.Darkened(0.3f), Math.Max(1, 0.08f * s), true);
                break;
            case Ico.Science:
                Rect(0.34f, 0.05f, 0.32f, 0.09f);
                Poly(0.41f, 0.1f, 0.59f, 0.1f, 0.59f, 0.4f, 0.9f, 0.86f, 0.82f, 0.95f, 0.18f, 0.95f, 0.1f, 0.86f, 0.41f, 0.4f);
                ci.DrawColoredPolygon(new[] { P(0.36f, 0.62f), P(0.64f, 0.62f), P(0.78f, 0.84f), P(0.22f, 0.84f) }, col.Lightened(0.55f));
                break;
            case Ico.Culture:
                Poly(0.5f, 0.06f, 0.96f, 0.36f, 0.04f, 0.36f);
                Rect(0.14f, 0.42f, 0.14f, 0.36f); Rect(0.43f, 0.42f, 0.14f, 0.36f); Rect(0.72f, 0.42f, 0.14f, 0.36f);
                Rect(0.06f, 0.82f, 0.88f, 0.12f);
                break;
            case Ico.Faith:
            {
                var pts = new Vector2[8];
                for (int i = 0; i < 8; i++)
                {
                    float a = Mathf.Tau * i / 8 - Mathf.Pi / 2, rad = i % 2 == 0 ? 0.5f : 0.17f;
                    pts[i] = c0 + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad * s;
                }
                ci.DrawColoredPolygon(pts, col);
                break;
            }
            case Ico.Favor:
            {
                float w = Math.Max(1.2f, 0.09f * s);
                ci.DrawArc(c0, 0.42f * s, 0, Mathf.Tau, 28, col, w, true);
                ci.DrawLine(P(0.08f, 0.5f), P(0.92f, 0.5f), col, w, true);
                var el = new Vector2[21];
                for (int i = 0; i <= 20; i++) { float a = Mathf.Tau * i / 20; el[i] = c0 + new Vector2(Mathf.Cos(a) * 0.19f, Mathf.Sin(a) * 0.42f) * s; }
                ci.DrawPolyline(el, col, w, true);
                break;
            }
            case Ico.Food:
                ci.DrawCircle(P(0.38f, 0.6f), 0.27f * s, col);
                ci.DrawCircle(P(0.62f, 0.6f), 0.27f * s, col);
                ci.DrawLine(P(0.5f, 0.4f), P(0.56f, 0.1f), col.Darkened(0.35f), Math.Max(1.2f, 0.09f * s), true);
                ci.DrawCircle(P(0.72f, 0.2f), 0.11f * s, col.Lightened(0.3f));
                break;
            case Ico.Prod:
            {
                var pts = new Vector2[32];
                for (int i = 0; i < 8; i++)
                {
                    float a = Mathf.Tau * i / 8;
                    float[] off = { -0.26f, -0.16f, 0.16f, 0.26f };
                    float[] rad = { 0.34f, 0.48f, 0.48f, 0.34f };
                    for (int k = 0; k < 4; k++) pts[i * 4 + k] = c0 + new Vector2(Mathf.Cos(a + off[k]), Mathf.Sin(a + off[k])) * rad[k] * s;
                }
                ci.DrawColoredPolygon(pts, col);
                ci.DrawCircle(c0, 0.15f * s, dark);
                break;
            }
            case Ico.Power:
                Poly(0.5f, 0.02f, 0.88f, 0.4f, 0.5f, 0.98f, 0.12f, 0.4f);
                ci.DrawColoredPolygon(new[] { P(0.5f, 0.02f), P(0.88f, 0.4f), P(0.5f, 0.4f) }, col.Lightened(0.45f));
                ci.DrawColoredPolygon(new[] { P(0.12f, 0.4f), P(0.5f, 0.4f), P(0.5f, 0.98f) }, col.Darkened(0.25f));
                break;
            case Ico.Happy:
                ci.DrawCircle(c0, 0.46f * s, col);
                ci.DrawCircle(P(0.35f, 0.4f), 0.07f * s, dark); ci.DrawCircle(P(0.65f, 0.4f), 0.07f * s, dark);
                ci.DrawArc(P(0.5f, 0.5f), 0.24f * s, 0.35f, Mathf.Pi - 0.35f, 12, dark, Math.Max(1.2f, 0.08f * s), true);
                break;
            case Ico.Unrest:
                Poly(0.52f, 0.02f, 0.74f, 0.34f, 0.86f, 0.64f, 0.72f, 0.9f, 0.5f, 0.98f, 0.28f, 0.9f, 0.14f, 0.64f, 0.26f, 0.4f, 0.38f, 0.54f, 0.4f, 0.28f);
                ci.DrawColoredPolygon(new[] { P(0.5f, 0.52f), P(0.64f, 0.74f), P(0.5f, 0.92f), P(0.36f, 0.74f) }, col.Lightened(0.5f));
                break;
            case Ico.Sword:
                Stroke(0.13f, 0.86f, 0.08f, 0.32f, 0.62f);
                Stroke(0.11f, 0.16f, 0.46f, 0.48f, 0.78f);
                Stroke(0.12f, 0.3f, 0.64f, 0.12f, 0.88f);
                break;
            case Ico.Shield:
                Poly(0.5f, 0.04f, 0.9f, 0.18f, 0.88f, 0.54f, 0.5f, 0.96f, 0.12f, 0.54f, 0.1f, 0.18f);
                ci.DrawColoredPolygon(new[] { P(0.5f, 0.16f), P(0.78f, 0.26f), P(0.76f, 0.5f), P(0.5f, 0.8f) }, col.Darkened(0.3f));
                break;
            case Ico.Moves:
                Stroke(0.14f, 0.12f, 0.18f, 0.42f, 0.5f, 0.12f, 0.82f);
                Stroke(0.14f, 0.52f, 0.18f, 0.82f, 0.5f, 0.52f, 0.82f);
                break;
            case Ico.Heart:
                ci.DrawCircle(P(0.3f, 0.34f), 0.23f * s, col); ci.DrawCircle(P(0.7f, 0.34f), 0.23f * s, col);
                Poly(0.08f, 0.44f, 0.92f, 0.44f, 0.5f, 0.94f);
                break;
            case Ico.Pop:
                ci.DrawCircle(P(0.5f, 0.26f), 0.2f * s, col);
                Poly(0.3f, 0.52f, 0.7f, 0.52f, 0.84f, 0.96f, 0.16f, 0.96f);
                break;
            case Ico.Turn:
                Rect(0.16f, 0.04f, 0.68f, 0.1f); Rect(0.16f, 0.86f, 0.68f, 0.1f);
                Poly(0.22f, 0.14f, 0.78f, 0.14f, 0.5f, 0.5f);
                Poly(0.5f, 0.5f, 0.78f, 0.86f, 0.22f, 0.86f);
                break;
            case Ico.Warn:
                Poly(0.5f, 0.06f, 0.96f, 0.9f, 0.04f, 0.9f);
                ci.DrawLine(P(0.5f, 0.36f), P(0.5f, 0.64f), dark.Darkened(0.5f), Math.Max(1.4f, 0.1f * s), true);
                ci.DrawCircle(P(0.5f, 0.77f), 0.06f * s, new Color(0, 0, 0, 0.8f));
                break;
            case Ico.Check:
                Stroke(0.15f, 0.12f, 0.54f, 0.4f, 0.8f, 0.88f, 0.2f);
                break;
            case Ico.City:
                Poly(0.5f, 0.06f, 0.96f, 0.48f, 0.04f, 0.48f);
                Rect(0.16f, 0.46f, 0.68f, 0.48f);
                Rect(0.42f, 0.64f, 0.16f, 0.3f, dark);
                break;
            case Ico.Chart:
                Rect(0.1f, 0.56f, 0.2f, 0.38f); Rect(0.4f, 0.3f, 0.2f, 0.64f); Rect(0.7f, 0.08f, 0.2f, 0.86f);
                break;
            case Ico.Help:
            {
                ci.DrawArc(c0, 0.43f * s, 0, Mathf.Tau, 28, col, Math.Max(1.2f, 0.09f * s), true);
                int fs = Math.Max(8, (int)(s * 0.72f));
                var size = Ui.Bold.GetStringSize("?", HorizontalAlignment.Left, -1, fs);
                ci.DrawString(Ui.Bold, c0 + new Vector2(-size.X / 2, fs * 0.36f), "?", HorizontalAlignment.Left, -1, fs, col);
                break;
            }
            case Ico.Branch:
                Stroke(0.08f, 0.22f, 0.5f, 0.76f, 0.22f);
                Stroke(0.08f, 0.22f, 0.5f, 0.76f, 0.78f);
                ci.DrawCircle(P(0.22f, 0.5f), 0.17f * s, col);
                ci.DrawCircle(P(0.76f, 0.22f), 0.15f * s, col); ci.DrawCircle(P(0.76f, 0.78f), 0.15f * s, col);
                break;
            case Ico.Ship:
                Poly(0.04f, 0.62f, 0.96f, 0.62f, 0.78f, 0.92f, 0.22f, 0.92f);
                Poly(0.46f, 0.06f, 0.46f, 0.54f, 0.86f, 0.54f);
                Rect(0.4f, 0.06f, 0.06f, 0.56f);
                break;
            case Ico.Star:
            {
                var pts = new Vector2[10];
                for (int i = 0; i < 10; i++)
                {
                    float a = Mathf.Tau * i / 10 - Mathf.Pi / 2, rad = i % 2 == 0 ? 0.5f : 0.21f;
                    pts[i] = c0 + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rad * s;
                }
                ci.DrawColoredPolygon(pts, col);
                break;
            }
            case Ico.Close:
                Stroke(0.13f, 0.18f, 0.18f, 0.82f, 0.82f);
                Stroke(0.13f, 0.82f, 0.18f, 0.18f, 0.82f);
                break;
            case Ico.Bow:
                ci.DrawArc(P(0.3f, 0.5f), 0.46f * s, -1.15f, 1.15f, 16, col, Math.Max(1.2f, 0.1f * s), true);
                Stroke(0.07f, 0.49f, 0.08f, 0.49f, 0.92f);
                Stroke(0.09f, 0.1f, 0.5f, 0.92f, 0.5f);
                Poly(0.96f, 0.5f, 0.76f, 0.38f, 0.76f, 0.62f);
                break;
            case Ico.Sleep:
                ci.DrawCircle(c0, 0.44f * s, col);
                ci.DrawCircle(P(0.66f, 0.38f), 0.36f * s, new Color(0.08f, 0.1f, 0.16f));
                break;
            case Ico.Scroll:
                Rect(0.16f, 0.1f, 0.68f, 0.8f);
                Rect(0.28f, 0.28f, 0.44f, 0.08f, dark); Rect(0.28f, 0.46f, 0.44f, 0.08f, dark); Rect(0.28f, 0.64f, 0.3f, 0.08f, dark);
                break;
        }
    }
}

/// <summary>A resource in the top bar or a yield in a list: icon, value and an optional per-turn rate.</summary>
public partial class Chip : HBoxContainer
{
    private readonly Label _value, _rate;
    private readonly Color _color;

    public Chip(Ico icon, Color color, string tooltip = "", int size = 15, bool colorValue = true)
    {
        _color = color;
        AddThemeConstantOverride("separation", 5);
        MouseFilter = tooltip.Length > 0 ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        TooltipText = tooltip;
        AddChild(new Icon(icon, color, size + 2));
        _value = Ui.Lbl("", size, colorValue ? color : Ui.Text, true);
        _rate = Ui.Lbl("", size - 2, Ui.Dim);
        _value.MouseFilter = _rate.MouseFilter = MouseFilterEnum.Ignore;
        _rate.Visible = false;
        AddChild(_value); AddChild(_rate);
    }

    public Chip Set(string value, string rate = "", Color? rateColor = null)
    {
        _value.Text = value;
        _rate.Text = rate;
        _rate.Visible = rate.Length > 0;
        _rate.AddThemeColorOverride("font_color", rateColor ?? Ui.Dim);
        return this;
    }
}

/// <summary>Thin progress bar; the lighter "ghost" part shows what the next turn will add.</summary>
public partial class Bar : Control
{
    private float _value, _ghost;
    private Color _fill;
    private readonly StyleBoxFlat _back = Ui.Box(Ui.Col(0x0A0E18, 0.9f), null, 3, 0, 0), _front = Ui.Box(Colors.White, null, 3, 0, 0), _next = Ui.Box(Colors.White, null, 3, 0, 0);

    public Bar(Color fill, float height = 6, float minWidth = 40)
    {
        _fill = fill;
        CustomMinimumSize = new Vector2(minWidth, height);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Colour of the unfilled part, for bars that weigh two sides against each other.</summary>
    public Bar WithBack(Color color) { _back.BgColor = color; return this; }

    public Bar Set(float value, float ghost = 0, Color? fill = null)
    {
        _value = Mathf.Clamp(value, 0, 1); _ghost = Mathf.Clamp(ghost, 0, 1);
        if (fill != null) _fill = fill.Value;
        QueueRedraw();
        return this;
    }

    public override void _Draw()
    {
        var full = new Rect2(Vector2.Zero, Size);
        DrawStyleBox(_back, full);
        float total = Math.Min(1, _value + _ghost);
        if (_ghost > 0 && total * Size.X >= 2)
        {
            _next.BgColor = new Color(_fill, 0.38f);
            DrawStyleBox(_next, new Rect2(Vector2.Zero, new Vector2(Size.X * total, Size.Y)));
        }
        if (_value * Size.X >= 2)
        {
            _front.BgColor = _fill;
            DrawStyleBox(_front, new Rect2(Vector2.Zero, new Vector2(Size.X * _value, Size.Y)));
        }
    }
}
