using IH.Core;
using IH.Server;
using IH.Shared;

// Infinite Horizons dedicated server (headless).
//   IH.Server [--port 27015] [--saves DIR] [--autosave N] [--afk SEC] [--balance FILE.json] [--load SAVE.json]
//   IH.Server --sim [--turns 300] [--players 6] [--seed 1] [--no-balance]   balance simulation, no network

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* no console */ } // Cyrillic on any Windows code page
var opt = new ServerOptions();
string? load = null;
bool sim = false, noBalance = false;
int simTurns = 300, simPlayers = 6, simSeed = 1;
double simStrong = 1;

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Параметру {args[i]} нужно значение");
    switch (args[i])
    {
        case "--port": opt.Port = int.Parse(Next()); break;
        case "--saves": opt.SaveDir = Next(); break;
        case "--autosave": opt.AutosaveEvery = int.Parse(Next()); break;
        case "--afk": opt.AfkSeconds = int.Parse(Next()); break;
        case "--balance": opt.Balance = SaveGame.FromJson<BalanceConfig>(File.ReadAllText(Next())); break;
        case "--load": load = Next(); break;
        case "--sim": sim = true; break;
        case "--turns": simTurns = int.Parse(Next()); break;
        case "--players": simPlayers = int.Parse(Next()); break;
        case "--seed": simSeed = int.Parse(Next()); break;
        case "--no-balance": noBalance = true; break;
        case "--strong": simStrong = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "--dump-balance": Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new BalanceConfig(),
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true, WriteIndented = true })); return 0;
        case "-h" or "--help":
            Console.WriteLine("IH.Server [--port 27015] [--saves DIR] [--autosave N] [--afk SEC] [--balance FILE] [--load SAVE]");
            Console.WriteLine("IH.Server --sim [--turns 300] [--players 6] [--seed 1] [--no-balance] [--strong 1.5]");
            Console.WriteLine("IH.Server --dump-balance   (печатает коэффициенты баланса по умолчанию в JSON)");
            return 0;
        default: Console.Error.WriteLine($"Неизвестный параметр: {args[i]}"); return 2;
    }
}

if (sim) { Simulation.Run(simTurns, simPlayers, simSeed, noBalance, opt.Balance, true, simStrong); return 0; }

var server = new GameServer(opt);
server.Start();
if (load != null) server.LoadSession(load);
Console.WriteLine("Игроки подключаются по вашему IP в Radmin VPN и этому порту. Ctrl+C — остановка с сохранением.");

bool running = true;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };
while (running)
{
    server.Tick();
    Thread.Sleep(5);
}
server.Stop();
Console.WriteLine("Сервер остановлен, игры сохранены.");
return 0;

public static class Simulation
{
    /// <summary>Disables every leader penalty and laggard bonus - the control group for balance comparisons.</summary>
    public static BalanceConfig Off() => new()
    {
        SciencePenalty = 0, CulturePenalty = 0, UpkeepPenalty = 0, SprawlPenalty = 0, UnrestPerPressure = 0,
        CatchUpStrength = 0, EchoBase = 0, EchoOpenBorders = 0,
    };

    public static Game Run(int turns, int players, int seed, bool noBalance, BalanceConfig? balance, bool print = true, double strong = 1)
    {
        var settings = new GameSettings { Name = "sim", MapW = 64, MapH = 40, Seed = seed, Simultaneous = true };
        var list = Enumerable.Range(0, players).Select(i => ($"AI{i}", true)).ToList();
        var g = Game.Create(settings, list, noBalance ? Off() : balance);
        g.P(0).Bonus = strong; // AI0 plays with a handicap in its favour to provoke a runaway leader
        int protests = 0, secessions = 0, wars = 0, captures = 0;
        if (print) Console.WriteLine($"Симуляция: {players} ИИ, {turns} ходов, seed {seed}, анти-сноуболл {(noBalance ? "ВЫКЛ" : "ВКЛ")}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (g.S.Turn <= turns)
        {
            g.StepAiOnly();
            foreach (var e in g.Events)
                switch (e.Dto.Type)
                {
                    case EventKind.Protest: protests++; break;
                    case EventKind.Secession: secessions++; break;
                    case EventKind.WarDeclared: wars++; break;
                    case EventKind.CityCaptured: captures++; break;
                }
            g.Events.Clear();
            if (print && (g.S.Turn % 50 == 0 || g.S.Turn == turns + 1))
            {
                Console.WriteLine($"--- ход {g.S.Turn}");
                Console.WriteLine("игрок  города нас.  техи  ур.науки  наука/х  золото   ITR  давл. отст.  эпоха");
                foreach (var p in g.S.Players.Where(p => !p.Neutral))
                    Console.WriteLine($"{p.Name,-6} {(p.Alive ? g.CitiesOf(p.Id).Count() : 0),5} {g.CitiesOf(p.Id).Sum(c => c.Pop),5} {p.TechCount,5} {p.ScienceLevel,9} {p.SciRate + p.EchoBonus,8:F1} {p.Gold,7} {p.Itr,6:F2} {p.Pressure,5:F2} {p.Lag,5:F2}  {(p.Alive ? Rules.EraName(Rules.EraOf(p.Known, p.Future)) : "уничтожен")}");
            }
        }
        if (print)
        {
            var lv = g.Active.Select(p => (double)p.ScienceLevel).OrderByDescending(x => x).ToList();
            double med = AntiSnowball.Median(lv.ToList());
            Console.WriteLine($"Итог: AI0/медиана = {g.P(0).ScienceLevel / Math.Max(1, AntiSnowball.Median(g.Active.Where(p => p.Id != 0).Select(p => (double)p.ScienceLevel).ToList())):F2}; лидер/медиана по науке = {lv.Max() / Math.Max(1, med):F2}, лидер/аутсайдер = {lv.Max() / Math.Max(1, lv.Min()):F2}; " +
                              $"войн {wars}, захватов {captures}, протестов {protests}, отделений {secessions}; вольных городов {g.CitiesOf(g.Neutral.Id).Count()}; {sw.ElapsedMilliseconds} мс");
        }
        return g;
    }
}
