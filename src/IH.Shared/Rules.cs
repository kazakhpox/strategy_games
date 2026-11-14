namespace IH.Shared;

public enum Terrain : byte { Ocean, Coast, Grassland, Plains, Desert, Tundra, Snow }
public enum Relief : byte { Flat, Hills, Mountain }
public enum Resource : byte
{
    None,
    // bonus
    Bananas, Stone, Crabs, Maize, Copper, Sheep, Deer, Wheat, Rice, Fish, Cattle,
    // strategic (hidden until the revealing technology is known)
    Horses, Iron, Niter, Coal, Oil, Aluminium, Uranium,
    // luxury (each distinct improved luxury makes every city happier)
    Incense, Wine, Cocoa, Coffee, Dyes, Olives, Spices, Sugar, Tobacco, Cotton, Citrus, Tea, Silk,
    Diamonds, Gypsum, Jade, Mercury, Silver, Salt, Amber,
    Furs, Ivory, Truffles,
    Pearls, Whales, Turtles,
    Marble, Cloves, Cinnamon, Honey,
}
public enum ResClass : byte { None, Bonus, Strategic, Luxury }
/// <summary>Tile improvements. WorkKind uses the same numbers plus Road.</summary>
public enum Improvement : byte { None, Farm, Mine, Cottage, Plantation, Quarry, Pasture, Camp, FishingBoats, OilWell }
public enum WorkKind : byte { None, Farm, Mine, Cottage, Plantation, Quarry, Pasture, Camp, FishingBoats, OilWell, Road }

public sealed record ResDef(Resource Id, string Name, ResClass Class, int F, int P, int C, WorkKind Imp, string? Reveal = null);

/// <summary>What a unit is able to cross; decides movement cost and passability.</summary>
public readonly record struct Mover(bool Sea, bool Coast, bool Ocean, bool Fly = false);
public enum UnitRole : byte { Military, Settler, Worker, Scout, Missionary }

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
    /// <summary>Walkable land.</summary>
    public readonly bool Passable => !IsWater && Relief != Relief.Mountain;
    public const ulong ResourceMask = 63UL << 6;

    public readonly ulong Pack()
    {
        ulong p = (ulong)Terrain;
        p |= (ulong)Relief << 3;
        p |= (Forest ? 1UL : 0) << 5;
        p |= (ulong)Resource << 6;
        p |= (ulong)Improvement << 12;
        p |= (Road ? 1UL : 0) << 16;
        p |= (ulong)(Owner + 1) << 17;
        p |= (ulong)(CityId + 1) << 24;
        return p;
    }

    public static TileData Unpack(ulong p) => new()
    {
        Terrain = (Terrain)(p & 7),
        Relief = (Relief)((p >> 3) & 3),
        Forest = ((p >> 5) & 1) != 0,
        Resource = (Resource)((p >> 6) & 63),
        Improvement = (Improvement)((p >> 12) & 15),
        Road = ((p >> 16) & 1) != 0,
        Owner = (int)((p >> 17) & 127) - 1,
        CityId = (int)((p >> 24) & 0xFFFFFF) - 1,
    };
}

public sealed record UnitDef(string Id, string Name, int Cost, int Str, int Moves, string? Tech,
    UnitRole Role = UnitRole.Military, int Sight = 1, int CityDefPct = 0, bool Sea = false, Resource Needs = Resource.None)
{
    /// <summary>Special abilities; units of the base game have none.</summary>
    public Rules.ModUnit X { get; init; } = Rules.ModUnit.None;
}

public sealed record BuildingDef(string Id, string Name, int Cost, string? Tech)
{
    /// <summary>Special mechanics of branch buildings (bombardment, auras, branch power).</summary>
    public Rules.ModBuilding X { get; init; } = Rules.ModBuilding.None;
    public bool Coastal;
    public int Faith;
    public int Food, Commerce, ProdPct, SciPct, GoldPct, Culture, CulPct, Happy, DefPct, Upkeep, Calm, MaintCutPct;
    public string Describe()
    {
        var p = new List<string>();
        if (Food != 0) p.Add($"+{Food} пищи");
        if (Commerce != 0) p.Add($"+{Commerce} коммерции");
        if (Faith != 0) p.Add($"+{Faith} веры");
        if (Coastal) p.Add("только в прибрежном городе");
        if (ProdPct != 0) p.Add($"+{ProdPct}% производства");
        if (SciPct != 0) p.Add($"+{SciPct}% науки");
        if (GoldPct != 0) p.Add($"+{GoldPct}% золота");
        if (Culture != 0) p.Add($"+{Culture} культуры");
        if (CulPct != 0) p.Add($"+{CulPct}% культуры");
        if (Happy != 0) p.Add($"+{Happy} довольства");
        if (DefPct != 0) p.Add($"+{DefPct}% обороны");
        if (Calm != 0) p.Add($"-{Calm} брожения/ход");
        if (MaintCutPct != 0) p.Add($"-{MaintCutPct}% содержания города");
        if (X.Extra().Length > 0) p.Add(X.Extra());
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
    public const int CongressPeriod = 30;
    public static int FaithPrice(int cost, int prod) => Math.Max(0, cost - prod) * 2;
    public static int TilePrice(int distance, int ownedTiles) => 25 + 15 * distance + ownedTiles / 2;

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
    /// <summary>All unit types: the built-in ones followed by those defined by branches (mods).</summary>
    public static UnitDef[] Units { get; private set; } = Array.Empty<UnitDef>();

    private static readonly UnitDef[] BaseUnits =
    {
        new("settler", "Поселенец", 60, 0, 2, null, UnitRole.Settler),
        new("worker", "Рабочий", 35, 0, 2, null, UnitRole.Worker),
        new("scout", "Разведчик", 15, 1, 2, "hunting", UnitRole.Scout, Sight: 2),
        new("warrior", "Воин", 15, 2, 1, null),
        new("archer", "Лучник", 25, 3, 1, "archery", CityDefPct: 50),
        new("spearman", "Копейщик", 35, 4, 1, "bronze_working"),
        new("swordsman", "Мечник", 45, 6, 1, "iron_working", Needs: Resource.Iron),
        new("horseman", "Всадник", 50, 6, 2, "horseback", Needs: Resource.Horses),
        new("longbowman", "Длинный лучник", 60, 6, 1, "feudalism", CityDefPct: 50),
        new("knight", "Рыцарь", 90, 10, 2, "guilds", Needs: Resource.Horses),
        new("musketman", "Мушкетёр", 100, 9, 1, "gunpowder", CityDefPct: 25, Needs: Resource.Niter),
        new("rifleman", "Стрелок", 140, 14, 1, "rifling", CityDefPct: 25),
        new("cavalry", "Кавалерия", 150, 15, 2, "military_tradition", Needs: Resource.Horses),
        new("infantry", "Пехота", 200, 20, 1, "assembly_line", CityDefPct: 25),
        new("tank", "Танк", 260, 28, 2, "combined_arms", Needs: Resource.Oil),
        new("mech_infantry", "Мотопехота", 320, 32, 2, "robotics", CityDefPct: 25, Needs: Resource.Aluminium),
        new("nano_trooper", "Нанодесант", 420, 42, 2, "f0_1", CityDefPct: 25),
        new("missionary", "Миссионер", 80, 0, 2, null, UnitRole.Missionary),
        // ---- navy
        new("work_boat", "Рыбацкое судно", 30, 0, 3, "sailing", UnitRole.Worker, Sea: true),
        new("galley", "Галера", 45, 3, 3, "sailing", Sea: true),
        new("caravel", "Каравелла", 110, 8, 4, "astronomy", Sight: 2, Sea: true),
        new("frigate", "Фрегат", 160, 13, 4, "military_tradition", Sea: true, Needs: Resource.Niter),
        new("ironclad", "Броненосец", 220, 19, 4, "steam_power", Sea: true, Needs: Resource.Coal),
        new("destroyer", "Эсминец", 280, 26, 5, "refining", Sight: 2, Sea: true, Needs: Resource.Oil),
        new("battleship", "Линкор", 360, 36, 4, "combined_arms", Sea: true, Needs: Resource.Oil),
        new("missile_cruiser", "Ракетный крейсер", 440, 46, 5, "robotics", Sight: 2, Sea: true, Needs: Resource.Aluminium),
    };

    /// <summary>Best unit this one can be upgraded to (same mobility class, stronger, tech known); -1 if none.</summary>
    public static int UpgradeTarget(int type, Func<string?, bool> knows, Func<Resource, bool>? has = null)
    {
        var d = Units[type];
        if (d.Role != UnitRole.Military) return -1;
        int best = -1;
        for (int i = 0; i < Units.Length; i++)
        {
            var u = Units[i];
            if (u.Role == UnitRole.Military && u.Sea == d.Sea && (d.Sea || u.Moves == d.Moves) && u.Str > d.Str && knows(u.Tech) && (u.Needs == Resource.None || has == null || has(u.Needs)) && (best < 0 || u.Str > Units[best].Str))
                best = i;
        }
        return best;
    }

    public static int UpgradePrice(int from, int to) => Math.Max(10, (Units[to].Cost - Units[from].Cost) * 2);

    public static int UnitIndex(string id) => Array.FindIndex(Units, u => u.Id == id);

    // ------------------------------------------------------------ buildings
    public static BuildingDef[] Buildings { get; private set; } = Array.Empty<BuildingDef>();

    private static readonly BuildingDef[] BaseBuildings =
    {
        new("palace", "Дворец", 0, null) { Commerce = 6, Culture = 2, Happy = 1, Calm = 2, Faith = 1 },
        new("shrine", "Святилище", 40, "mysticism") { Faith = 2 },
        new("monument", "Монумент", 30, "mysticism") { Culture = 2 },
        new("granary", "Амбар", 60, "pottery") { Food = 2, Upkeep = 1 },
        new("walls", "Стены", 50, "masonry") { DefPct = 50 },
        new("library", "Библиотека", 90, "writing") { SciPct = 25, Culture = 1, Upkeep = 1 },
        new("temple", "Храм", 80, "priesthood") { Happy = 1, Culture = 1, Calm = 1, Upkeep = 1, Faith = 3 },
        new("harbor", "Гавань", 100, "sailing") { Food = 1, GoldPct = 25, Upkeep = 1, Coastal = true },
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
        new("cathedral", "Собор", 180, "philosophy") { Faith = 4, Culture = 2, Happy = 1, Upkeep = 2 },
        new("stock_exchange", "Биржа", 260, "economics") { GoldPct = 25, Upkeep = 2 },
        new("shipyard", "Верфь", 240, "steam_power") { ProdPct = 15, GoldPct = 15, Upkeep = 2, Coastal = true },
        new("stadium", "Стадион", 300, "radio") { Happy = 3, Calm = 1, Upkeep = 2 },
        new("laboratory", "Лаборатория", 300, "computers") { SciPct = 25, Upkeep = 2 },
        new("hospital", "Больница", 320, "genetics") { Food = 3, Happy = 1, Upkeep = 2 },
        new("fusion_plant", "Термоядерная станция", 420, "fusion") { ProdPct = 35, Upkeep = 3 },
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
        T("mysticism", "Мистицизм", 30, 0), T("wheel", "Колесо", 40, 0), T("sailing", "Мореходство", 40, 0),
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
        T("refining", "Переработка нефти", 1600, 4, "steam_power"),

        T("industrialism", "Индустриализация", 2200, 5, "assembly_line", "electricity"),
        T("radio", "Радио", 2000, 5, "electricity"),
        T("combined_arms", "Комбинированное оружие", 2600, 5, "industrialism", "radio", "refining"),
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
    private static ResDef B(Resource id, string name, int f, int p, int c, WorkKind imp) => new(id, name, ResClass.Bonus, f, p, c, imp);
    private static ResDef St(Resource id, string name, string reveal, WorkKind imp) => new(id, name, ResClass.Strategic, 0, 1, 0, imp, reveal);
    private static ResDef Lx(Resource id, string name, WorkKind imp) => new(id, name, ResClass.Luxury, 0, 0, 1, imp);

    /// <summary>Indexed by <see cref="Resource"/>.</summary>
    public static readonly ResDef[] Resources =
    {
        new(Resource.None, "", ResClass.None, 0, 0, 0, WorkKind.None),
        B(Resource.Bananas, "Бананы", 1, 0, 0, WorkKind.Plantation), B(Resource.Stone, "Камень", 0, 1, 0, WorkKind.Quarry),
        B(Resource.Crabs, "Крабы", 0, 0, 2, WorkKind.FishingBoats), B(Resource.Maize, "Кукуруза", 0, 0, 2, WorkKind.Farm),
        B(Resource.Copper, "Медь", 0, 0, 2, WorkKind.Mine), B(Resource.Sheep, "Овцы", 1, 0, 0, WorkKind.Pasture),
        B(Resource.Deer, "Олени", 0, 1, 0, WorkKind.Camp), B(Resource.Wheat, "Пшеница", 1, 0, 0, WorkKind.Farm),
        B(Resource.Rice, "Рис", 1, 0, 0, WorkKind.Farm), B(Resource.Fish, "Рыба", 1, 0, 0, WorkKind.FishingBoats),
        B(Resource.Cattle, "Скот", 1, 0, 0, WorkKind.Pasture),
        St(Resource.Horses, "Лошади", "animal_husbandry", WorkKind.Pasture), St(Resource.Iron, "Железо", "bronze_working", WorkKind.Mine),
        St(Resource.Niter, "Селитра", "gunpowder", WorkKind.Mine), St(Resource.Coal, "Уголь", "industrialism", WorkKind.Mine),
        St(Resource.Oil, "Нефть", "refining", WorkKind.OilWell), St(Resource.Aluminium, "Алюминий", "radio", WorkKind.Mine),
        St(Resource.Uranium, "Уран", "combined_arms", WorkKind.Mine),
        Lx(Resource.Incense, "Благовония", WorkKind.Plantation), Lx(Resource.Wine, "Вино", WorkKind.Plantation),
        Lx(Resource.Cocoa, "Какао", WorkKind.Plantation), Lx(Resource.Coffee, "Кофе", WorkKind.Plantation),
        Lx(Resource.Dyes, "Красители", WorkKind.Plantation), Lx(Resource.Olives, "Оливки", WorkKind.Plantation),
        Lx(Resource.Spices, "Пряности", WorkKind.Plantation), Lx(Resource.Sugar, "Сахар", WorkKind.Plantation),
        Lx(Resource.Tobacco, "Табак", WorkKind.Plantation), Lx(Resource.Cotton, "Хлопок", WorkKind.Plantation),
        Lx(Resource.Citrus, "Цитрусовые", WorkKind.Plantation), Lx(Resource.Tea, "Чай", WorkKind.Plantation),
        Lx(Resource.Silk, "Шёлк", WorkKind.Plantation),
        Lx(Resource.Diamonds, "Алмазы", WorkKind.Mine), Lx(Resource.Gypsum, "Гипс", WorkKind.Mine), Lx(Resource.Jade, "Нефрит", WorkKind.Mine),
        Lx(Resource.Mercury, "Ртуть", WorkKind.Mine), Lx(Resource.Silver, "Серебро", WorkKind.Mine), Lx(Resource.Salt, "Соль", WorkKind.Mine),
        Lx(Resource.Amber, "Янтарь", WorkKind.Mine),
        Lx(Resource.Furs, "Меха", WorkKind.Camp), Lx(Resource.Ivory, "Слоновая кость", WorkKind.Camp), Lx(Resource.Truffles, "Трюфели", WorkKind.Camp),
        Lx(Resource.Pearls, "Жемчуг", WorkKind.FishingBoats), Lx(Resource.Whales, "Киты", WorkKind.FishingBoats),
        Lx(Resource.Turtles, "Черепахи", WorkKind.FishingBoats),
        Lx(Resource.Marble, "Мрамор", WorkKind.Quarry), Lx(Resource.Cloves, "Гвоздика", WorkKind.Plantation),
        Lx(Resource.Cinnamon, "Корица", WorkKind.Plantation), Lx(Resource.Honey, "Мёд", WorkKind.Pasture),
    };

    public static ResDef Res(Resource r) => Resources[(int)r < Resources.Length ? (int)r : 0];

    public static bool ResourceVisible(Resource r, Func<string?, bool> knows) => Res(r).Reveal == null || knows(Res(r).Reveal);

    /// <summary>True when the tile's resource is connected: it carries the improvement that resource needs.</summary>
    public static bool ResourceWorked(in TileData t) => t.Resource != Resource.None && (int)t.Improvement == (int)Res(t.Resource).Imp;

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
        var rd = Res(t.Resource);
        f += rd.F; p += rd.P; c += rd.C;
        switch (t.Improvement)
        {
            case Improvement.Farm: f += 1; break;
            case Improvement.Mine: p += 2; break;
            case Improvement.Cottage: c += 2; break;
            case Improvement.Plantation: c += 1; break;
            case Improvement.Quarry: p += 1; break;
            case Improvement.Pasture: p += 1; break;
            case Improvement.Camp: c += 1; break;
            case Improvement.FishingBoats: f += 1; break;
            case Improvement.OilWell: p += 2; break;
        }
        if (ResourceWorked(t))
        {
            // a connected resource yields its bonus twice; luxuries are also traded
            f += rd.F; p += rd.P; c += rd.C;
            if (rd.Class == ResClass.Luxury) c += 2;
        }
        if (t.Road && !t.IsWater) c += 1;
        if (cityCenter) { f = Math.Max(f, 2); p = Math.Max(p, 2); c = Math.Max(c, 2); }
        return (f, p, c);
    }

    public const int EmbarkCost = 100; // boarding or landing ends the unit's move

    public static Mover MoverFor(UnitDef d, Func<string?, bool> knows) => new(d.Sea, knows("sailing"), knows("astronomy"), d.X.Flying);

    /// <summary>Step cost in half-points; -1 when the unit cannot enter the destination at all.</summary>
    public static int MoveCost(in TileData from, in TileData to, Mover m)
    {
        if (m.Fly) return MovePoint; // spirits and familiars cross mountains and water alike
        if (to.Relief == Relief.Mountain) return -1;
        if (m.Sea) return to.IsWater || to.CityId >= 0 ? MovePoint : -1; // ships dock in coastal cities
        if (to.IsWater)
        {
            if (to.Terrain == Terrain.Coast ? !m.Coast : !m.Ocean) return -1;
            return from.IsWater ? MovePoint : EmbarkCost;
        }
        if (from.IsWater) return EmbarkCost;
        if (from.Road && to.Road) return 1;
        return to.Relief == Relief.Hills || to.Forest ? 2 * MovePoint : MovePoint;
    }

    public static string? WorkTech(WorkKind k) => k switch
    {
        WorkKind.Farm => "agriculture", WorkKind.Mine => "mining", WorkKind.Cottage => "pottery", WorkKind.Road => "wheel",
        WorkKind.Plantation => "pottery", WorkKind.Quarry => "masonry", WorkKind.Pasture => "animal_husbandry",
        WorkKind.Camp => "hunting", WorkKind.FishingBoats => "sailing", WorkKind.OilWell => "refining", _ => null,
    };

    public static int WorkTurns(WorkKind k) => k switch { WorkKind.None => 0, WorkKind.Road => 2, WorkKind.OilWell => 6, WorkKind.Mine => 5, _ => 4 };

    public static string WorkName(WorkKind k) => k switch
    {
        WorkKind.Farm => "Ферма", WorkKind.Mine => "Рудник", WorkKind.Cottage => "Посёлок", WorkKind.Road => "Дорога",
        WorkKind.Plantation => "Плантация", WorkKind.Quarry => "Каменоломня", WorkKind.Pasture => "Пастбище",
        WorkKind.Camp => "Лагерь", WorkKind.FishingBoats => "Рыбацкие суда", WorkKind.OilWell => "Буровая вышка", _ => "",
    };

    public static readonly WorkKind[] AllWork =
    {
        WorkKind.Farm, WorkKind.Mine, WorkKind.Cottage, WorkKind.Plantation, WorkKind.Quarry, WorkKind.Pasture,
        WorkKind.Camp, WorkKind.FishingBoats, WorkKind.OilWell, WorkKind.Road,
    };

    /// <summary>
    /// Whether the terrain allows the improvement. Pass the tile as the player sees it (hidden strategic
    /// resources removed); ownership, technology and unit type are checked by the server.
    /// </summary>
    public static bool WorkFits(in TileData t, WorkKind k)
    {
        if (t.CityId >= 0 || t.Relief == Relief.Mountain || k == WorkKind.None) return false;
        if (k == WorkKind.Road) return !t.IsWater && !t.Road;
        if ((int)t.Improvement == (int)k) return false;
        bool forResource = t.Resource != Resource.None && Res(t.Resource).Imp == k;
        if (t.IsWater) return forResource;
        if (forResource) return true;
        // generic improvements never replace the one a resource needs
        if (ResourceWorked(t)) return false;
        switch (k)
        {
            case WorkKind.Farm: return !t.Forest && t.Relief == Relief.Flat && t.Terrain is Terrain.Grassland or Terrain.Plains;
            case WorkKind.Mine: return !t.Forest && t.Relief == Relief.Hills;
            case WorkKind.Cottage:
                return !t.Forest && t.Relief == Relief.Flat && t.Terrain is Terrain.Grassland or Terrain.Plains or Terrain.Desert or Terrain.Tundra;
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

    // --------------------------------------------------------------- civics
    /// <summary>Additive empire-wide bonuses; civics and religious beliefs are both expressed with it.</summary>
    public sealed class Bonus : IWire
    {
        public int SciPct, GoldPct, ProdPct, CulPct, FaithPct, StrPct, UpkeepCutPct, Happy, Calm, Food, Culture, Favor;
        public void Sync(Wire w)
        {
            w.I(ref SciPct); w.I(ref GoldPct); w.I(ref ProdPct); w.I(ref CulPct); w.I(ref FaithPct); w.I(ref StrPct);
            w.I(ref UpkeepCutPct); w.I(ref Happy); w.I(ref Calm); w.I(ref Food); w.I(ref Culture); w.I(ref Favor);
        }
        public void Add(Bonus o)
        {
            SciPct += o.SciPct; GoldPct += o.GoldPct; ProdPct += o.ProdPct; CulPct += o.CulPct; FaithPct += o.FaithPct; StrPct += o.StrPct;
            UpkeepCutPct += o.UpkeepCutPct; Happy += o.Happy; Calm += o.Calm; Food += o.Food; Culture += o.Culture; Favor += o.Favor;
        }
        public string Describe()
        {
            var p = new List<string>();
            if (SciPct != 0) p.Add($"+{SciPct}% науки");
            if (GoldPct != 0) p.Add($"+{GoldPct}% золота");
            if (ProdPct != 0) p.Add($"+{ProdPct}% производства");
            if (CulPct != 0) p.Add($"+{CulPct}% культуры");
            if (FaithPct != 0) p.Add($"+{FaithPct}% веры");
            if (StrPct != 0) p.Add($"+{StrPct}% силы юнитов");
            if (UpkeepCutPct != 0) p.Add($"-{UpkeepCutPct}% содержания");
            if (Happy != 0) p.Add($"+{Happy} довольства в городах");
            if (Calm != 0) p.Add($"-{Calm} брожения за ход");
            if (Food != 0) p.Add($"+{Food} пищи в городах");
            if (Culture != 0) p.Add($"+{Culture} культуры в городах");
            if (Favor != 0) p.Add($"+{Favor} одобрения за ход");
            return string.Join(", ", p);
        }
    }

    public sealed record CivicDef(string Id, string Name, int Cost, int Era, string[] Prereqs, Bonus Effect, int Level = 0);

    private static CivicDef C(string id, string name, int cost, int era, Bonus effect, params string[] pre) => new(id, name, cost, era, pre, effect);

    /// <summary>The social institutions tree, advanced by culture.</summary>
    public static readonly CivicDef[] Civics =
    {
        C("c_tribal", "Племенной строй", 25, 0, new Bonus { Happy = 1 }),
        C("c_craft", "Ремесло", 40, 0, new Bonus { ProdPct = 10 }, "c_tribal"),
        C("c_trade", "Внешняя торговля", 40, 0, new Bonus { GoldPct = 10 }, "c_tribal"),
        C("c_rites", "Обряды предков", 50, 0, new Bonus { FaithPct = 25 }, "c_tribal"),
        C("c_state", "Государственная служба", 70, 0, new Bonus { UpkeepCutPct = 15 }, "c_craft", "c_trade"),
        C("c_military", "Воинская традиция", 70, 0, new Bonus { StrPct = 10 }, "c_craft"),

        C("c_philosophy", "Политическая философия", 110, 1, new Bonus { SciPct = 10 }, "c_state", "c_rites"),
        C("c_drama", "Драма и поэзия", 110, 1, new Bonus { CulPct = 20 }, "c_rites"),
        C("c_games", "Игры и отдых", 130, 1, new Bonus { Happy = 2 }, "c_state"),
        C("c_theology", "Теология", 150, 1, new Bonus { FaithPct = 25, Calm = 1 }, "c_drama"),

        C("c_feudal", "Феодальный уклад", 280, 2, new Bonus { Food = 2 }, "c_state", "c_military"),
        C("c_guilds", "Цеховое право", 300, 2, new Bonus { GoldPct = 15 }, "c_games", "c_philosophy"),
        C("c_divine", "Божественное право", 320, 2, new Bonus { Happy = 2, CulPct = 10 }, "c_theology"),
        C("c_merc", "Наёмные армии", 300, 2, new Bonus { UpkeepCutPct = 10, StrPct = 5 }, "c_feudal"),

        C("c_humanism", "Гуманизм", 550, 3, new Bonus { SciPct = 15 }, "c_guilds", "c_drama"),
        C("c_diplomacy", "Дипломатическая служба", 550, 3, new Bonus { Favor = 3 }, "c_guilds"),
        C("c_reform", "Реформация", 600, 3, new Bonus { FaithPct = 50 }, "c_divine"),

        C("c_national", "Национализм", 1100, 4, new Bonus { StrPct = 15, Calm = 2 }, "c_humanism", "c_merc"),
        C("c_capital", "Капитализм", 1200, 4, new Bonus { GoldPct = 20 }, "c_diplomacy"),
        C("c_urban", "Урбанизация", 1200, 4, new Bonus { ProdPct = 15 }, "c_humanism"),

        C("c_ideology", "Идеология", 2000, 5, new Bonus { SciPct = 10, ProdPct = 10 }, "c_national", "c_capital"),
        C("c_welfare", "Социальные гарантии", 2200, 5, new Bonus { Happy = 3, Calm = 3 }, "c_urban", "c_reform"),
        C("c_global", "Глобализация", 3000, 6, new Bonus { SciPct = 20, Favor = 5 }, "c_ideology", "c_welfare"),
    };

    private static readonly Dictionary<string, CivicDef> CivicById = Civics.ToDictionary(c => c.Id);
    private static readonly Bonus FutureCivicStep = new() { CulPct = 3, GoldPct = 2, Calm = 1 };

    /// <summary>After the last institution the tree continues forever with "Общественное развитие N" (cf_N).</summary>
    public static CivicDef? Civic(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (CivicById.TryGetValue(id, out var c)) return c;
        if (!id.StartsWith("cf_") || !int.TryParse(id.AsSpan(3), out int n) || n < 1 || n > 100000) return null;
        return new CivicDef(id, $"Общественное развитие {n}", (int)(4000 * Math.Pow(1 + 0.5 * (n - 1), 1.5)), 7 + (n - 1) / 3,
            n == 1 ? new[] { "c_global" } : new[] { $"cf_{n - 1}" }, FutureCivicStep, n);
    }

    public static bool HasCivic(ICollection<string> known, int level, string id)
        => known.Contains(id) || (Civic(id) is { Level: > 0 } c && level >= c.Level);

    public static List<CivicDef> AvailableCivics(ICollection<string> known, int level)
    {
        var res = new List<CivicDef>();
        foreach (var c in Civics)
            if (!known.Contains(c.Id) && c.Prereqs.All(known.Contains)) res.Add(c);
        var next = Civic($"cf_{level + 1}")!;
        if (next.Prereqs.All(p => HasCivic(known, level, p))) res.Add(next);
        return res;
    }

    public static Bonus CivicBonus(IEnumerable<string> known, int level)
    {
        var b = new Bonus();
        foreach (string id in known) if (CivicById.TryGetValue(id, out var c)) b.Add(c.Effect);
        for (int i = 0; i < level; i++) b.Add(FutureCivicStep);
        return b;
    }

    // ------------------------------------------------- custom branches (mods)
    /// <summary>One step of a custom development branch.</summary>
    public sealed class NodeDef : IWire
    {
        public string Id = "", Name = "", Description = "";
        public int Cost;
        /// <summary>Ids that must be known first: technologies, social institutions or nodes of any branch.</summary>
        public string[] Requires = Array.Empty<string>();
        public Bonus Effect = new();
        /// <summary>Branch power (mana, spirits...) this node yields every turn.</summary>
        public int Power;
        public void Sync(Wire w) { w.S(ref Id); w.S(ref Name); w.S(ref Description); w.I(ref Cost); w.SA(ref Requires); Effect.Sync(w); w.I(ref Power); }
    }

    public sealed class ModUnit : IWire
    {
        public static readonly ModUnit None = new();

        public string Id = "", Name = "", Requires = "";
        public int Cost, Str, Moves = 1, CityDefPct, Sight = 1;
        public bool Sea;
        // ---- abilities
        /// <summary>Attacks from this many tiles away with RangedStr and takes no damage in return.</summary>
        public int Ranged, RangedStr;
        /// <summary>Heals friendly units on its own and adjacent tiles by this much every turn.</summary>
        public int HealAura;
        /// <summary>Heals itself every turn, even after moving or fighting.</summary>
        public int Regen;
        /// <summary>Enemy units next to it fight this many percent weaker.</summary>
        public int FearPct;
        /// <summary>Strength bonus while standing in a forest.</summary>
        public int ForestPct;
        /// <summary>Hit points regained after winning a melee fight.</summary>
        public int Lifesteal;
        /// <summary>Enemies see it only from an adjacent tile.</summary>
        public bool Stealth;
        /// <summary>Moves over any terrain, mountains and water included, at one move per tile.</summary>
        public bool Flying;
        /// <summary>An artificial being: no upkeep, never heals by resting, immune to fear.</summary>
        public bool Construct;
        /// <summary>Cannot be built in cities; appears only through a Summon action.</summary>
        public bool SummonOnly;

        public void Sync(Wire w)
        {
            w.S(ref Id); w.S(ref Name); w.S(ref Requires); w.I(ref Cost); w.I(ref Str); w.I(ref Moves); w.I(ref CityDefPct); w.I(ref Sight); w.B(ref Sea);
            w.I(ref Ranged); w.I(ref RangedStr); w.I(ref HealAura); w.I(ref Regen); w.I(ref FearPct); w.I(ref ForestPct); w.I(ref Lifesteal);
            w.B(ref Stealth); w.B(ref Flying); w.B(ref Construct); w.B(ref SummonOnly);
        }

        public string Abilities()
        {
            var p = new List<string>();
            if (Ranged > 0) p.Add($"дальний бой: сила {RangedStr}, дальность {Ranged}, без ответного урона");
            if (HealAura > 0) p.Add($"лечит союзников рядом на {HealAura} за ход");
            if (Regen > 0) p.Add($"регенерация {Regen} за ход");
            if (FearPct > 0) p.Add($"страх: враги рядом слабее на {FearPct}%");
            if (ForestPct > 0) p.Add($"+{ForestPct}% силы в лесу");
            if (Lifesteal > 0) p.Add($"восстанавливает {Lifesteal} HP после победы");
            if (Stealth) p.Add("скрытность: виден врагу только вплотную");
            if (Flying) p.Add("полёт: любые клетки, горы и вода");
            if (Construct) p.Add("конструкт: без содержания, не лечится отдыхом, не знает страха");
            if (SummonOnly) p.Add("только призывается");
            return string.Join("; ", p);
        }
    }

    public sealed class ModBuilding : IWire
    {
        public static readonly ModBuilding None = new();

        public string Id = "", Name = "", Requires = "";
        public int Cost, Food, Commerce, ProdPct, SciPct, GoldPct, Culture, CulPct, Happy, DefPct, Upkeep, Calm, Faith;
        // ---- mechanics
        /// <summary>Branch power produced every turn.</summary>
        public int Power;
        /// <summary>Extra healing per turn for own units in the city and next to it.</summary>
        public int UnitHeal;
        /// <summary>Every turn deals this damage to one enemy unit within BombardRange of the city.</summary>
        public int Bombard, BombardRange = 2;
        /// <summary>Own units within AuraRange of the city defend this many percent better.</summary>
        public int AuraDefPct, AuraRange = 2;

        public void Sync(Wire w)
        {
            w.S(ref Id); w.S(ref Name); w.S(ref Requires); w.I(ref Cost); w.I(ref Food); w.I(ref Commerce); w.I(ref ProdPct); w.I(ref SciPct);
            w.I(ref GoldPct); w.I(ref Culture); w.I(ref CulPct); w.I(ref Happy); w.I(ref DefPct); w.I(ref Upkeep); w.I(ref Calm); w.I(ref Faith);
            w.I(ref Power); w.I(ref UnitHeal); w.I(ref Bombard); w.I(ref BombardRange); w.I(ref AuraDefPct); w.I(ref AuraRange);
        }

        public string Extra()
        {
            var p = new List<string>();
            if (Power > 0) p.Add($"+{Power} силы ветки за ход");
            if (UnitHeal > 0) p.Add($"лечит ваших юнитов в городе и рядом на {UnitHeal} за ход");
            if (Bombard > 0) p.Add($"обстрел: {Bombard} урона врагу в {BombardRange} клетках от города каждый ход");
            if (AuraDefPct > 0) p.Add($"+{AuraDefPct}% обороны вашим юнитам в {AuraRange} клетках от города");
            return string.Join(", ", p);
        }
    }

    /// <summary>
    /// A special action: a rite or a spell. It is either cast by the empire as a whole or by a caster unit
    /// (then it is aimed from where that unit stands and spends the unit's move).
    /// </summary>
    public sealed class ActionDef : IWire
    {
        public string Id = "", Name = "", Description = "", Requires = "";
        /// <summary>One of <see cref="ActionKinds"/>.</summary>
        public string Kind = "";
        public int Amount, CostFaith, CostGold, Cooldown = 8;
        /// <summary>Cost in the branch's own power (mana, spirits...).</summary>
        public int CostPower;
        /// <summary>Id of the unit type that performs the action; empty = the empire itself.</summary>
        public string Caster = "";
        /// <summary>"None", "Tile" or "City" (one of the player's own cities).</summary>
        public string Target = "None";
        /// <summary>How far from the caster (or, for empire actions on a tile, from any own city) the target may be.</summary>
        public int Range = 1;
        /// <summary>Damage: tiles around the target that take half damage.</summary>
        public int Radius;
        /// <summary>Turns a summoned unit, a shield or a blessing lasts; 0 = a permanent summon.</summary>
        public int Duration;
        /// <summary>Summon: id of the unit to create.</summary>
        public string Unit = "";
        /// <summary>Bless: Food, Prod, Sci, Gold or Culture.</summary>
        public string Stat = "";

        public void Sync(Wire w)
        {
            w.S(ref Id); w.S(ref Name); w.S(ref Description); w.S(ref Requires); w.S(ref Kind); w.I(ref Amount); w.I(ref CostFaith); w.I(ref CostGold); w.I(ref Cooldown);
            w.I(ref CostPower); w.S(ref Caster); w.S(ref Target); w.I(ref Range); w.I(ref Radius); w.I(ref Duration); w.S(ref Unit); w.S(ref Stat);
        }

        private static string StatName(string stat) => stat switch
        {
            "Food" => "пищи", "Prod" => "производства", "Sci" => "науки", "Gold" => "золота", "Culture" => "культуры", _ => stat,
        };

        public string Effect() => Kind switch
        {
            "Heal" => $"лечит всех ваших юнитов на {Amount} HP",
            "Harvest" => $"+{Amount} пищи в каждом городе",
            "Calm" => $"снимает {Amount} брожения во всех городах и прекращает беспорядки",
            "Gold" => $"+{Amount} золота",
            "Science" => $"+{Amount} к текущему исследованию",
            "Culture" => $"+{Amount} к текущему институту",
            "Reveal" => $"открывает карту в радиусе {Amount} клеток от столицы",
            "Smite" => $"наносит {Amount} урона вражеским юнитам на вашей территории",
            "HealArea" => $"лечит ваших юнитов в {Range} клетках от заклинателя на {Amount} HP",
            "Damage" => $"{Amount} урона врагам на выбранной клетке" + (Radius > 0 ? $" и {Amount / 2} — на соседних" : "") + $" (дальность {Range})",
            "Summon" => $"призывает юнита «{Units.FirstOrDefault(u => u.Id == Unit)?.Name ?? Unit}»" + (Duration > 0 ? $" на {Duration} х." : ""),
            "Bless" => $"+{Amount}% {StatName(Stat)} в выбранном городе на {Duration} х.",
            "Forest" => $"выращивает лес на выбранной клетке (дальность {Range})",
            "Shield" => $"+{Amount}% обороны вашим юнитам на выбранной клетке на {Duration} х. (дальность {Range})",
            "Teleport" => "переносит заклинателя и юнитов с его клетки в выбранный ваш город",
            _ => Kind,
        };

        public string Price(string powerName = "силы") => string.Join(" + ", new[]
        {
            CostPower > 0 ? $"{CostPower} {powerName}" : "", CostFaith > 0 ? $"{CostFaith} веры" : "", CostGold > 0 ? $"{CostGold} золота" : "",
        }.Where(x => x.Length > 0));
    }

    public static readonly string[] ActionKinds =
    {
        "Heal", "Harvest", "Calm", "Gold", "Science", "Culture", "Reveal", "Smite",
        "HealArea", "Damage", "Summon", "Bless", "Forest", "Shield", "Teleport",
    };
    public static readonly string[] BlessStats = { "Food", "Prod", "Sci", "Gold", "Culture" };
    public const int PowerCap = 60;

    /// <summary>
    /// A development branch defined by data (a JSON file in the host's mods folder), e.g. Magic or Shamanism.
    /// It advances in parallel with science and institutions, fed by a mix of the empire's yields.
    /// </summary>
    public sealed class BranchDef : IWire
    {
        public string Id = "", Name = "", Description = "";
        /// <summary>Points per turn = FromScience x science + FromCulture x culture + FromFaith x faith.</summary>
        public float FromScience, FromCulture, FromFaith;
        /// <summary>Name of the branch's own spendable resource, e.g. "Мана".</summary>
        public string PowerName = "Сила";
        /// <summary>Format generation of the file; older copies of a built-in branch are ignored in favour of the built-in.</summary>
        public int Version;
        public bool Enabled = true;
        public NodeDef[] Nodes = Array.Empty<NodeDef>();
        /// <summary>Content unlocked by the branch: each entry names in Requires the node (or tech/institution) that opens it.</summary>
        public ModUnit[] Units = Array.Empty<ModUnit>();
        public ModBuilding[] Buildings = Array.Empty<ModBuilding>();
        public ActionDef[] Actions = Array.Empty<ActionDef>();
        public void Sync(Wire w)
        {
            w.S(ref Id); w.S(ref Name); w.S(ref Description); w.F(ref FromScience); w.F(ref FromCulture); w.F(ref FromFaith); w.A(ref Nodes);
            w.A(ref Units); w.A(ref Buildings); w.A(ref Actions); w.S(ref PowerName); w.I(ref Version);
        }
        public string Fuel()
        {
            var p = new List<string>();
            if (FromScience > 0) p.Add($"{FromScience * 100:0}% науки");
            if (FromCulture > 0) p.Add($"{FromCulture * 100:0}% культуры");
            if (FromFaith > 0) p.Add($"{FromFaith * 100:0}% веры");
            return p.Count == 0 ? "ничего" : string.Join(" + ", p);
        }
    }

    public const int BuiltInVersion = 2;

    private static NodeDef N(string id, string name, int cost, Bonus effect, int power, params string[] requires)
        => new() { Id = id, Name = name, Cost = cost, Effect = effect, Power = power, Requires = requires };

    /// <summary>The two branches that ship with the game; they double as examples for writing your own.</summary>
    public static List<BranchDef> DefaultBranches() => new()
    {
        // Shamanism: nature, spirits and support. Shamans heal, grow forests and call spirits; werebeasts thrive in woods.
        new BranchDef
        {
            Id = "shamanism", Name = "Шаманство", FromFaith = 1f, FromCulture = 0.5f, PowerName = "Духи", Version = BuiltInVersion,
            Description = "Природа, духи и поддержка. Шаманы лечат войска, выращивают леса и призывают духов; оборотни сильны в лесу.",
            Nodes = new[]
            {
                N("sh_spirits", "Духи предков", 30, new Bonus { Happy = 1 }, 1),
                N("sh_herbs", "Травничество", 50, new Bonus { Food = 1 }, 0, "sh_spirits", "agriculture"),
                N("sh_totems", "Тотемы", 60, new Bonus { CulPct = 10 }, 0, "sh_spirits", "c_tribal"),
                N("sh_trance", "Транс и видения", 110, new Bonus { FaithPct = 25 }, 1, "sh_herbs", "sh_totems"),
                N("sh_beast", "Звериный облик", 160, new Bonus { StrPct = 5 }, 0, "sh_trance", "hunting"),
                N("sh_weather", "Заклинание погоды", 260, new Bonus { Food = 1, ProdPct = 5 }, 1, "sh_trance", "c_rites"),
                N("sh_council", "Совет предков", 420, new Bonus { Calm = 2, Favor = 1 }, 2, "sh_weather", "sh_beast"),
            },
            Units = new[]
            {
                new ModUnit { Id = "shaman", Name = "Шаман", Requires = "sh_spirits", Cost = 35, Str = 2, Moves = 2, HealAura = 10 },
                new ModUnit { Id = "spirit", Name = "Дух-страж", Requires = "sh_trance", Cost = 40, Str = 4, Moves = 2, Flying = true, FearPct = 20, SummonOnly = true },
                new ModUnit { Id = "werebeast", Name = "Оборотень", Requires = "sh_beast", Cost = 50, Str = 5, Moves = 2, ForestPct = 50, Regen = 10, Lifesteal = 15 },
            },
            Buildings = new[]
            {
                new ModBuilding { Id = "herbalist", Name = "Хижина травника", Requires = "sh_herbs", Cost = 50, Food = 1, Happy = 1, UnitHeal = 20 },
                new ModBuilding { Id = "totem_pole", Name = "Тотемный столб", Requires = "sh_totems", Cost = 35, Culture = 1, Faith = 1, Power = 1, AuraDefPct = 25, AuraRange = 2 },
                new ModBuilding { Id = "stone_circle", Name = "Круг камней", Requires = "sh_trance", Cost = 90, Power = 2, Calm = 1 },
            },
            Actions = new[]
            {
                new ActionDef { Id = "sh_soothe", Name = "Умиротворение духов", Requires = "sh_spirits", Kind = "Calm", Amount = 30, CostPower = 4, Cooldown = 8 },
                new ActionDef { Id = "sh_heal", Name = "Исцеляющий обряд", Requires = "sh_herbs", Caster = "shaman", Kind = "HealArea", Amount = 40, Range = 2, CostPower = 3, Cooldown = 3 },
                new ActionDef { Id = "sh_grow", Name = "Рост леса", Requires = "sh_totems", Caster = "shaman", Target = "Tile", Kind = "Forest", Range = 2, Amount = 1, CostPower = 4, Cooldown = 2 },
                new ActionDef { Id = "sh_summon", Name = "Призыв духа", Requires = "sh_trance", Caster = "shaman", Target = "Tile", Kind = "Summon", Unit = "spirit", Range = 1, Amount = 1, Duration = 8, CostPower = 6, Cooldown = 5 },
                new ActionDef { Id = "sh_rain", Name = "Призыв дождя", Requires = "sh_weather", Target = "City", Kind = "Bless", Stat = "Food", Amount = 50, Duration = 6, CostPower = 6, Cooldown = 10 },
            },
        },
        // Magic: ranged damage, constructs and economy. Battle mages bombard from afar; towers shell besiegers.
        new BranchDef
        {
            Id = "magic", Name = "Магия", FromScience = 0.4f, FromFaith = 0.6f, PowerName = "Мана", Version = BuiltInVersion,
            Description = "Дальний урон, конструкты и экономика. Боевые маги бьют издалека, башни обстреливают осаждающих, големы не требуют содержания.",
            Nodes = new[]
            {
                N("mg_runes", "Руны", 40, new Bonus { SciPct = 5 }, 1, "writing"),
                N("mg_alchemy", "Алхимия", 80, new Bonus { GoldPct = 10 }, 0, "mg_runes", "sh_herbs"),
                N("mg_elements", "Стихийная магия", 120, new Bonus { ProdPct = 5 }, 1, "mg_runes"),
                N("mg_wards", "Обереги", 150, new Bonus { UpkeepCutPct = 10 }, 0, "mg_runes", "masonry"),
                N("mg_divination", "Прорицание", 220, new Bonus { SciPct = 10, Favor = 1 }, 1, "mg_alchemy", "sh_trance"),
                N("mg_enchant", "Зачарование оружия", 300, new Bonus { StrPct = 15 }, 0, "mg_elements", "iron_working"),
                N("mg_academy", "Академия магов", 520, new Bonus { SciPct = 15, CulPct = 10 }, 2, "mg_divination", "mg_enchant", "education"),
                N("mg_arcane", "Высшая магия", 900, new Bonus { SciPct = 10, GoldPct = 10, ProdPct = 10, Happy = 1 }, 3, "mg_academy", "c_humanism"),
            },
            Units = new[]
            {
                new ModUnit { Id = "familiar", Name = "Фамильяр", Requires = "mg_runes", Cost = 30, Str = 1, Moves = 2, Sight = 2, Stealth = true, Flying = true },
                new ModUnit { Id = "battle_mage", Name = "Боевой маг", Requires = "mg_elements", Cost = 60, Str = 3, Moves = 1, Ranged = 2, RangedStr = 7 },
                new ModUnit { Id = "golem", Name = "Голем", Requires = "mg_wards", Cost = 55, Str = 6, Moves = 1, CityDefPct = 50, Construct = true },
            },
            Buildings = new[]
            {
                new ModBuilding { Id = "mage_tower", Name = "Башня мага", Requires = "mg_runes", Cost = 80, SciPct = 10, Power = 2, Bombard = 15, BombardRange = 2, Upkeep = 1 },
                new ModBuilding { Id = "alchemy_lab", Name = "Алхимическая лаборатория", Requires = "mg_alchemy", Cost = 90, GoldPct = 15, Power = 1, Upkeep = 1 },
            },
            Actions = new[]
            {
                new ActionDef { Id = "mg_transmute", Name = "Трансмутация", Requires = "mg_alchemy", Kind = "Gold", Amount = 60, CostPower = 5, Cooldown = 8 },
                new ActionDef { Id = "mg_fireball", Name = "Огненный шар", Requires = "mg_elements", Caster = "battle_mage", Target = "Tile", Kind = "Damage", Amount = 35, Range = 3, Radius = 1, CostPower = 5, Cooldown = 2 },
                new ActionDef { Id = "mg_shield", Name = "Магический щит", Requires = "mg_wards", Caster = "battle_mage", Target = "Tile", Kind = "Shield", Amount = 50, Range = 2, Duration = 3, CostPower = 3, Cooldown = 2 },
                new ActionDef { Id = "mg_teleport", Name = "Телепорт", Requires = "mg_divination", Caster = "battle_mage", Target = "City", Kind = "Teleport", Amount = 1, CostPower = 8, Cooldown = 6 },
                new ActionDef { Id = "mg_insight", Name = "Прозрение", Requires = "mg_divination", Kind = "Reveal", Amount = 8, CostPower = 6, Cooldown = 12 },
            },
        },
    };

    /// <summary>Branches in effect. The server sets them from its mods folder; clients receive them when a game starts.</summary>
    public static IReadOnlyList<BranchDef> Branches { get; private set; } = Array.Empty<BranchDef>();
    public static IReadOnlyList<ActionDef> Actions { get; private set; } = Array.Empty<ActionDef>();
    private static Dictionary<string, NodeDef> _nodes = new();

    static Rules() => SetBranches(DefaultBranches());

    public static void SetBranches(IEnumerable<BranchDef> branches)
    {
        var list = branches.Where(b => b.Enabled).ToList();
        _nodes = list.SelectMany(b => b.Nodes).ToDictionary(n => n.Id);
        Branches = list;
        Actions = list.SelectMany(b => b.Actions).ToList();
        // built-in types keep their indices; branch content is appended
        Units = BaseUnits.Concat(list.SelectMany(b => b.Units).Select(u =>
            new UnitDef(u.Id, u.Name, u.Cost, u.Str, Math.Max(1, u.Moves), u.Requires.Length > 0 ? u.Requires : null,
                UnitRole.Military, Math.Max(1, u.Sight), u.CityDefPct, u.Sea) { X = u })).ToArray();
        Buildings = BaseBuildings.Concat(list.SelectMany(b => b.Buildings).Select(m =>
            new BuildingDef(m.Id, m.Name, m.Cost, m.Requires.Length > 0 ? m.Requires : null)
            {
                Food = m.Food, Commerce = m.Commerce, ProdPct = m.ProdPct, SciPct = m.SciPct, GoldPct = m.GoldPct, Culture = m.Culture,
                CulPct = m.CulPct, Happy = m.Happy, DefPct = m.DefPct, Upkeep = m.Upkeep, Calm = m.Calm, Faith = m.Faith, X = m,
            })).ToArray();
    }

    public static ActionDef? Action(string? id) => Actions.FirstOrDefault(a => a.Id == id);
    public static BranchDef? BranchOfAction(string actionId) => Branches.FirstOrDefault(b => b.Actions.Any(a => a.Id == actionId));
    public static BranchDef? BranchOfBuilding(string buildingId) => Branches.FirstOrDefault(b => b.Buildings.Any(x => x.Id == buildingId));
    public static bool IsBaseUnitId(string id) => BaseUnits.Any(u => u.Id == id);
    public static bool IsBaseContentId(string id) => BaseUnits.Any(u => u.Id == id) || BaseBuildings.Any(b => b.Id == id);

    /// <summary>What a requirement id opens besides its own bonus: units, buildings and actions.</summary>
    public static string OpenedBy(string id)
    {
        var parts = new List<string>();
        parts.AddRange(Units.Where(u => u.Tech == id).Select(u => "юнит " + u.Name));
        parts.AddRange(Buildings.Where(b => b.Tech == id).Select(b => "здание " + b.Name));
        parts.AddRange(Actions.Where(a => a.Requires == id).Select(a => "действие «" + a.Name + "»"));
        return string.Join(", ", parts);
    }

    public static NodeDef? Node(string? id) => id != null && _nodes.TryGetValue(id, out var n) ? n : null;
    public static BranchDef? BranchOf(string nodeId) => Branches.FirstOrDefault(b => b.Nodes.Any(n => n.Id == nodeId));

    /// <summary>Human-readable name of anything a node may require, with the tree it comes from.</summary>
    public static string RequirementName(string id)
    {
        if (Node(id) is { } n) return $"{n.Name} ({BranchOf(id)?.Name})";
        if (Civic(id) is { } c) return $"{c.Name} (институт)";
        if (Tech(id) is { } t) return $"{t.Name} (наука)";
        return id;
    }

    /// <summary>Whether a requirement of any kind is met.</summary>
    public static bool Satisfied(string id, ICollection<string> techs, int[] future, ICollection<string> civics, int civicLevel, ICollection<string> nodes)
    {
        if (string.IsNullOrEmpty(id)) return true;
        if (_nodes.ContainsKey(id)) return nodes.Contains(id);
        if (Civic(id) != null) return HasCivic(civics, civicLevel, id);
        return HasTech(techs, future, id);
    }

    public static Bonus NodeBonus(IEnumerable<string> known)
    {
        var b = new Bonus();
        foreach (string id in known) if (_nodes.TryGetValue(id, out var n)) b.Add(n.Effect);
        return b;
    }

    // ------------------------------------------------------------- religion
    public sealed record BeliefDef(string Name, Bonus Effect);

    /// <summary>A religion has one belief; it applies to every city that follows the religion, whoever owns it.</summary>
    public static readonly BeliefDef[] Beliefs =
    {
        new("Священные рощи", new Bonus { Culture = 2 }),
        new("Трудолюбие", new Bonus { ProdPct = 10 }),
        new("Мудрость писаний", new Bonus { SciPct = 10 }),
        new("Благословение урожая", new Bonus { Food = 2 }),
        new("Умиротворение", new Bonus { Happy = 2, Calm = 2 }),
        new("Десятина", new Bonus { GoldPct = 15 }),
    };

    public static readonly string[] ReligionNames =
        { "Путь Солнца", "Культ Предков", "Учение Звёзд", "Завет Гор", "Круг Жизни", "Орден Моря", "Небесный Путь", "Вера Пламени" };

    public const int MissionaryFaith = 80, PreachPressure = 60, ConvertThreshold = 25, ReligionRange = 6;
    public static int ReligionPrice(int alreadyFounded) => 100 + 60 * alreadyFounded;

    public static readonly int[] PlayerColors =
    {
        0x3B6FE0, 0xD93A3A, 0x2FA84F, 0xE0B020, 0x9A4FD0, 0x28B5B5, 0xE07A28, 0xD05090,
        0x7A8C2A, 0x5A5AA0, 0xA06040, 0x40A080,
    };
    public const int NeutralColor = 0x8A8A8A;
}
