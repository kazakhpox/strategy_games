using IH.Shared;

namespace IH.Core;

/// <summary>Deterministic map generator: one dominant continent, climate bands, relief, forests, resources.</summary>
public static class MapGen
{
    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private static double Smooth(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        double a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed), c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }

    private static double Fbm(double x, double y, int seed)
    {
        double sum = 0, amp = 0.5, norm = 0;
        for (int o = 0; o < 4; o++) { sum += amp * Smooth(x, y, seed + o * 101); norm += amp; x *= 2; y *= 2; amp *= 0.5; }
        return sum / norm;
    }

    public static TileData[] Generate(int w, int h, int seed, HexGrid grid)
    {
        var tiles = new TileData[w * h];
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                ref var t = ref tiles[y * w + x];
                t.Owner = -1; t.CityId = -1;
                double nx = (x + 0.5 * (y & 1)) / w, ny = (double)y / h;
                double dx = (nx - 0.5) * 2, dy = (ny - 0.5) * 2;
                double edge = Math.Max(Math.Abs(dx), Math.Abs(dy));
                double elev = Fbm(nx * 5, ny * 5 * h / w, seed) + 0.35 - 0.55 * Math.Pow(edge, 2.2) - 0.25 * Math.Sqrt(dx * dx + dy * dy);
                if (elev < 0.33) { t.Terrain = Terrain.Ocean; continue; }

                double lat = Math.Abs(dy);
                double moist = Fbm(nx * 7 + 40, ny * 7 * h / w + 40, seed + 7);
                if (lat > 0.88) t.Terrain = Terrain.Snow;
                else if (lat > 0.72) t.Terrain = Terrain.Tundra;
                else if (moist < 0.38 && lat < 0.45) t.Terrain = Terrain.Desert;
                else t.Terrain = moist > 0.52 ? Terrain.Grassland : Terrain.Plains;

                double rough = Fbm(nx * 11 + 90, ny * 11 * h / w + 90, seed + 13);
                if (rough > 0.68) t.Relief = Relief.Mountain;
                else if (rough > 0.56) t.Relief = Relief.Hills;

                if (t.Relief != Relief.Mountain && t.Terrain is Terrain.Grassland or Terrain.Plains or Terrain.Tundra)
                    t.Forest = Fbm(nx * 9 + 200, ny * 9 * h / w + 200, seed + 21) > 0.55 && rnd.Next(100) < 75;
            }

        for (int i = 0; i < tiles.Length; i++)
            if (tiles[i].Terrain == Terrain.Ocean && grid.Nb[i].Any(n => tiles[n].Terrain > Terrain.Coast))
                tiles[i].Terrain = Terrain.Coast;

        for (int i = 0; i < tiles.Length; i++)
        {
            ref var t = ref tiles[i];
            if (t.Relief == Relief.Mountain) continue;
            int roll = rnd.Next(1000);
            if (t.Terrain == Terrain.Coast) { if (roll < 60) t.Resource = Resource.Fish; continue; }
            if (t.IsWater || roll >= 90) continue;
            int pick = rnd.Next(100);
            if (t.Relief == Relief.Hills)
                t.Resource = pick < 35 ? Resource.Iron : pick < 60 ? Resource.Stone : pick < 80 ? Resource.Gold : Resource.Uranium;
            else if (t.Terrain == Terrain.Desert || t.Terrain == Terrain.Tundra || t.Terrain == Terrain.Snow)
                t.Resource = pick < 60 ? Resource.Oil : pick < 80 ? Resource.Stone : Resource.Gold;
            else if (!t.Forest)
                t.Resource = pick < 35 ? Resource.Wheat : pick < 65 ? Resource.Cattle : pick < 90 ? Resource.Horses : Resource.Stone;
        }
        return tiles;
    }

    /// <summary>Picks fair, well-spread start tiles on the largest landmass.</summary>
    public static List<int> StartPositions(TileData[] tiles, HexGrid grid, int count, int seed)
    {
        // largest connected passable region
        var comp = new int[tiles.Length];
        int best = 0, bestSize = 0, cur = 0;
        var stack = new Stack<int>();
        for (int i = 0; i < tiles.Length; i++)
        {
            if (comp[i] != 0 || !tiles[i].Passable) continue;
            cur++; int size = 0; comp[i] = cur; stack.Push(i);
            while (stack.Count > 0)
            {
                int t = stack.Pop(); size++;
                foreach (int n in grid.Nb[t]) if (comp[n] == 0 && tiles[n].Passable) { comp[n] = cur; stack.Push(n); }
            }
            if (size > bestSize) { bestSize = size; best = cur; }
        }

        var cand = new List<(int tile, int score)>();
        for (int i = 0; i < tiles.Length; i++)
        {
            if (comp[i] != best || tiles[i].Terrain is Terrain.Snow or Terrain.Desert || tiles[i].Forest) continue;
            int x = grid.X(i), y = grid.Y(i);
            if (x < 2 || y < 2 || x >= grid.W - 2 || y >= grid.H - 2) continue;
            int score = 0;
            foreach (int n in grid.Range(i, 2)) { var (f, p, c) = Rules.TileYield(tiles[n]); score += f * 2 + p + c; }
            cand.Add((i, score));
        }
        if (cand.Count == 0) throw new InvalidOperationException("Map has no suitable start positions");
        cand.Sort((a, b) => b.score != a.score ? b.score.CompareTo(a.score) : a.tile.CompareTo(b.tile));
        var good = cand.Take(Math.Max(count * 6, cand.Count / 3)).ToList();

        var rnd = new Random(seed ^ 0x5bd1e995);
        var res = new List<int> { good[rnd.Next(Math.Min(good.Count, 10))].tile };
        while (res.Count < count)
        {
            int pick = -1; double bestVal = -1;
            foreach (var (tile, score) in good)
            {
                int d = res.Min(r => grid.Distance(r, tile));
                double val = d + score * 0.02;
                if (val > bestVal) { bestVal = val; pick = tile; }
            }
            res.Add(pick);
        }
        return res;
    }
}
