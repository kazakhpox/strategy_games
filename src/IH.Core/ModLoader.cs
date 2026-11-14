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
    public static void Export(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var b in Rules.DefaultBranches()) File.WriteAllText(Path.Combine(dir, b.Id + ".json"), ToJson(b));
        File.WriteAllText(Path.Combine(dir, "КАК_СДЕЛАТЬ_ВЕТКУ.txt"), Guide);
    }

    public const string Guide = """
        СВОИ ВЕТКИ РАЗВИТИЯ
        ===================
        Каждый файл *.json в этой папке — одна ветка. Скопируйте magic.json, переименуйте и измените.
        Изменения вступают в силу при следующем запуске сервера (кнопка «Создать сервер» в игре).
        Файлы нужны только хосту: остальным игрокам ветки передаются по сети.

        Поля ветки:
          Id            короткое имя латиницей, уникальное (например "necromancy")
          Name          название в игре
          Description   пояснение
          FromScience   какая доля науки за ход идёт в ветку (0.4 = 40%)
          FromCulture   то же для культуры
          FromFaith     то же для веры
          Enabled       false — отключить ветку (так можно убрать и встроенную)
          Nodes         список узлов

        Поля узла:
          Id        уникальное имя латиницей (например "nc_bones")
          Name      название
          Cost      цена в очках ветки
          Requires  что нужно освоить раньше. Сюда можно писать id:
                      - узлов этой или любой другой ветки ("sh_trance")
                      - технологий ("writing", "iron_working", "education" ...)
                      - социальных институтов ("c_tribal", "c_rites", "c_humanism" ...)
          Effect    бонус всей империи; любые из полей:
                      SciPct GoldPct ProdPct CulPct FaithPct   проценты к науке, золоту, производству, культуре, вере
                      StrPct          проценты к силе юнитов
                      UpkeepCutPct    на сколько процентов дешевле содержание
                      Happy           довольство в каждом городе
                      Calm            на сколько меньше брожения за ход
                      Food  Culture   пища и культура в каждом городе
                      Favor           дипломатическое одобрение за ход

        Ветка с тем же Id, что у встроенной ("magic", "shamanism"), заменяет её.
        Если в файле ошибка, сервер пропустит его и напишет в консоли, что именно не так.
        Список всех id технологий и институтов: IH.Server --list-ids
        """;
}
