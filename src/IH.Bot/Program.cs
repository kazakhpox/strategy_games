using System.Net;
using IH.Bot;
using IH.Shared;

// Infinite Horizons network tools.
//   IH.Bot proxy  --listen 27016 --target 127.0.0.1:27015 [--delay 120] [--jitter 60] [--loss 3]
//   IH.Bot stress --host 127.0.0.1 --port 27015 [--bots 4] [--ai 2] [--turns 50] [--sequential]

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("IH.Bot proxy  --listen 27016 --target 127.0.0.1:27015 [--delay 120] [--jitter 60] [--loss 3]");
    Console.WriteLine("IH.Bot stress --host 127.0.0.1 --port 27015 [--bots 4] [--ai 2] [--turns 50] [--sequential]");
    return 0;
}

var opt = new Dictionary<string, string>();
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"Неизвестный параметр: {args[i]}"); return 2; }
    bool flag = i + 1 >= args.Length || args[i + 1].StartsWith("--");
    opt[args[i][2..]] = flag ? "1" : args[++i];
}
string Str(string k, string def) => opt.TryGetValue(k, out var v) ? v : def;
int Int(string k, int def) => opt.TryGetValue(k, out var v) ? int.Parse(v) : def;

if (args[0] == "proxy")
{
    var parts = Str("target", "127.0.0.1:27015").Split(':');
    var target = new IPEndPoint(Dns.GetHostAddresses(parts[0]).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), int.Parse(parts[1]));
    using var proxy = new LagProxy(Int("listen", 27016), target, Int("delay", 120), Int("jitter", 60), Int("loss", 3));
    Console.WriteLine($"Прокси: порт {proxy.Port} -> {target}, задержка {Int("delay", 120)}±{Int("jitter", 60)} мс в каждую сторону, потери {Int("loss", 3)}%");
    Console.WriteLine("Подключайте клиентов к порту прокси. Ctrl+C — выход.");
    while (true)
    {
        Thread.Sleep(5000);
        Console.WriteLine($"  переслано {proxy.Forwarded}, потеряно {proxy.Dropped}");
    }
}

if (args[0] == "stress")
{
    var report = StressTest.Run(Str("host", "127.0.0.1"), Int("port", 27015), Int("bots", 4), Int("ai", 2), Int("turns", 50),
        !opt.ContainsKey("sequential"), TimeSpan.FromMinutes(30), Console.WriteLine);
    return report.Completed ? 0 : 1;
}

Console.Error.WriteLine($"Неизвестный режим: {args[0]}");
return 2;

public sealed class StressReport
{
    public bool Completed;
    public int Turn, Commands, Rejected, AvgRtt, P95Rtt, MaxRtt;
    public long Bytes;
    public List<BotPlayer> Bots = new();
}

public static class StressTest
{
    /// <summary>Connects bots, has the first one host a game, and plays until the target turn.</summary>
    public static StressReport Run(string host, int port, int bots, int ai, int turns, bool simultaneous, TimeSpan timeout,
        Action<string>? log = null, Action? pump = null)
    {
        var report = new StressReport();
        var list = Enumerable.Range(0, bots).Select(i => new BotPlayer($"Бот{i + 1}", 1000 + i)).ToList();
        report.Bots = list;
        var deadline = DateTime.UtcNow + timeout;
        bool Wait(Func<bool> cond)
        {
            while (!cond())
            {
                if (DateTime.UtcNow > deadline) return false;
                pump?.Invoke();
                foreach (var b in list) b.Update();
                Thread.Sleep(2);
            }
            return true;
        }

        foreach (var b in list) b.Net.Connect(host, port);
        if (!Wait(() => list.All(b => b.Net.State == NetState.Lobby))) { log?.Invoke("Не удалось подключиться к серверу"); return report; }

        list[0].Net.Send(MsgType.CreateGame, new GameSettings
        {
            Name = "Стресс-тест", MapW = 48, MapH = 32, AiCount = ai, MaxPlayers = bots, Simultaneous = simultaneous, Seed = 4242,
        });
        if (!Wait(() => list[0].Net.Room != null)) { log?.Invoke("Не удалось создать игру"); return report; }
        int gameId = list[0].Net.Room!.Info.Id;
        foreach (var b in list.Skip(1)) b.Net.Send(MsgType.JoinGame, new IntMsg { V = gameId });
        if (!Wait(() => list[0].Net.Room!.Slots.Count(s => !s.AI) == bots)) { log?.Invoke("Боты не смогли войти в игру"); return report; }
        list[0].Net.Send(MsgType.StartGame);
        if (!Wait(() => list.All(b => b.World != null && b.World.Turn >= 1))) { log?.Invoke("Игра не началась: " + list[0].LastError); return report; }
        log?.Invoke($"Игра началась: {bots} ботов + {ai} ИИ, {(simultaneous ? "одновременные ходы" : "ходы по очереди")}");

        int lastLogged = 0;
        report.Completed = Wait(() =>
        {
            int t = list[0].World!.Turn;
            if (log != null && t >= lastLogged + 10) { lastLogged = t; log($"  ход {t}"); }
            return list.All(b => b.World!.Turn > turns);
        });

        var rtts = list.SelectMany(b => b.Rtts).OrderBy(x => x).ToList();
        report.Turn = list[0].World?.Turn ?? 0;
        report.Commands = rtts.Count;
        report.Rejected = list.Sum(b => b.Rejected);
        report.Bytes = list.Sum(b => b.Net.BytesReceived);
        if (rtts.Count > 0)
        {
            report.AvgRtt = (int)rtts.Average();
            report.P95Rtt = rtts[(int)(rtts.Count * 0.95)];
            report.MaxRtt = rtts[^1];
        }
        log?.Invoke($"{(report.Completed ? "Готово" : "ТАЙМАУТ")}: ход {report.Turn}, команд {report.Commands} (отклонено правилами {report.Rejected}), " +
                    $"отклик команд: средний {report.AvgRtt} мс, p95 {report.P95Rtt} мс, макс {report.MaxRtt} мс; " +
                    $"получено {report.Bytes / 1024} КБ на {bots} клиент(ов) ({report.Bytes / Math.Max(1, report.Turn) / bots} байт/ход на клиента)");
        return report;
    }
}
