using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Velocity.App;

public sealed record UpdateInfo(
    string Version,
    string DownloadUrl,
    long SizeBytes,
    string Notes,
    string PageUrl);

/// <summary>
/// Проверка и загрузка обновлений через страницу релизов на GitHub.
///
/// Загрузка возобновляемая: если она оборвалась на середине, следующая попытка
/// докачивает остаток, а не начинает файл заново. Установщик весит полсотни
/// мегабайт, и на нестабильном интернете это принципиально.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Updater
{
    private const string ReleasesApi =
        "https://api.github.com/repos/HumsteRD/Velocity/releases/latest";

    private static string CacheFolder => Path.Combine(AppSettings.Folder, "updates");

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "0.0.0";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub отвергает запросы без User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Velocity/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Возвращает сведения об обновлении или null, если установлена актуальная версия.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        try
        {
            using var client = CreateClient();
            var json = await client.GetStringAsync(ReleasesApi, cancel);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v');
            if (tag is null || !IsNewer(tag, CurrentVersion)) return null;

            // Берём первый установщик среди файлов релиза.
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                return new UpdateInfo(
                    tag,
                    asset.GetProperty("browser_download_url").GetString() ?? "",
                    asset.GetProperty("size").GetInt64(),
                    root.GetProperty("body").GetString() ?? "",
                    root.GetProperty("html_url").GetString() ?? "");
            }

            return null;
        }
        catch
        {
            // Нет интернета, GitHub недоступен, ответ изменился — тихо молчим.
            // Проверка обновлений не должна беспокоить пользователя своими сбоями.
            return null;
        }
    }

    /// <summary>Сравнение версий вида «0.2.0». Нечисловые части игнорируются.</summary>
    private static bool IsNewer(string candidate, string current)
    {
        static int[] Parse(string v) =>
            [.. v.Split('.', '-').Select(p => int.TryParse(p, out var n) ? n : 0)];

        var a = Parse(candidate);
        var b = Parse(current);

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }

        return false;
    }

    /// <summary>
    /// Скачивает установщик с поддержкой докачки. Возвращает путь к готовому файлу.
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        Directory.CreateDirectory(CacheFolder);

        var target = Path.Combine(CacheFolder, $"velocity-setup-{update.Version}.exe");
        var partial = target + ".part";

        // Файл уже скачан целиком — второй раз не тянем.
        if (File.Exists(target) && new FileInfo(target).Length == update.SizeBytes)
        {
            progress?.Report(1.0);
            return target;
        }

        long downloaded = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        // Недокачанный кусок больше заявленного размера — файл испорчен, начинаем заново.
        if (downloaded > update.SizeBytes)
        {
            File.Delete(partial);
            downloaded = 0;
        }

        if (downloaded < update.SizeBytes)
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);

            if (downloaded > 0)
                request.Headers.Range = new RangeHeaderValue(downloaded, null);

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();

            // Сервер не поддержал докачку и отдал файл целиком — пишем с нуля.
            bool append = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!append) downloaded = 0;

            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var destination = new FileStream(
                partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancel);
                downloaded += read;
                progress?.Report(Math.Min(1.0, (double)downloaded / update.SizeBytes));
            }
        }

        // Размер не сошёлся — файл неполный, оставлять его как готовый нельзя.
        var actual = new FileInfo(partial).Length;
        if (actual != update.SizeBytes)
            throw new InvalidOperationException(
                Loc.F("Файл скачан не полностью: {0} из {1} байт", actual, update.SizeBytes));

        if (File.Exists(target)) File.Delete(target);
        File.Move(partial, target);

        CleanOldDownloads(target);
        return target;
    }

    /// <summary>Установщики прошлых версий в кэше не нужны — это просто занятое место.</summary>
    private static void CleanOldDownloads(string keep)
    {
        try
        {
            foreach (var file in Directory.GetFiles(CacheFolder))
                if (!file.Equals(keep, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }
        catch { }
    }

    /// <summary>Запускает установщик и закрывает программу, чтобы файлы не были заняты.</summary>
    public static void Install(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            // Установщик сам закроет запущенную копию и запустит новую после установки.
            Arguments = "/SILENT /NORESTART"
        });

        System.Windows.Application.Current.Shutdown();
    }
}
