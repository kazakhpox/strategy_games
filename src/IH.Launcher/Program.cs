using System.Diagnostics;
using System.IO.Compression;
using System.Text;

// Infinite Horizons launcher: keeps the game in the "game" folder next to itself up to date with the
// latest GitHub release, then starts it. Works offline if a copy of the game is already installed.

const string DefaultBase = "https://github.com/kazakhpox/strategy_games/releases/latest/download/";
const string VersionFile = "version.txt";
const string Package = "InfiniteHorizons-win-x64.zip";
const string GameExe = "InfiniteHorizons.exe";

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* no console attached */ }
try { Console.Title = "Infinite Horizons"; } catch { /* output is redirected */ }
Console.WriteLine("=== Infinite Horizons — лаунчер ===\n");

string baseUrl = Environment.GetEnvironmentVariable("IH_UPDATE_URL") ?? DefaultBase;
if (!baseUrl.EndsWith('/')) baseUrl += "/";
string root = AppContext.BaseDirectory;
string gameDir = Path.Combine(root, "game");
string exePath = Path.Combine(gameDir, GameExe);
string localVersionPath = Path.Combine(gameDir, VersionFile);
string? local = File.Exists(localVersionPath) && File.Exists(exePath) ? File.ReadAllText(localVersionPath).Trim() : null;

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("InfiniteHorizonsLauncher/1.0");

string? remote = null;
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    remote = (await http.GetStringAsync(baseUrl + VersionFile, cts.Token)).Trim();
    if (remote.Length == 0 || remote.Length > 100 || remote.Contains('<')) remote = null; // not a version file
}
catch (Exception ex)
{
    Console.WriteLine($"Не удалось проверить обновления: {Short(ex)}");
}

if (remote != null && remote != local)
{
    Console.WriteLine(local == null ? $"Устанавливаю игру, версия {remote}" : $"Найдено обновление: {local} -> {remote}");
    try
    {
        await Install(remote);
        local = remote;
        Console.WriteLine("Готово.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nОбновление не удалось: {Short(ex)}");
        if (ex is IOException or UnauthorizedAccessException)
            Console.WriteLine("Возможно, игра или её сервер ещё запущены — закройте их и попробуйте снова.");
    }
}
else if (remote != null) Console.WriteLine($"Установлена последняя версия ({local}).");

if (local == null || !File.Exists(exePath))
{
    Console.WriteLine("\nИгра не установлена, а скачать её не получилось. Проверьте интернет и запустите лаунчер ещё раз.");
    Pause();
    return 1;
}

if (remote == null) Console.WriteLine($"Запускаю установленную версию ({local}) без проверки обновлений.");
Console.WriteLine("Запуск игры…");
try
{
    var psi = new ProcessStartInfo(exePath) { WorkingDirectory = gameDir, UseShellExecute = false };
    foreach (string a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi);
    if (p == null) throw new InvalidOperationException("процесс не создан");
}
catch (Exception ex)
{
    Console.WriteLine($"Не удалось запустить игру: {Short(ex)}");
    Pause();
    return 1;
}
return 0;

async Task Install(string version)
{
    string tmpZip = Path.Combine(root, "update.zip.part");
    string newDir = Path.Combine(root, "game.new");
    string oldDir = Path.Combine(root, "game.old");
    try
    {
        using (var resp = await http.GetAsync(baseUrl + Package, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmpZip);
            var buf = new byte[1 << 16];
            long done = 0;
            int n, lastShown = -1;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                int shown = total > 0 ? (int)(done * 100 / total) : (int)(done >> 20);
                if (shown == lastShown) continue;
                lastShown = shown;
                Console.Write(total > 0 ? $"\rЗагрузка: {shown}% ({done >> 20} из {total >> 20} МБ)   " : $"\rЗагрузка: {shown} МБ   ");
            }
            Console.WriteLine();
            if (total > 0 && done != total) throw new IOException("загрузка оборвалась");
        }

        Console.WriteLine("Распаковка…");
        if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
        ZipFile.ExtractToDirectory(tmpZip, newDir);
        if (!File.Exists(Path.Combine(newDir, GameExe))) throw new InvalidDataException("в архиве нет игры");
        File.WriteAllText(Path.Combine(newDir, VersionFile), version);

        // swap folders; the previous version is restored if the swap fails half-way
        if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
        bool hadOld = Directory.Exists(gameDir);
        if (hadOld) Directory.Move(gameDir, oldDir);
        try { Directory.Move(newDir, gameDir); }
        catch { if (hadOld && !Directory.Exists(gameDir)) Directory.Move(oldDir, gameDir); throw; }
        if (hadOld) try { Directory.Delete(oldDir, true); } catch { /* cleaned up next time */ }
    }
    finally
    {
        try { File.Delete(tmpZip); } catch { }
        try { if (Directory.Exists(newDir)) Directory.Delete(newDir, true); } catch { }
    }
}

static string Short(Exception ex) => ex is TaskCanceledException or OperationCanceledException ? "нет ответа от сервера" : ex.Message;

static void Pause()
{
    Console.WriteLine("\nНажмите Enter, чтобы закрыть окно.");
    try { Console.ReadLine(); } catch { }
}
