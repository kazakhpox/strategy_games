using System;
using Godot;

namespace IH.Client;

/// <summary>Small helpers for building the interface in code.</summary>
public static class Ui
{
    public static readonly Color PanelBg = new(0.09f, 0.11f, 0.16f, 0.94f);
    public static readonly Color PanelEdge = new(0.55f, 0.47f, 0.27f);
    public static readonly Color Gold = new(0.96f, 0.82f, 0.36f);
    public static readonly Color Science = new(0.45f, 0.75f, 1f);
    public static readonly Color Good = new(0.5f, 0.9f, 0.5f);
    public static readonly Color Bad = new(1f, 0.45f, 0.4f);
    public static readonly Color Dim = new(0.7f, 0.72f, 0.78f);

    public static Color Col(int rgb, float a = 1) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);

    public static Label Lbl(string text, int size = 14, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color != null) l.AddThemeColorOverride("font_color", color.Value);
        return l;
    }

    public static Button Btn(string text, Action onPress, string? tooltip = null, int size = 14)
    {
        var b = new Button { Text = text, TooltipText = tooltip ?? "", FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", size);
        b.Pressed += onPress;
        return b;
    }

    public static PanelContainer Panel(int margin = 8, Color? bg = null)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        var sb = new StyleBoxFlat { BgColor = bg ?? PanelBg, BorderColor = PanelEdge };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(4);
        sb.SetContentMarginAll(margin);
        p.AddThemeStyleboxOverride("panel", sb);
        return p;
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

    public static Control Spacer(bool horizontal = true)
    {
        var c = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        if (horizontal) c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; else c.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return c;
    }

    public static void Clear(Node n)
    {
        foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); }
    }

    public static string Signed(int v) => v >= 0 ? $"+{v}" : v.ToString();
}
