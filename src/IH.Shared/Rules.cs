namespace IH.Shared;

public enum Terrain : byte { Ocean, Coast, Grassland, Plains, Desert, Tundra, Snow }
public enum Relief : byte { Flat, Hills, Mountain }
public enum Resource : byte { None, Wheat, Cattle, Fish, Iron, Horses, Gold, Stone, Oil, Uranium }
public enum Improvement : byte { None, Farm, Mine, Cottage }
public enum WorkKind : byte { None, Farm, Mine, Cottage, Road }
public enum UnitRole : byte { Military, Settler, Worker, Scout }

/// <summary>Static tile data; identical representation on server and client.</summary>
public struct TileData
{
    public Terrain Terrain;
    public Relief Relief;
    public bool Forest;
    public Resource Resource;
    public Improvement Improvement;
    public bool Road;
    public int Owner;   // -1 = nobody
    public int CityId;  // -1 = no city

    public const ulong ExploredBit = 1UL << 62;
    public const ulong VisibleBit = 1UL << 63;

    public readonly bool IsWater => Terrain <= Terrain.Coast;
    public readonly bool Passable => !IsWater && Relief != Relief.Mountain;

    public readonly ulong Pack()
    {
        ulong p = (ulong)Terrain;
        p |= (ulong)Relief << 3;
        p |= (Forest ? 1UL : 0) << 5;
        p |= (ulong)Resource << 6;
        p |= (ulong)Improvement << 10;
        p |= (Road ? 1UL : 0) << 12;
        p |= (ulong)(Owner + 1) << 13;
        p |= (ulong)(CityId + 1) << 20;
        return p;
    }

    public static TileData Unpack(ulong p) => new()
    {
        Terrain = (Terrain)(p & 7),
        Relief = (Relief)((p >> 3) & 3),
        Forest = ((p >> 5) & 1) != 0,
        Resource = (Resource)((p >> 6) & 15),
        Improvement = (Improvement)((p >> 10) & 3),
        Road = ((p >> 12) & 1) != 0,
        Owner = (int)((p >> 13) & 127) - 1,
        CityId = (int)((p >> 20) & 0xFFFFFF) - 1,
    };
}

public sealed record UnitDef(string Id, string Name, int Cost, int Str, int Moves, string? Tech,
    UnitRole Role = UnitRole.Military, int Sight = 1, int CityDefPct = 0);

public sealed record BuildingDef(string Id, string Name, int Cost, string? Tech)
{
    public int Food, Commerce, ProdPct, SciPct, GoldPct, Culture, CulPct, Happy, DefPct, Upkeep, Calm, MaintCutPct;
    public string Describe()
    {
        var p = new List<string>();
        if (Food != 0) p.Add($"+{Food} пищи");
        if (Commerce != 0) p.Add($"+{Commerce} коммерции");
        if (ProdPct != 0) p.Add($"+{ProdPct}% производства");
        if (SciPct != 0) p.Add($"+{SciPct}% науки");
        if (GoldPct != 0) p.Add($"+{GoldPct}% золота");
        if (Culture != 0) p.Add($"+{Culture} культуры");
        if (CulPct != 0) p.Add($"+{CulPct}% культуры");
        if (Happy != 0) p.Add($"+{Happy} довольства");
        if (DefPct != 0) p.Add($"+{DefPct}% обороны");
        if (Calm != 0) p.Add($"-{Calm} брожения/ход");
        if (MaintCutPct != 0) p.Add($"-{MaintCutPct}% содержания города");
        if (Upkeep != 0) p.Add($"содержание {Upkeep}");
        return string.Join(", ", p);
    }
}

public sealed record TechDef(string Id, string Name, int Cost, int Era, string[] Prereqs, int Branch = -1, int Level = 0);

/// <summary>Game data tables and pure rule functions shared by server and client.</summary>
public static class Rules
{
    public const int MovePoint = 2;          // movement is tracked in half-points
    public const int CityWorkRadius = 2;
    public const int MinCityDistance = 3;
    public const int MaxQueue = 8;
    public const string Wealth = "p:wealth";

    public static readonly string[] EraNames =
    {
        "Древний мир", "Античность", "Средневековье", "Возрождение", "Индустриальная эра",
        "Новейшее время", "Информационная эра",
    };

    public static readonly string[] FutureBranchNames =
        { "Нанотехнологии", "Космическая колонизация", "Квантовые сети", "Социальная инженерия" };

    public static readonly string[] FutureBranchEffects =
    {
        "+5% производства и +5% силы юнитов за уровень",
        "+5% золота за уровень",
        "+5% науки за уровень",
        "+5% культуры за уровень, +1 довольства за каждые 2 уровня, -1 брожения/ход за уровень",
    };

    public const int BranchNano = 0, BranchSpace = 1, BranchQuantum = 2, BranchSocial = 3;
    private static readonly string[] FutureGate = { "robotics", "fusion", "computers", "genetics" };

    public static string EraName(int era)
    {
        if (era < EraNames.Length) return EraNames[era];
        string[] future = { "Эра синтеза", "Орбитальная эра", "Квантовая эра", "Постчеловеческая эра" };
        int f = era - EraNames.Length;
        return f < future.Length ? future[f] : $"Горизонт {f - future.Length + 1}";
    }

    // ---------------------------------------------------------------- units
    public static readonly UnitDef[] Units =
    {
        new("settler", "Поселенец", 60, 0, 2, null, UnitRole.Settler),
        new("worker", "Рабочий", 35, 0, 2, null, UnitRole.Worker),
        new("scout", "Разведчик", 15, 1, 2, "hunting", UnitRole.Scout, Sight: 2),
        new("warrior", "Воин", 15, 2, 1, null),
        new("archer", "Лучник", 25, 3, 1, "archery", CityDefPct: 50),
        new("spearman", "Копейщик", 35, 4, 1, "bronze_working"),
        new("swordsman", "Мечник", 45, 6, 1, "iron_working"),
        new("horseman", "Всадник", 50, 6, 2, "horseback"),
        new("longbowman", "Длинный лучник", 60, 6, 1, "feudalism", CityDefPct: 50),
        new("knight", "Рыцарь", 90, 10, 2, "guilds"),
        new("musketman", "Мушкетёр", 100, 9, 1, "gunpowder", CityDefPct: 25),
        new("rifleman", "Стрелок", 140, 14, 1, "rifling", CityDefPct: 25),
        new("cavalry", "Кавалерия", 150, 15, 2, "military_tradition"),
        new("infantry", "Пехота", 200, 20, 1, "assembly_line", CityDefPct: 25),
        new("tank", "Танк", 260, 28, 2, "industrialism"),
        new("mech_infantry", "Мотопехота", 320, 32, 2, "robotics", CityDefPct: 25),
        new("nano_trooper", "Нанодесант", 420, 42, 2, "f0_1", CityDefPct: 25),
    };

    public static int UnitIndex(string id) => Array.FindIndex(Units, u => u.Id == id);

    // ------------------------------------------------------------ buildings
    public static readonly BuildingDef[] Buildings =
    {
        new("palace", "Дворец", 0, null) { Commerce = 6, Culture = 2, Happy = 1, Calm = 2 },
        new("monument", "Монумент", 30, "mysticism") { Culture = 2 },
        new("granary", "Амбар", 60, "pottery") { Food = 2, Upkeep = 1 },
        new("walls", "Стены", 50, "masonry") { DefPct = 50 },
        new("library", "Библиотека", 90, "writing") { SciPct = 25, Culture = 1, Upkeep = 1 },
        new("temple", "Храм", 80, "priesthood") { Happy = 1, Culture = 1, Calm = 1, Upkeep = 1 },
        new("market", "Рынок", 120, "currency") { GoldPct = 25, Happy = 1, Upkeep = 1 },
        new("forge", "Кузница", 120, "metal_casting") { ProdPct = 25, Upkeep = 1 },
        new("courthouse", "Суд", 120, "code_of_laws") { MaintCutPct = 50, Calm = 2, Upkeep = 1 },
        new("colosseum", "Колизей", 100, "construction") { Happy = 2, Upkeep = 1 },
        new("university", "Университет", 200, "education") { SciPct = 25, Culture = 2, Upkeep = 2 },
        new("bank", "Банк", 200, "banking") { GoldPct = 25, Upkeep = 1 },
        new("observatory", "Обсерватория", 150, "astronomy") { SciPct = 25, Upkeep = 1 },
        new("theatre", "Театр", 150, "printing_press") { CulPct = 50, Happy = 1, Upkeep = 1 },
        new("factory", "Фабрика", 250, "assembly_line") { ProdPct = 25, Upkeep = 2 },
        new("power_plant", "Электростанция", 280, "electricity") { ProdPct = 25, Upkeep = 2 },
        new("broadcast", "Радиовышка", 220, "radio") { CulPct = 50, Happy = 1, Calm = 1, Upkeep = 2 },
        new("laboratory", "Лаборатория", 300, "computers") { SciPct = 25, Upkeep = 2 },
        new("nanoforge", "Нанокузня", 500, "f0_1") { ProdPct = 50, Upkeep = 3 },
        new("orbital_hub", "Орбитальный хаб", 500, "f1_1") { GoldPct = 50, Upkeep = 3 },
        new("quantum_lab", "Квантовая лаборатория", 500, "f2_1") { SciPct = 50, Upkeep = 3 },
        new("social_center", "Центр соц. инженерии", 500, "f3_1") { Happy = 3, Calm = 4, CulPct = 25, Upkeep = 3 },
    };

    public static BuildingDef? Building(string id) => Array.Find(Buildings, b => b.Id == id);

    // ---------------------------------------------------------------- techs
    private static TechDef T(string id, string name, int cost, int era, params string[] pre) => new(id, name, cost, era, pre);

    public static readonly TechDef[] Techs =
    {
        T("agriculture", "Земледелие", 25, 0), T("mining", "Горное дело", 30, 0), T("hunting", "Охота", 25, 0),
        T("mysticism", "Мистицизм", 30, 0), T("wheel", "Колесо", 40, 0),
        T("pottery", "Гончарное дело", 40, 0, "agriculture"), T("archery", "Стрельба из лука", 40, 0, "hunting"),
        T("animal_husbandry", "Животноводство", 50, 0, "agriculture"),
        T("bronze_working", "Обработка бронзы", 60, 0, "mining"), T("masonry", "Каменная кладка", 50, 0, "mining"),
        T("writing", "Письменность", 70, 0, "pottery"), T("priesthood", "Жречество", 60, 0, "mysticism"),

        T("iron_working", "Обработка железа", 110, 1, "bronze_working"),
        T("horseback", "Верховая езда", 120, 1, "animal_husbandry", "wheel"),
        T("mathematics", "Математика", 130, 1, "writing"),
        T("metal_casting", "Литьё", 150, 1, "bronze_working", "pottery"),
        T("currency", "Деньги", 160, 1, "mathematics"),
        T("code_of_laws", "Свод законов", 170, 1, "writing", "priesthood"),
        T("construction", "Строительство", 180, 1, "masonry", "mathematics"),
        T("philosophy", "Философия", 200, 1, "code_of_laws"),

        T("feudalism", "Феодализм", 300, 2, "code_of_laws", "iron_working"),
        T("machinery", "Механика", 320, 2, "metal_casting", "construction"),
        T("guilds", "Гильдии", 420, 2, "feudalism", "machinery", "horseback"),
        T("education", "Образование", 450, 2, "philosophy", "mathematics"),
        T("banking", "Банковское дело", 480, 2, "currency", "guilds"),
        T("astronomy", "Астрономия", 550, 2, "education"),

        T("printing_press", "Книгопечатание", 600, 3, "education", "machinery"),
        T("gunpowder", "Порох", 650, 3, "guilds"),
        T("military_tradition", "Военная традиция", 800, 3, "gunpowder"),
        T("economics", "Экономика", 850, 3, "banking", "printing_press"),
        T("scientific_method", "Научный метод", 900, 3, "printing_press", "astronomy"),

        T("steam_power", "Паровая машина", 1200, 4, "scientific_method"),
        T("rifling", "Нарезное оружие", 1300, 4, "gunpowder", "steam_power"),
        T("electricity", "Электричество", 1500, 4, "scientific_method"),
        T("assembly_line", "Конвейер", 1700, 4, "steam_power", "economics"),

        T("industrialism", "Индустриализация", 2200, 5, "assembly_line", "electricity"),
        T("radio", "Радио", 2000, 5, "electricity"),
        T("computers", "Компьютеры", 2800, 5, "radio"),
        T("robotics", "Робототехника", 3400, 5, "computers", "industrialism"),

        T("genetics", "Генетика", 3600, 6, "computers"),
        T("fusion", "Термоядерный синтез", 4200, 6, "robotics"),
    };

    private static readonly Dictionary<string, TechDef> TechById = Techs.ToDictionary(t => t.Id);
    private static readonly string[] Roman = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };

    public static string FutureId(int branch, int level) => $"f{branch}_{level}";

    public static int FutureCost(int level) => (int)(5000 * Math.Pow(1 + 0.6 * (level - 1), 1.5));

    /// <summary>Resolves both historical techs and procedurally generated future techs (f{branch}_{level}).</summary>
    public static TechDef? Tech(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (TechById.TryGetValue(id, out var t)) return t;
        if (id[0] != 'f') return null;
        int us = id.IndexOf('_');
        if (us != 2 || !int.TryParse(id.AsSpan(1, 1), out int branch) || branch > 3) return null;
        if (!int.TryParse(id.AsSpan(3), out int level) || level < 1 || level > 100000) return null;
        string[] pre = level == 1
            ? new[] { FutureGate[branch] }
            : new[] { FutureId(branch, level - 1), FutureId((branch + 1) % 4, level - 1) };
        string num = level < Roman.Length ? Roman[level] : level.ToString();
        return new TechDef(id, $"{FutureBranchNames[branch]} {num}", FutureCost(level), 7 + (level - 1) / 3, pre, branch, level);
    }

    public static bool HasTech(ICollection<string> known, int[] future, string? id)
    {
        if (id == null) return true;
        if (known.Contains(id)) return true;
        var t = Tech(id);
        return t != null && t.Branch >= 0 && future[t.Branch] >= t.Level;
    }

    /// <summary>Techs the player may research right now. The list never runs dry: future branches are endless.</summary>
    public static List<TechDef> AvailableTechs(ICollection<string> known, int[] future)
    {
        var res = new List<TechDef>();
        foreach (var t in Techs)
            if (!known.Contains(t.Id) && t.Prereqs.All(known.Contains)) res.Add(t);
        for (int b = 0; b < 4; b++)
        {
            var t = Tech(FutureId(b, future[b] + 1))!;
            if (t.Prereqs.All(p => HasTech(known, future, p))) res.Add(t);
        }
        return res;
    }

    public static int EraOf(ICollection<string> known, int[] future)
    {
        int era = 0;
        foreach (var id in known) if (TechById.TryGetValue(id, out var t) && t.Era > era) era = t.Era;
        int maxF = future.Max();
        if (maxF > 0) era = Math.Max(era, 7 + (maxF - 1) / 3);
        return era;
    }

    // ---------------------------------------------------------------- tiles
    public static (int f, int p, int c) TileYield(in TileData t, bool cityCenter = false)
    {
        if (t.Relief == Relief.Mountain) return (0, 0, 0);
        int f = 0, p = 0, c = 0;
        switch (t.Terrain)
        {
            case Terrain.Ocean: f = 1; c = 1; break;
            case Terrain.Coast: f = 1; c = 2; break;
            case Terrain.Grassland: f = 2; break;
            case Terrain.Plains: f = 1; p = 1; break;
            case Terrain.Tundra: f = 1; break;
        }
        if (t.Relief == Relief.Hills) { f = Math.Max(0, f - 1); p += 1; }
        if (t.Forest) p += 1;
        switch (t.Resource)
        {
            case Resource.Wheat: f += 2; break;
            case Resource.Cattle: f += 1; p += 1; break;
            case Resource.Fish: f += 2; break;
            case Resource.Iron: p += 2; break;
            case Resource.Horses: p += 1; c += 1; break;
            case Resource.Gold: c += 3; break;
            case Resource.Stone: p += 2; break;
            case Resource.Oil: p += 2; c += 1; break;
            case Resource.Uranium: p += 1; c += 2; break;
        }
        switch (t.Improvement)
        {
            case Improvement.Farm: f += 1; break;
            case Improvement.Mine: p += 2; break;
            case Improvement.Cottage: c += 2; break;
        }
        if (t.Road && !t.IsWater) c += 1;
        if (cityCenter) { f = Math.Max(f, 2); p = Math.Max(p, 2); c = Math.Max(c, 2); }
        return (f, p, c);
    }

    /// <summary>Step cost in half-points; -1 when the destination terrain is impassable for land units.</summary>
    public static int MoveCost(in TileData from, in TileData to)
    {
        if (!to.Passable) return -1;
        if (from.Road && to.Road) return 1;
        return to.Relief == Relief.Hills || to.Forest ? 2 * MovePoint : MovePoint;
    }

    public static string? WorkTech(WorkKind k) => k switch
    {
        WorkKind.Farm => "agriculture", WorkKind.Mine => "mining", WorkKind.Cottage => "pottery",
        WorkKind.Road => "wheel", _ => null,
    };

    public static int WorkTurns(WorkKind k) => k switch
    {
        WorkKind.Farm => 4, WorkKind.Mine => 5, WorkKind.Cottage => 4, WorkKind.Road => 2, _ => 0,
    };

    public static string WorkName(WorkKind k) => k switch
    {
        WorkKind.Farm => "Ферма", WorkKind.Mine => "Шахта", WorkKind.Cottage => "Посёлок", WorkKind.Road => "Дорога", _ => "",
    };

    /// <summary>Whether the terrain itself allows the improvement (ownership and tech are checked by the server).</summary>
    public static bool WorkFits(in TileData t, WorkKind k)
    {
        if (!t.Passable || t.CityId >= 0) return false;
        switch (k)
        {
            case WorkKind.Road: return !t.Road;
            case WorkKind.Farm:
                return !t.Forest && t.Relief == Relief.Flat && t.Improvement != Improvement.Farm
                    && t.Terrain is Terrain.Grassland or Terrain.Plains;
            case WorkKind.Mine: return !t.Forest && t.Relief == Relief.Hills && t.Improvement != Improvement.Mine;
            case WorkKind.Cottage:
                return !t.Forest && t.Relief == Relief.Flat && t.Improvement != Improvement.Cottage
                    && t.Terrain is Terrain.Grassland or Terrain.Plains or Terrain.Desert or Terrain.Tundra;
            default: return false;
        }
    }

    public static int CultureForRadius(int radius) => radius switch { <= 1 => 0, 2 => 20, 3 => 120, 4 => 500, _ => int.MaxValue };
    public static int RadiusForCulture(int culture) => culture >= 500 ? 4 : culture >= 120 ? 3 : culture >= 20 ? 2 : 1;
    public static int FoodToGrow(int pop) => 12 + 6 * pop;
    public static int BuyPrice(int cost, int prod) => Math.Max(0, cost - prod) * 3;

    public static string ItemName(string item)
    {
        if (item == Wealth) return "Богатство";
        if (item.StartsWith("u:")) { int i = UnitIndex(item[2..]); return i >= 0 ? Units[i].Name : item; }
        if (item.StartsWith("b:")) return Building(item[2..])?.Name ?? item;
        return item;
    }

    public static int ItemCost(string item)
    {
        if (item.StartsWith("u:")) { int i = UnitIndex(item[2..]); return i >= 0 ? Units[i].Cost : -1; }
        if (item.StartsWith("b:")) return Building(item[2..])?.Cost ?? -1;
        return -1;
    }

    public static readonly int[] PlayerColors =
    {
        0x3B6FE0, 0xD93A3A, 0x2FA84F, 0xE0B020, 0x9A4FD0, 0x28B5B5, 0xE07A28, 0xD05090,
        0x7A8C2A, 0x5A5AA0, 0xA06040, 0x40A080,
    };
    public const int NeutralColor = 0x8A8A8A;
}
