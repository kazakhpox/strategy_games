using IH.Shared;

namespace IH.Core;

public sealed partial class Game
{
    /// <summary>
    /// Consistency audit of the whole world. Returns human-readable violations (empty = healthy).
    /// Used by tests on long AI games to catch defects no single-feature test would notice.
    /// </summary>
    public List<string> CheckInvariants()
    {
        var bad = new List<string>();
        void Fail(string text) { if (bad.Count < 40) bad.Add($"ход {S.Turn}: {text}"); }

        // ---- units
        int indexed = 0;
        for (int t = 0; t < _at.Length; t++)
        {
            var here = _at[t];
            if (here == null) continue;
            indexed += here.Count;
            foreach (var u in here) if (u.Tile != t) Fail($"юнит {u.Id} числится на клетке {t}, а стоит на {u.Tile}");
            if (here.Select(u => u.Owner).Distinct().Count() > 1) Fail($"на клетке {t} юниты разных владельцев");
        }
        if (indexed != S.Units.Count) Fail($"индекс клеток знает {indexed} юнитов, а в мире их {S.Units.Count}");
        foreach (var u in S.Units.Values)
        {
            if (!Grid.Valid(u.Tile)) { Fail($"юнит {u.Id} вне карты"); continue; }
            var tile = S.Tiles[u.Tile];
            var owner = P(u.Owner);
            if (u.Hp < 1 || u.Hp > 100) Fail($"{u.Def.Name} {u.Id}: HP {u.Hp}");
            if (u.Moves < 0) Fail($"{u.Def.Name} {u.Id}: отрицательные ходы");
            if (!owner.Alive && !owner.Neutral) Fail($"{u.Def.Name} {u.Id} принадлежит выбывшему игроку");
            if (u.Def.X.Flying) { /* anywhere */ }
            else if (u.Def.Sea) { if (!tile.IsWater && tile.CityId < 0) Fail($"корабль {u.Def.Name} {u.Id} на суше"); }
            else
            {
                if (tile.Relief == Relief.Mountain) Fail($"{u.Def.Name} {u.Id} стоит на горе");
                if (tile.Terrain == Terrain.Coast && !owner.Has("sailing")) Fail($"{u.Def.Name} {u.Id} в море без мореходства");
                if (tile.Terrain == Terrain.Ocean && !owner.Has("astronomy")) Fail($"{u.Def.Name} {u.Id} в океане без астрономии");
            }
            if (tile.CityId >= 0 && S.Cities.TryGetValue(tile.CityId, out var host) && host.Owner != u.Owner)
                Fail($"{u.Def.Name} {u.Id} стоит в чужом городе {host.Name}");
            if (u.Work != WorkKind.None && u.Def.Role != UnitRole.Worker) Fail($"{u.Def.Name} {u.Id} что-то строит, не будучи рабочим");
        }

        // ---- tiles and cities
        for (int t = 0; t < S.Tiles.Length; t++)
        {
            var tile = S.Tiles[t];
            if (tile.Owner >= S.Players.Count) Fail($"клетка {t}: несуществующий владелец {tile.Owner}");
            if (tile.CityId >= 0 && (!S.Cities.TryGetValue(tile.CityId, out var c) || c.Tile != t)) Fail($"клетка {t} ссылается на город {tile.CityId}, которого там нет");
            if (tile.IsWater && tile.Improvement is not (Improvement.None or Improvement.FishingBoats or Improvement.OilWell)) Fail($"клетка {t}: сухопутное улучшение на воде");
            if (tile.Improvement != Improvement.None && tile.CityId >= 0) Fail($"клетка {t}: улучшение под городом");
        }
        var worked = new Dictionary<int, int>();
        foreach (var c in S.Cities.Values)
        {
            if (S.Tiles[c.Tile].CityId != c.Id) Fail($"город {c.Name}: клетка не ссылается на него");
            if (S.Tiles[c.Tile].Owner != c.Owner) Fail($"город {c.Name}: клетка города принадлежит {S.Tiles[c.Tile].Owner}, а город — {c.Owner}");
            if (c.Pop < 1) Fail($"город {c.Name}: население {c.Pop}");
            if (c.Food < 0 || c.Prod < 0 || c.Culture < 0) Fail($"город {c.Name}: отрицательные запасы");
            if (c.Unrest < 0 || c.Unrest > 100) Fail($"город {c.Name}: брожение {c.Unrest}");
            var owner = P(c.Owner);
            if (!owner.Alive && !owner.Neutral) Fail($"город {c.Name} принадлежит выбывшему игроку");
            foreach (int w in c.Worked)
            {
                if (S.Tiles[w].Owner != c.Owner) Fail($"город {c.Name} обрабатывает чужую клетку {w}");
                if (Grid.Distance(w, c.Tile) > Rules.CityWorkRadius) Fail($"город {c.Name} обрабатывает далёкую клетку {w}");
                if (worked.TryGetValue(w, out int other)) Fail($"клетку {w} обрабатывают два города ({other} и {c.Id})");
                worked[w] = c.Id;
            }
            if (c.Worked.Count > c.Pop) Fail($"город {c.Name}: занятых клеток больше, чем жителей");
            foreach (string item in c.Queue)
                if (item != Rules.Wealth && Rules.ItemCost(item) <= 0) Fail($"город {c.Name}: в очереди неизвестный проект {item}");
            foreach (string b in c.Buildings) if (Rules.Building(b) == null) Fail($"город {c.Name}: неизвестное здание {b}");
            if (c.Religion != 0 && ReligionOf(c.Religion) == null) Fail($"город {c.Name}: несуществующая религия");
        }

        // ---- players
        foreach (var p in S.Players)
        {
            if (p.Neutral) continue;
            var cities = CitiesOf(p.Id).ToList();
            if (!p.Alive)
            {
                if (cities.Count > 0) Fail($"выбывший {p.Name} владеет городами");
                continue;
            }
            int capitals = cities.Count(c => c.Capital);
            if (cities.Count > 0 && capitals != 1) Fail($"{p.Name}: столиц {capitals} при {cities.Count} городах");
            if (p.Gold < 0 || p.Faith < 0 || p.Favor < 0) Fail($"{p.Name}: отрицательная казна (золото {p.Gold}, вера {p.Faith}, одобрение {p.Favor})");
            if (p.Slider < 0 || p.Slider > 100) Fail($"{p.Name}: ползунок науки {p.Slider}");
            if (p.ResearchProgress < 0 || p.CivicProgress < 0) Fail($"{p.Name}: отрицательный прогресс");
            foreach (string tech in p.Known)
            {
                var t = Rules.Tech(tech);
                if (t == null) Fail($"{p.Name}: неизвестная технология {tech}");
                else foreach (string pre in t.Prereqs) if (!p.Has(pre)) Fail($"{p.Name}: {t.Name} изучена без {pre}");
            }
            foreach (string civic in p.Civics)
            {
                var c = Rules.Civic(civic);
                if (c == null) Fail($"{p.Name}: неизвестный институт {civic}");
                else foreach (string pre in c.Prereqs) if (!p.Civics.Contains(pre)) Fail($"{p.Name}: {c.Name} принят без {pre}");
            }
            foreach (string node in p.Nodes)
            {
                var n = Rules.Node(node);
                if (n == null) continue; // the mod may have changed
                foreach (string pre in n.Requires) if (!Knows(p, pre)) Fail($"{p.Name}: узел {n.Name} освоен без {pre}");
            }
            if (p.Research != null && !Rules.AvailableTechs(p.Known, p.Future).Any(t => t.Id == p.Research)) Fail($"{p.Name}: изучает недоступное {p.Research}");
            if (p.Civic != null && !Rules.AvailableCivics(p.Civics, p.CivicLevel).Any(c => c.Id == p.Civic)) Fail($"{p.Name}: принимает недоступный {p.Civic}");
            foreach (var (branch, power) in p.Power) if (power < 0 || power > Rules.PowerCap) Fail($"{p.Name}: ресурс ветки {branch} = {power}");
            if (double.IsNaN(p.SciRate) || double.IsNaN(p.Income) || double.IsNaN(p.Itr)) Fail($"{p.Name}: NaN в экономике");
        }
        foreach (var r in S.Religions)
            if (!S.Cities.ContainsKey(r.HolyCity)) Fail($"религия {r.Name}: священного города больше нет");
        if (S.Chair >= 0 && (S.Chair >= S.Players.Count || P(S.Chair).Neutral)) Fail($"председатель конгресса {S.Chair} некорректен");
        return bad;
    }
}
