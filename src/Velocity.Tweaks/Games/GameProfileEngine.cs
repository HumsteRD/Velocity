using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using Velocity.Core;
using Velocity.Tweaks.Actions;

namespace Velocity.Tweaks.Games;

/// <summary>Профиль, сопоставленный с реально установленной игрой.</summary>
public sealed record MatchedGame(GameProfile Profile, GameInfo Installed);

public sealed record GameApplyResult(
    string ProfileId,
    string GameName,
    List<ConfigFileEditor.EditResult> Files,
    List<string> DriverSettings,
    string? LaunchOptions,
    List<GameSetting> Recommendations);

/// <summary>
/// Сопоставляет найденные игры с профилями и применяет движковые настройки.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameProfileEngine
{
    private readonly List<GameProfile> _profiles = [];

    public IReadOnlyList<GameProfile> Profiles => _profiles;
    public List<string> LoadErrors { get; } = [];

    public static GameProfileEngine Load(string? overrideDirectory = null)
    {
        var engine = new GameProfileEngine();

        if (overrideDirectory is not null && Directory.Exists(overrideDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(overrideDirectory, "*.json"))
                engine.Add(() => File.ReadAllText(file), Path.GetFileName(file));
        }
        else
        {
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".GameProfiles.")))
            {
                engine.Add(() =>
                {
                    using var stream = assembly.GetManifestResourceStream(name)!;
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }, name);
            }
        }

        return engine;
    }

    private void Add(Func<string> read, string source)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<GameProfile>>(L10n.TranslateJson(read()), TweakCatalog.JsonOptions);
            if (parsed is not null) _profiles.AddRange(parsed);
        }
        catch (Exception ex) { LoadErrors.Add($"{source}: {ex.Message}"); }
    }

    /// <summary>Находит профили для игр, которые реально установлены.</summary>
    public List<MatchedGame> Match(IEnumerable<GameInfo> installed)
    {
        var games = installed.ToList();
        var matched = new List<MatchedGame>();

        foreach (var profile in _profiles)
        {
            // Совпадение по Steam AppID надёжнее совпадения по названию:
            // названия локализуются и меняются между изданиями игры.
            var game = games.FirstOrDefault(g =>
                profile.SteamAppId is not null && g.StoreId == profile.SteamAppId);

            game ??= games.FirstOrDefault(g =>
                g.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));

            if (game is not null) matched.Add(new MatchedGame(profile, game));
        }

        return matched;
    }

    public GameApplyResult Apply(MatchedGame match, SystemFacts facts, bool dryRun = false)
    {
        var files = new List<ConfigFileEditor.EditResult>();
        var drivers = new List<string>();

        foreach (var tweak in match.Profile.Config)
        {
            var path = ResolvePath(tweak.File, match.Installed);
            if (path is null)
            {
                files.Add(new ConfigFileEditor.EditResult(tweak.File, 0, false, L10n.T("не удалось определить путь")));
                continue;
            }

            files.Add(dryRun
                ? new ConfigFileEditor.EditResult(path, tweak.Entries.Count, !File.Exists(path))
                : ConfigFileEditor.Apply(tweak, path));
        }

        foreach (var setting in match.Profile.NvidiaSettings)
        {
            if (facts.Get("gpu.vendor") != "nvidia") break;

            if (!uint.TryParse(setting.Setting.Replace("0x", ""),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)) continue;

            try
            {
                if (!dryRun) NvidiaExecutor.Write(id, setting.Value);
                drivers.Add($"{setting.Setting} = {setting.Value} — {setting.Why}");
            }
            catch (Exception ex) { drivers.Add(L10n.F("{0}: ошибка — {1}", setting.Setting, ex.Message)); }
        }

        // Советы фильтруем по железу: смысла показывать «снизь тени из-за 4 ГБ видеопамяти»
        // владельцу карты на 16 ГБ нет.
        var recommendations = match.Profile.Settings
            .Where(s => s.When.Count == 0 || facts.MatchesAll(s.When))
            .ToList();

        return new GameApplyResult(match.Profile.Id, match.Profile.Name, files, drivers,
            ResolveTokens(match.Profile.LaunchOptions, facts), recommendations);
    }

    /// <summary>Возвращает конфиги игры к сохранённым оригиналам.</summary>
    public List<string> Revert(MatchedGame match)
    {
        var restored = new List<string>();
        foreach (var tweak in match.Profile.Config)
        {
            var path = ResolvePath(tweak.File, match.Installed);
            if (path is not null && ConfigFileEditor.Restore(path)) restored.Add(path);
        }
        return restored;
    }

    // ────────────────────── Подстановки по железу ──────────────────────

    /// <summary>
    /// Подставляет в текст значения, зависящие от конкретного компьютера.
    ///
    /// Зачем: параметр вроде «-maxMem=16384» нельзя зашивать константой — на машине
    /// с 32 ГБ он искусственно ограничит игру вдвое, а на машине с 8 ГБ приведёт
    /// к выходу за пределы физической памяти. Значение обязано считаться от факта.
    /// </summary>
    public static string? ResolveTokens(string? text, SystemFacts facts)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{')) return text;

        double ramGb = double.TryParse(facts.Get("ram.total_gb"),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : 0;

        int ramMb = (int)Math.Round(ramGb * 1024);

        // Игре отдаём долю памяти, остальное оставляем системе, драйверу и лаунчеру.
        // Округляем вниз до целого гигабайта — дробные значения движки не любят.
        int Share(double fraction) => Math.Max(2048, (int)(ramMb * fraction / 1024) * 1024);

        return text
            .Replace("{ram_mb}", ramMb.ToString())
            .Replace("{ram_mb_75}", Share(0.75).ToString())
            .Replace("{ram_mb_50}", Share(0.50).ToString())
            .Replace("{cpu_threads}", facts.Get("cpu.threads") ?? "4")
            .Replace("{cpu_cores}", facts.Get("cpu.cores") ?? "4");
    }

    // ─────────────────────────── Пути ───────────────────────────

    /// <summary>
    /// Раскрывает подстановки в пути конфига. Возвращает null, если подстановку
    /// разрешить не удалось — лучше пропустить правку, чем создать файл не там.
    /// </summary>
    public static string? ResolvePath(string template, GameInfo game)
    {
        try
        {
            var path = template
                .Replace("{game}", game.InstallPath ?? "")
                .Replace("{documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                .Replace("{localappdata}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

            if (path.Contains("{steam_userdata}"))
            {
                var userdata = FindSteamUserdata();
                if (userdata is null) return null;
                path = path.Replace("{steam_userdata}", userdata);
            }

            // Осталась неразрешённая подстановка — путь неверен.
            if (path.Contains('{')) return null;

            return Path.GetFullPath(path);
        }
        catch { return null; }
    }

    /// <summary>
    /// Каталог userdata активного пользователя Steam. Если аккаунтов несколько,
    /// берём тот, куда писали последним — он почти наверняка и есть текущий.
    /// </summary>
    private static string? FindSteamUserdata()
    {
        try
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
            if (steam is null) return null;

            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) return null;

            return Directory.EnumerateDirectories(userdata)
                .Where(d => Path.GetFileName(d) != "0")
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
