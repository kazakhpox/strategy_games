using System.Text.Encodings.Web;
using System.Text.Json;
using IH.Shared;

namespace IH.Core;

/// <summary>
/// Loads custom development branches from JSON files (one branch per file). A file whose branch id matches
/// a built-in branch replaces it; "Enabled": false switches a branch off. Broken files are skipped with a
/// message that says what to fix, never silently.
/// </summary>
public static class ModLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, IgnoreReadOnlyProperties = true,
    };

    public const int MaxBranches = 12, MaxNodes = 60;

    public static string ToJson(Rules.BranchDef b) => JsonSerializer.Serialize(b, Json);

    public static Rules.BranchDef Parse(string json) =>
        JsonSerializer.Deserialize<Rules.BranchDef>(json, Json) ?? throw new InvalidDataException("файл пуст");

    /// <summary>Checks one branch against everything already accepted. Returns the problems found (empty = fine).</summary>
    public static List<string> Validate(Rules.BranchDef b, IEnumerable<Rules.BranchDef> others)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(b.Id)) errors.Add("у ветки нет Id");
        if (string.IsNullOrWhiteSpace(b.Name)) errors.Add("у ветки нет Name");
        if (b.FromScience < 0 || b.FromCulture < 0 || b.FromFaith < 0) errors.Add("FromScience/FromCulture/FromFaith не могут быть отрицательными");
        if (b.FromScience + b.FromCulture + b.FromFaith <= 0) errors.Add("ветка ни от чего не растёт: задайте FromScience, FromCulture или FromFaith больше нуля");
        if (b.Nodes == null || b.Nodes.Length == 0) { errors.Add("в ветке нет узлов (Nodes)"); return errors; }
        if (b.Nodes.Length > MaxNodes) errors.Add($"слишком много узлов: {b.Nodes.Length}, максимум {MaxNodes}");

        var foreign = others.Where(o => o.Id != b.Id).SelectMany(o => o.Nodes).Select(n => n.Id).ToHashSet();
        var own = new HashSet<string>();
        foreach (var n in b.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) { errors.Add("у узла нет Id"); continue; }
            if (string.IsNullOrWhiteSpace(n.Name)) errors.Add($"узел {n.Id}: нет Name");
            if (n.Cost <= 0) errors.Add($"узел {n.Id}: Cost должен быть больше нуля");
            if (!own.Add(n.Id)) errors.Add($"узел {n.Id} описан дважды");
            if (foreign.Contains(n.Id)) errors.Add($"узел {n.Id}: такой Id уже есть в другой ветке");
            if (Rules.Tech(n.Id) != null || Rules.Civic(n.Id) != null) errors.Add($"узел {n.Id}: Id совпадает с технологией или институтом");
            n.Requires ??= Array.Empty<string>();
            n.Effect ??= new Rules.Bonus();
            n.Description ??= "";
        }
        foreach (var n in b.Nodes)
            foreach (string r in n.Requires ?? Array.Empty<string>())
            {
                if (r == n.Id) errors.Add($"узел {n.Id} требует сам себя");
                else if (!own.Contains(r) && !foreign.Contains(r) && Rules.Tech(r) == null && Rules.Civic(r) == null)
                    errors.Add($"узел {n.Id}: требование «{r}» не найдено среди технологий, институтов и узлов веток");
            }
        b.PowerResources ??= Array.Empty<string>();
        foreach (string r in b.PowerResources)
            if (Rules.ParseResource(r) == Resource.None) errors.Add($"PowerResources: ресурс «{r}» неизвестен (см. --list-ids)");
        // content opened by the branch
        b.Units ??= Array.Empty<Rules.ModUnit>(); b.Buildings ??= Array.Empty<Rules.ModBuilding>(); b.Actions ??= Array.Empty<Rules.ActionDef>();
        var taken = others.Where(o => o.Id != b.Id).SelectMany(o => o.Units.Select(u => u.Id).Concat(o.Buildings.Select(x => x.Id)).Concat(o.Actions.Select(x => x.Id))).ToHashSet();
        bool Resolves(string r) => string.IsNullOrEmpty(r) || own.Contains(r) || foreign.Contains(r) || Rules.Tech(r) != null || Rules.Civic(r) != null;
        void CheckId(string kind, string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id)) { errors.Add($"{kind}: нет Id"); return; }
            if (string.IsNullOrWhiteSpace(name)) errors.Add($"{kind} {id}: нет Name");
            if (!taken.Add(id) || Rules.IsBaseContentId(id) || own.Contains(id)) errors.Add($"{kind} {id}: такой Id уже занят");
        }
        foreach (var u in b.Units)
        {
            u.Requires ??= "";
            CheckId("юнит", u.Id, u.Name);
            if (u.Cost <= 0) errors.Add($"юнит {u.Id}: Cost должен быть больше нуля");
            if (u.Str <= 0 || u.Str > 500) errors.Add($"юнит {u.Id}: Str должен быть от 1 до 500");
            if (u.Moves < 1 || u.Moves > 8) errors.Add($"юнит {u.Id}: Moves должен быть от 1 до 8");
            if (!Resolves(u.Requires)) errors.Add($"юнит {u.Id}: требование «{u.Requires}» не найдено");
            u.Needs ??= "";
            if (u.Needs.Length > 0 && Rules.ParseResource(u.Needs) == Resource.None) errors.Add($"юнит {u.Id}: ресурс «{u.Needs}» неизвестен (см. --list-ids)");
            if (u.Ranged < 0 || u.Ranged > 4) errors.Add($"юнит {u.Id}: Ranged (дальность) должен быть от 0 до 4");
            if (u.Ranged > 0 && u.RangedStr <= 0) errors.Add($"юнит {u.Id}: для дальнего боя задайте RangedStr");
            if (u.FearPct < 0 || u.FearPct > 80) errors.Add($"юнит {u.Id}: FearPct должен быть от 0 до 80");
            if (u.HealAura < 0 || u.Regen < 0 || u.Lifesteal < 0 || u.ForestPct < 0) errors.Add($"юнит {u.Id}: способности не могут быть отрицательными");
        }
        foreach (var m in b.Buildings)
        {
            m.Requires ??= "";
            CheckId("здание", m.Id, m.Name);
            if (m.Cost <= 0) errors.Add($"здание {m.Id}: Cost должен быть больше нуля");
            if (!Resolves(m.Requires)) errors.Add($"здание {m.Id}: требование «{m.Requires}» не найдено");
            if (m.Power < 0 || m.UnitHeal < 0 || m.Bombard < 0 || m.AuraDefPct < 0) errors.Add($"здание {m.Id}: механики не могут быть отрицательными");
            if (m.BombardRange > 4 || m.AuraRange > 4) errors.Add($"здание {m.Id}: BombardRange и AuraRange не больше 4");
        }
        var unitIds = b.Units.Select(u => u.Id).Concat(others.Where(o => o.Id != b.Id).SelectMany(o => o.Units).Select(u => u.Id)).ToHashSet();
        bool UnitExists(string id) => unitIds.Contains(id) || Rules.IsBaseUnitId(id);
        foreach (var a in b.Actions)
        {
            a.Requires ??= ""; a.Description ??= "";
            CheckId("действие", a.Id, a.Name);
            if (!Rules.ActionKinds.Contains(a.Kind)) errors.Add($"действие {a.Id}: Kind «{a.Kind}» неизвестен; допустимы {string.Join(", ", Rules.ActionKinds)}");
            if (a.Amount <= 0) errors.Add($"действие {a.Id}: Amount должен быть больше нуля");
            a.Caster ??= ""; a.Unit ??= ""; a.Stat ??= ""; a.Target = string.IsNullOrEmpty(a.Target) ? "None" : a.Target;
            if (a.CostFaith < 0 || a.CostGold < 0 || a.CostPower < 0 || a.CostFaith + a.CostGold + a.CostPower <= 0)
                errors.Add($"действие {a.Id}: задайте цену CostPower, CostFaith и/или CostGold");
            if (a.Target is not ("None" or "Tile" or "City")) errors.Add($"действие {a.Id}: Target должен быть None, Tile или City");
            if (a.Caster.Length > 0 && !UnitExists(a.Caster)) errors.Add($"действие {a.Id}: юнит-исполнитель «{a.Caster}» не найден");
            if (a.Range < 0 || a.Range > 6) errors.Add($"действие {a.Id}: Range должен быть от 0 до 6");
            string need = a.Kind switch
            {
                "Damage" or "Forest" or "Shield" => "Tile", "Bless" or "Teleport" => "City", "Summon" => a.Target == "None" ? "None" : a.Target,
                _ => "None",
            };
            if (a.Target != need) errors.Add($"действие {a.Id}: для Kind «{a.Kind}» нужен Target «{need}»");
            if (a.Kind is "HealArea" or "Teleport" && a.Caster.Length == 0) errors.Add($"действие {a.Id}: Kind «{a.Kind}» требует исполнителя (Caster)");
            if (a.Kind == "Summon" && !UnitExists(a.Unit)) errors.Add($"действие {a.Id}: призываемый юнит «{a.Unit}» не найден");
            if (a.Kind == "Bless" && !Rules.BlessStats.Contains(a.Stat)) errors.Add($"действие {a.Id}: Stat должен быть одним из {string.Join(", ", Rules.BlessStats)}");
            if (a.Kind is "Bless" or "Shield" && a.Duration < 1) errors.Add($"действие {a.Id}: задайте Duration (сколько ходов действует)");
            if (a.Cooldown < 1) errors.Add($"действие {a.Id}: Cooldown должен быть не меньше 1");
            if (!Resolves(a.Requires)) errors.Add($"действие {a.Id}: требование «{a.Requires}» не найдено");
        }
        if (b.Units.Length + b.Buildings.Length + b.Actions.Length > 60) errors.Add("слишком много юнитов, зданий и действий в одной ветке (максимум 60)");
        if (errors.Count > 0) return errors;

        // a node must be reachable: no circular requirements inside the branch
        var state = new Dictionary<string, int>();
        var byId = b.Nodes.ToDictionary(n => n.Id);
        bool Cyclic(string id)
        {
            if (!byId.TryGetValue(id, out var n)) return false;
            if (state.TryGetValue(id, out int s)) return s == 1;
            state[id] = 1;
            foreach (string r in n.Requires) if (Cyclic(r)) return true;
            state[id] = 2;
            return false;
        }
        foreach (var n in b.Nodes)
            if (Cyclic(n.Id)) { errors.Add($"узел {n.Id} участвует в круговой зависимости — его невозможно освоить"); break; }
        return errors;
    }

    /// <summary>Built-in branches plus every valid *.json in the folder. Missing folder = built-ins only.</summary>
    public static List<Rules.BranchDef> Load(string? dir, Action<string>? log = null)
    {
        var result = Rules.DefaultBranches();
        if (dir == null || !Directory.Exists(dir)) return result;
        foreach (string path in Directory.GetFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            try
            {
                var b = Parse(File.ReadAllText(path));
                if (Rules.DefaultBranches().Any(d => d.Id == b.Id) && b.Version < Rules.BuiltInVersion)
                {
                    // a copy of a built-in branch exported by an older game would hide the newer built-in content
                    log?.Invoke($"  Мод {file} пропущен: это устаревшая копия встроенной ветки (Version {b.Version} < {Rules.BuiltInVersion}). Свежий образец — в папке examples.");
                    continue;
                }
                var rest = result.Where(o => o.Id != b.Id).ToList();
                var errors = Validate(b, rest);
                if (errors.Count > 0)
                {
                    log?.Invoke($"! Мод {file} пропущен:");
                    foreach (string e in errors.Take(8)) log?.Invoke($"    - {e}");
                    continue;
                }
                if (rest.Count >= MaxBranches) { log?.Invoke($"! Мод {file} пропущен: веток уже {MaxBranches}"); continue; }
                // a replacement keeps the position of the branch it replaces
                int at = result.FindIndex(o => o.Id == b.Id);
                if (at >= 0) result[at] = b; else result.Add(b);
                log?.Invoke(b.Enabled ? $"  Ветка «{b.Name}» загружена из {file}: узлов {b.Nodes.Length}, растёт от {b.Fuel()}" : $"  Ветка «{b.Name}» отключена ({file})");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                log?.Invoke($"! Мод {file} пропущен: ошибка в JSON — {ex.Message}");
            }
        }
        // a requirement may point into a branch that was switched off or failed to load
        var known = result.Where(b => b.Enabled).SelectMany(b => b.Nodes).Select(n => n.Id).ToHashSet();
        foreach (var b in result.Where(b => b.Enabled).ToList())
        {
            var dangling = b.Nodes.SelectMany(n => n.Requires.Where(r => !known.Contains(r) && Rules.Tech(r) == null && Rules.Civic(r) == null).Select(r => $"{n.Id} → {r}")).ToList();
            if (dangling.Count == 0) continue;
            log?.Invoke($"! Ветка «{b.Name}» отключена: её узлы требуют то, чего нет ({string.Join(", ", dangling.Take(5))})");
            b.Enabled = false;
        }
        return result;
    }

    /// <summary>Writes the built-in branches and a short guide, as a starting point for the player's own files.</summary>
    /// <summary>
    /// Writes the built-in branches and a guide into "examples" inside the mods folder. That subfolder is
    /// never loaded, so it can be refreshed on every start without touching the player's own files.
    /// </summary>
    public static void Export(string dir)
    {
        string examples = Path.Combine(dir, "examples");
        Directory.CreateDirectory(examples);
        foreach (var b in Rules.DefaultBranches()) File.WriteAllText(Path.Combine(examples, b.Id + ".json"), ToJson(b));
        File.WriteAllText(Path.Combine(dir, "КАК_СДЕЛАТЬ_ВЕТКУ.txt"), Guide);
    }

    public const string Guide = """
        СВОИ ВЕТКИ РАЗВИТИЯ
        ===================
        Каждый файл *.json прямо в этой папке — одна ветка. Образцы лежат в подпапке examples
        (она не загружается и обновляется при каждом запуске сервера): скопируйте оттуда magic.json
        сюда, переименуйте, поменяйте Id — и правьте.
        Изменения вступают в силу при следующем запуске сервера (кнопка «Создать сервер» в игре).
        Файлы нужны только хосту: остальным игрокам ветки передаются по сети.

        ВЕТКА
          Id, Name, Description
          FromScience / FromCulture / FromFaith   какая доля науки, культуры, веры за ход идёт в ветку (0.4 = 40%)
          PowerName     название собственного ресурса ветки ("Мана", "Духи" ...) — на него творятся действия
          PowerResources  ресурсы карты (английские имена: Furs, Mercury ...), каждый подключённый даёт +1 ресурса ветки
          Version       для своих веток не нужен; чтобы ЗАМЕНИТЬ встроенную ветку, оставьте Version как в образце
          Enabled       false — отключить ветку
          Nodes, Units, Buildings, Actions

        УЗЕЛ (Nodes)
          Id, Name, Cost
          Requires  список id того, что нужно освоить раньше: узлы этой или другой ветки ("sh_trance"),
                    технологии ("writing"), социальные институты ("c_rites")
          Power     сколько ресурса ветки узел даёт каждый ход
          Effect    бонус всей империи: SciPct GoldPct ProdPct CulPct FaithPct StrPct UpkeepCutPct
                    Happy Calm Food Culture Favor

        ЮНИТ (Units)  — Requires здесь и ниже: один id, который открывает
          Id, Name, Requires, Cost, Str (сила), Moves, CityDefPct, Sight, Sea
          Needs        ресурс карты, без которого юнита не построить (английское имя, например Stone)
          Способности:
            Ranged + RangedStr   дальний бой: дальность и сила выстрела, без ответного урона
            HealAura     лечит союзников на своей и соседних клетках за ход
            Regen        лечит себя каждый ход
            FearPct      враги рядом слабее на столько процентов
            ForestPct    бонус силы в лесу
            Lifesteal    HP после победы в ближнем бою
            Stealth      true — виден врагу только вплотную
            Flying       true — летает через горы и воду
            Construct    true — без содержания, не лечится отдыхом, не боится
            SummonOnly   true — не строится, только призывается действием

        ЗДАНИЕ (Buildings)
          Id, Name, Requires, Cost; доходы: Food Commerce ProdPct SciPct GoldPct Culture CulPct Happy DefPct Calm Faith Upkeep
          Механики:
            Power                     ресурс ветки за ход
            UnitHeal                  лечит ваших юнитов в городе и рядом
            Bombard + BombardRange    каждый ход бьёт одного врага рядом с городом
            AuraDefPct + AuraRange    бонус обороны вашим юнитам вокруг города

        ДЕЙСТВИЕ (Actions)
          Id, Name, Requires, Kind, Amount, Cooldown (ходов до повтора)
          CostPower / CostFaith / CostGold   цена
          Caster    id юнита-исполнителя; пусто — действие империи (из окна «Ветки»)
          Target    None, Tile (клетка) или City (ваш город)
          Range     дальность от исполнителя; Radius — соседние клетки получают половину урона
          Duration  сколько ходов длится эффект или живёт призванный юнит
          Kind:
            Heal      лечит всех ваших юнитов           Harvest  +Amount пищи в каждом городе
            Calm      снимает брожение                  Gold / Science / Culture  +Amount
            Reveal    открывает карту у столицы         Smite    урон врагам на вашей земле
            HealArea  лечит вокруг исполнителя (нужен Caster)
            Damage    урон по клетке (Target Tile)
            Summon    призывает юнита Unit (Target None, Tile или City)
            Bless     +Amount% к Stat (Food, Prod, Sci, Gold, Culture) в городе (Target City, Duration)
            Forest    выращивает лес (Target Tile)
            Shield    +Amount% обороны юнитам на клетке (Target Tile, Duration)
            Teleport  переносит исполнителя со спутниками в ваш город (Target City, нужен Caster)

        Если в файле ошибка, сервер пропустит его и напишет в консоли, что именно не так.
        Список всех id технологий, институтов и встроенных узлов: IH.Server --list-ids
        """;
}
