namespace IH.Shared;

/// <summary>Pointy-top hex grid in "odd-r" offset coordinates. Tile index = y * W + x.</summary>
public sealed class HexGrid
{
    public readonly int W, H;
    public readonly int[][] Nb;

    private static readonly (int dx, int dy)[] EvenRow = { (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1) };
    private static readonly (int dx, int dy)[] OddRow = { (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1) };

    public HexGrid(int w, int h)
    {
        W = w; H = h;
        Nb = new int[w * h][];
        var tmp = new List<int>(6);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                tmp.Clear();
                foreach (var (dx, dy) in (y & 1) == 0 ? EvenRow : OddRow)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h) tmp.Add(ny * w + nx);
                }
                Nb[y * w + x] = tmp.ToArray();
            }
    }

    public int Count => W * H;
    public int X(int idx) => idx % W;
    public int Y(int idx) => idx / W;
    public bool Valid(int idx) => idx >= 0 && idx < W * H;

    public int Distance(int a, int b)
    {
        int ax = a % W, ay = a / W, bx = b % W, by = b / W;
        int aq = ax - (ay - (ay & 1)) / 2, bq = bx - (by - (by & 1)) / 2;
        int dq = aq - bq, dr = ay - by;
        return (Math.Abs(dq) + Math.Abs(dq + dr) + Math.Abs(dr)) / 2;
    }

    /// <summary>All tiles within <paramref name="radius"/> of the center (center included).</summary>
    public List<int> Range(int center, int radius)
    {
        var res = new List<int>(1 + 3 * radius * (radius + 1));
        int cx = center % W, cy = center / W;
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(H - 1, cy + radius); y++)
            for (int x = Math.Max(0, cx - radius - 1); x <= Math.Min(W - 1, cx + radius + 1); x++)
            {
                int i = y * W + x;
                if (Distance(center, i) <= radius) res.Add(i);
            }
        return res;
    }

    /// <summary>A* path. cost(from,to) returns a negative value for impassable steps. Result excludes the start tile.</summary>
    public List<int>? FindPath(int from, int to, Func<int, int, int> cost, int maxNodes = 20000)
    {
        if (from == to) return new List<int>();
        var open = new PriorityQueue<int, int>();
        var g = new Dictionary<int, int> { [from] = 0 };
        var prev = new Dictionary<int, int>();
        open.Enqueue(from, 0);
        int expanded = 0;
        while (open.Count > 0)
        {
            open.TryDequeue(out int cur, out int pri);
            int gc = g[cur];
            if (pri > gc + Distance(cur, to)) continue; // stale entry
            if (cur == to)
            {
                var path = new List<int>();
                for (int t = to; t != from; t = prev[t]) path.Add(t);
                path.Reverse();
                return path;
            }
            if (++expanded > maxNodes) return null;
            foreach (int n in Nb[cur])
            {
                int c = cost(cur, n);
                if (c < 0) continue;
                int ng = gc + c;
                if (g.TryGetValue(n, out int old) && old <= ng) continue;
                g[n] = ng; prev[n] = cur;
                open.Enqueue(n, ng + Distance(n, to));
            }
        }
        return null;
    }
}
