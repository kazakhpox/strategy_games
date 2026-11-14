using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using IH.Shared;

namespace IH.Client;

/// <summary>
/// The technology tree drawn as a left-to-right graph: a tech sits one column to the right of its deepest
/// prerequisite and is joined to every prerequisite by a curve. Click an available tech to research it.
/// </summary>
public partial class TechGraph : Control
{
    private sealed class Node
    {
        public TechDef Tech = null!;
        public int Col, Row;
        public Rect2 Rect;
        public bool Known, Current;
        public TechCostDto? Avail;
    }

    private const float NodeW = 178, NodeH = 44, GapX = 64, GapY = 14, Pad = 14, HeaderH = 22;
    private readonly List<Node> _nodes = new();
    private readonly Dictionary<string, Node> _byId = new();
    private readonly Action<string> _research;
    private readonly int _sciRate, _progress;
    private Node? _hover;

    /// <summary>X position of the first column that still has something to research.</summary>
    public float FrontierX { get; private set; }

    private readonly string _unit;
    private readonly Func<string, TechDef?> _lookup;
    private readonly Func<TechDef, string> _unlocks;

    /// <summary>The science tree.</summary>
    public static TechGraph ForScience(MeDto me, Action<string> research) => new(
        Rules.Techs, Enumerable.Range(0, 4).Select(b => Rules.Tech(Rules.FutureId(b, me.Future[b] + 1))!).ToList(),
        new HashSet<string>(me.Known), me.Available.ToDictionary(a => a.Id), me.Research, me.SciRate, me.ResearchProgress,
        "науки", Rules.Tech, GameScreen.Unlocks, research);

    /// <summary>The social institutions tree, advanced by culture.</summary>
    public static TechGraph ForCivics(MeDto me, Action<string> adopt)
    {
        static TechDef Conv(Rules.CivicDef c) => new(c.Id, c.Name, c.Cost, c.Era, c.Prereqs);
        var avail = Rules.AvailableCivics(me.Civics, me.CivicLevel)
            .ToDictionary(c => c.Id, c => new TechCostDto { Id = c.Id, Cost = c.Cost, BaseCost = c.Cost });
        return new TechGraph(Rules.Civics.Select(Conv).ToList(), new List<TechDef> { Conv(Rules.Civic($"cf_{me.CivicLevel + 1}")!) },
            new HashSet<string>(me.Civics), avail, me.Civic, me.CultureRate, me.CivicProgress, "культуры",
            id => Rules.Civic(id) is { } c ? Conv(c) : null, t => Rules.Civic(t.Id)?.Effect.Describe() ?? "", adopt);
    }

    private TechGraph(IReadOnlyList<TechDef> tree, IReadOnlyList<TechDef> endless, HashSet<string> known, Dictionary<string, TechCostDto> avail,
        string current, int rate, int progress, string unit, Func<string, TechDef?> lookup, Func<TechDef, string> unlocks, Action<string> research)
    {
        _research = research; _unit = unit; _lookup = lookup; _unlocks = unlocks;
        _sciRate = rate; _progress = progress;

        var depth = new Dictionary<string, int>();
        int Depth(TechDef t) => depth.TryGetValue(t.Id, out int d) ? d
            : depth[t.Id] = t.Prereqs.Length == 0 ? 0 : 1 + t.Prereqs.Max(p => Depth(lookup(p)!));
        foreach (var t in tree)
            Add(new Node { Tech = t, Col = Depth(t), Known = known.Contains(t.Id) });
        int lastCol = _nodes.Max(n => n.Col);
        // the endless future branches: only the next level of each is shown, to the right of everything else
        foreach (var t in endless) Add(new Node { Tech = t, Col = lastCol + 1 });
        foreach (var n in _nodes) { n.Avail = avail.GetValueOrDefault(n.Tech.Id); n.Current = current == n.Tech.Id; }

        // order every column by where its prerequisites sit, so the curves cross as little as possible
        float maxRows = 0;
        for (int col = 0; col <= lastCol + 1; col++)
        {
            var column = _nodes.Where(n => n.Col == col)
                .OrderBy(n => n.Tech.Prereqs.Select(p => _byId.TryGetValue(p, out var q) ? (double)q.Row : 0).DefaultIfEmpty(col == 0 ? 0 : 99).Average())
                .ThenBy(n => n.Tech.Cost).ThenBy(n => n.Tech.Id, StringComparer.Ordinal).ToList();
            for (int i = 0; i < column.Count; i++) column[i].Row = i;
            maxRows = Math.Max(maxRows, column.Count);
        }
        foreach (var n in _nodes)
        {
            int count = _nodes.Count(o => o.Col == n.Col);
            float offset = (maxRows - count) * (NodeH + GapY) / 2; // centre short columns vertically
            n.Rect = new Rect2(Pad + n.Col * (NodeW + GapX), Pad + HeaderH + offset + n.Row * (NodeH + GapY), NodeW, NodeH);
        }
        CustomMinimumSize = new Vector2(Pad * 2 + (lastCol + 2) * (NodeW + GapX) - GapX, Pad * 2 + HeaderH + maxRows * (NodeH + GapY));
        int frontier = _nodes.Where(n => !n.Known).Min(n => n.Col);
        FrontierX = Math.Max(0, Pad + (frontier - 1) * (NodeW + GapX));
        MouseFilter = MouseFilterEnum.Stop;
    }

    private void Add(Node n) { _nodes.Add(n); _byId[n.Tech.Id] = n; }

    private Node? NodeAt(Vector2 p) => _nodes.FirstOrDefault(n => n.Rect.HasPoint(p));

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            var n = NodeAt(mm.Position);
            if (n != _hover) { _hover = n; QueueRedraw(); }
            MouseDefaultCursorShape = n is { Avail: not null } ? CursorShape.PointingHand : CursorShape.Arrow;
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            var n = NodeAt(mb.Position);
            if (n is { Avail: not null }) { _research(n.Tech.Id); AcceptEvent(); }
        }
    }

    public override string _GetTooltip(Vector2 at)
    {
        var n = NodeAt(at);
        if (n == null) return "";
        string pre = n.Tech.Prereqs.Length == 0 ? "" : "\nТребует: " + string.Join(", ", n.Tech.Prereqs.Select(p => _lookup(p)?.Name ?? p));
        string state = n.Known ? "Изучено" : n.Current ? "Изучается сейчас" : n.Avail != null ? "Нажмите, чтобы изучать" : "Пока недоступно";
        string discount = n.Avail != null && n.Avail.Cost < n.Avail.BaseCost ? $"\nЭффект низкой базы: {n.Avail.Cost} вместо {n.Avail.BaseCost}" : "";
        return $"{n.Tech.Name} — {Rules.EraName(n.Tech.Era)}\n{state}\nДаёт: {_unlocks(n.Tech)}{pre}{discount}";
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        var cGood = new Color(0.35f, 0.75f, 0.42f);
        var cLocked = new Color(0.33f, 0.36f, 0.43f);

        // column headers: how far along the tree each column is
        foreach (var group in _nodes.GroupBy(n => n.Col))
        {
            int era = group.Min(n => n.Tech.Era);
            DrawString(font, new Vector2(Pad + group.Key * (NodeW + GapX), Pad + 12), Rules.EraName(era), HorizontalAlignment.Left, NodeW, 12, Ui.Dim);
        }

        // edges first, so the nodes sit on top of them
        foreach (var n in _nodes)
            foreach (string p in n.Tech.Prereqs)
            {
                if (!_byId.TryGetValue(p, out var from)) continue;
                var a = new Vector2(from.Rect.End.X, from.Rect.GetCenter().Y);
                var b = new Vector2(n.Rect.Position.X, n.Rect.GetCenter().Y);
                bool lit = from.Known;
                bool hot = _hover != null && (_hover == n || _hover == from);
                var col = hot ? Ui.Gold : lit ? (n.Known ? cGood : new Color(0.55f, 0.8f, 0.6f)) : cLocked;
                var pts = new Vector2[17];
                float bend = Math.Max(30, (b.X - a.X) * 0.5f);
                for (int i = 0; i < pts.Length; i++)
                {
                    float t = i / (float)(pts.Length - 1);
                    pts[i] = a.BezierInterpolate(a + new Vector2(bend, 0), b - new Vector2(bend, 0), b, t);
                }
                DrawPolyline(pts, col, hot ? 3f : lit ? 2.2f : 1.4f, true);
            }

        foreach (var n in _nodes)
        {
            Color bg, edge, text;
            if (n.Known) { bg = new Color(0.13f, 0.3f, 0.18f); edge = cGood; text = new Color(0.8f, 1f, 0.82f); }
            else if (n.Current) { bg = new Color(0.12f, 0.24f, 0.42f); edge = Ui.Science; text = Colors.White; }
            else if (n.Avail != null) { bg = new Color(0.2f, 0.22f, 0.3f); edge = new Color(0.85f, 0.85f, 0.9f); text = Colors.White; }
            else { bg = new Color(0.1f, 0.11f, 0.15f); edge = cLocked; text = new Color(0.55f, 0.57f, 0.63f); }
            if (n == _hover && n.Avail != null) bg = bg.Lightened(0.12f);
            DrawRect(n.Rect, bg);
            DrawRect(n.Rect, n == _hover ? Ui.Gold : edge, false, n.Current ? 2.5f : 1.5f);

            string name = (n.Known ? "✔ " : n.Current ? "▶ " : "") + n.Tech.Name;
            DrawString(font, n.Rect.Position + new Vector2(8, 17), name, HorizontalAlignment.Left, NodeW - 14, 13, text);
            string sub;
            if (n.Known) sub = "изучено";
            else
            {
                int cost = n.Avail?.Cost ?? n.Tech.Cost;
                sub = $"{cost} {_unit}";
                if (n.Avail != null && _sciRate > 0)
                    sub += $" · {Math.Max(1, (cost - (n.Current ? _progress : Math.Min(_progress, cost)) + _sciRate - 1) / _sciRate)} х.";
                if (n.Avail != null && n.Avail.Cost < n.Avail.BaseCost) sub += " ▼";
            }
            DrawString(font, n.Rect.Position + new Vector2(8, 35), sub, HorizontalAlignment.Left, NodeW - 14, 11, n.Known ? cGood : Ui.Dim);
            if (n.Current && n.Avail != null)
            {
                float k = Math.Clamp(_progress / (float)Math.Max(1, n.Avail.Cost), 0, 1);
                DrawRect(new Rect2(n.Rect.Position.X, n.Rect.End.Y - 3, NodeW * k, 3), Ui.Science);
            }
        }
    }
}
