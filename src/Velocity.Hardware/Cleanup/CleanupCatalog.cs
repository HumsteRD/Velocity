using Velocity.Core;
using System.Runtime.Versioning;

namespace Velocity.Hardware.Cleanup;

/// <summary>
/// Что именно считается мусором. Список закрытый и явный.
///
/// Здесь нет и не будет ни масок, ни «умного» поиска ненужных файлов: цена ошибки —
/// чужие данные. Каждый путь добавлен осознанно, с пониманием, что там лежит
/// и что сломается, если это удалить.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CleanupCatalog
{
    public static List<CleanupTarget> All =>
    [
        new()
        {
            Id = "nvidia.installer_cache",
            Title = L10n.T("Распакованные установщики драйверов NVIDIA"),
            Description = L10n.T("Установщик драйвера NVIDIA распаковывается в папку C:\\NVIDIA и после установки её не убирает. ") +
                          L10n.T("Каждое обновление драйвера добавляет туда ещё один полный комплект файлов. ") +
                          L10n.T("За год-два набегает несколько десятков гигабайт."),
            Consequence = L10n.T("Ничего. Это временная папка распаковки, драйвер уже установлен и в ней не нуждается."),
            Kind = CleanupKind.Drivers,
            Safety = CleanupSafety.Safe,
            Paths =
            [
                "C:\\NVIDIA",
                "{programdata}\\NVIDIA Corporation\\Downloader",
                "{programdata}\\NVIDIA Corporation\\NetService",
                "{localappdata}\\NVIDIA Corporation\\NvTelemetry"
            ]
        },
        new()
        {
            Id = "amd.installer_cache",
            Title = L10n.T("Распакованные установщики драйверов AMD"),
            Description = L10n.T("Пакеты драйверов AMD распаковываются в C:\\AMD и остаются там после установки."),
            Consequence = L10n.T("Ничего. Драйвер уже установлен."),
            Kind = CleanupKind.Drivers,
            Safety = CleanupSafety.Safe,
            Paths = ["C:\\AMD"]
        },
        new()
        {
            Id = "windows.update_cache",
            Title = L10n.T("Скачанные обновления Windows"),
            Description = L10n.T("Windows хранит установочные файлы обновлений после того, как они применены. ") +
                          L10n.T("Место освобождается сразу, на работу системы это не влияет."),
            Consequence = L10n.T("Откатить уже установленное обновление станет сложнее. Сами обновления останутся установленными."),
            Kind = CleanupKind.WindowsUpdate,
            Safety = CleanupSafety.Regenerates,
            RequiresElevation = true,
            MinAgeDays = 7,
            Paths = ["{windows}\\SoftwareDistribution\\Download"]
        },
        new()
        {
            Id = "windows.delivery_optimization",
            Title = L10n.T("Кэш раздачи обновлений другим компьютерам"),
            Description = L10n.T("Windows по умолчанию раздаёт скачанные обновления другим компьютерам и держит их копии у себя."),
            Consequence = L10n.T("Ничего."),
            Kind = CleanupKind.WindowsUpdate,
            Safety = CleanupSafety.Safe,
            RequiresElevation = true,
            Paths = ["{windows}\\SoftwareDistribution\\DeliveryOptimization"]
        },
        new()
        {
            Id = "temp.user",
            Title = L10n.T("Временные файлы"),
            Description = L10n.T("Программы складывают сюда промежуточные файлы и далеко не всегда убирают за собой. ") +
                          L10n.T("Файлы, созданные за последние сутки, не трогаем: они могут быть нужны работающим прямо сейчас программам."),
            Consequence = L10n.T("Ничего."),
            Kind = CleanupKind.Temp,
            Safety = CleanupSafety.Safe,
            MinAgeDays = 1,
            Paths = ["{temp}", "{windows}\\Temp"]
        },
        new()
        {
            Id = "cache.shaders",
            Title = L10n.T("Кэш шейдеров видеодрайвера"),
            Description = L10n.T("Драйвер хранит скомпилированные шейдеры, чтобы не компилировать их заново при каждом запуске игры. ") +
                          L10n.T("Нормальный размер — несколько гигабайт, но драйвер почти никогда не убирает записи от удалённых игр ") +
                          L10n.T("и от прошлых версий самого себя. За пару лет кэш дорастает до нескольких десятков гигабайт ") +
                          L10n.T("и становится самой жирной папкой в профиле пользователя."),
            Consequence = L10n.T("Первый запуск каждой игры будет подтормаживать, пока шейдеры компилируются заново. Потом всё вернётся к норме."),
            Kind = CleanupKind.ShaderCache,
            Safety = CleanupSafety.Regenerates,
            Paths =
            [
                "{localappdata}\\NVIDIA\\DXCache",
                "{localappdata}\\NVIDIA\\GLCache",
                "{localappdata}\\NVIDIA\\ComputeCache",
                "{localappdata}\\NVIDIA\\NV_Cache",
                "{localappdata}\\AMD\\DxCache",
                "{localappdata}\\AMD\\GLCache",
                "{localappdata}\\D3DSCache",
                "{localappdata}\\Intel\\ShaderCache",
                "{programdata}\\NVIDIA Corporation\\NV_Cache"
            ]
        },
        new()
        {
            Id = "dumps.crash",
            Title = L10n.T("Отчёты о сбоях и дампы памяти"),
            Description = L10n.T("При падении программы или системы Windows сохраняет слепок памяти. ") +
                          L10n.T("Полный дамп системы весит столько же, сколько оперативная память."),
            Consequence = L10n.T("Разобраться в причине давнего сбоя будет уже нельзя."),
            Kind = CleanupKind.CrashDumps,
            Safety = CleanupSafety.Safe,
            MinAgeDays = 7,
            Paths =
            [
                "{localappdata}\\CrashDumps",
                "{windows}\\Minidump",
                "{localappdata}\\Microsoft\\Windows\\WER"
            ]
        },
        new()
        {
            Id = "logs.windows",
            Title = L10n.T("Журналы обслуживания Windows"),
            Description = L10n.T("Журналы установки компонентов и обновлений. Нужны только при разборе проблем с установкой."),
            Consequence = L10n.T("Ничего для повседневной работы."),
            Kind = CleanupKind.Logs,
            Safety = CleanupSafety.Safe,
            RequiresElevation = true,
            MinAgeDays = 14,
            Paths = ["{windows}\\Logs\\CBS", "{windows}\\Logs\\DISM"]
        },
        new()
        {
            Id = "launchers.cache",
            Title = L10n.T("Кэш игровых лаунчеров"),
            Description = L10n.T("Steam, Epic и Battle.net кэшируют картинки магазина и веб-страницы интерфейса. ") +
                          L10n.T("Сами игры и их файлы это не затрагивает."),
            Consequence = L10n.T("Магазин и библиотека будут открываться чуть медленнее в первый раз."),
            Kind = CleanupKind.Launchers,
            Safety = CleanupSafety.Regenerates,
            Paths =
            [
                "{localappdata}\\Steam\\htmlcache",
                "{localappdata}\\EpicGamesLauncher\\Saved\\webcache",
                "{localappdata}\\EpicGamesLauncher\\Saved\\Logs",
                "{appdata}\\Battle.net\\Cache",
                "{localappdata}\\Battle.net\\Cache"
            ]
        },
        new()
        {
            Id = "reports.wer_system",
            Title = L10n.T("Системные отчёты об ошибках"),
            Description = L10n.T("Отчёты, которые Windows собирает после вылетов программ и драйверов для всех пользователей. ") +
                          L10n.T("Копятся годами, особенно если какая-то программа падает регулярно."),
            Consequence = L10n.T("Разобраться в причине давнего сбоя по отчёту Windows будет уже нельзя. Журнал событий остаётся."),
            Kind = CleanupKind.CrashDumps,
            Safety = CleanupSafety.Safe,
            RequiresElevation = true,
            MinAgeDays = 3,
            Paths =
            [
                "{programdata}\\Microsoft\\Windows\\WER\\ReportArchive",
                "{programdata}\\Microsoft\\Windows\\WER\\ReportQueue"
            ]
        },
        new()
        {
            Id = "browsers.cache",
            Title = L10n.T("Кэш браузеров"),
            Description = L10n.T("Сохранённые копии картинок, скриптов и видео с сайтов в Chrome, Edge, Яндекс Браузере, Brave и Opera GX. ") +
                          L10n.T("Браузеры ограничивают его размер, но держат по гигабайту и больше на каждый профиль. ") +
                          L10n.T("Пароли, история, вкладки и входы на сайты здесь не хранятся и не затрагиваются. ") +
                          L10n.T("Лучше закрыть браузер перед очисткой: занятые им файлы будут пропущены."),
            Consequence = L10n.T("Сайты откроются чуть медленнее в первый раз."),
            Kind = CleanupKind.Browsers,
            Safety = CleanupSafety.Regenerates,
            Paths =
            [
                "{localappdata}\\Google\\Chrome\\User Data\\Default\\Cache",
                "{localappdata}\\Google\\Chrome\\User Data\\Default\\Code Cache",
                "{localappdata}\\Microsoft\\Edge\\User Data\\Default\\Cache",
                "{localappdata}\\Microsoft\\Edge\\User Data\\Default\\Code Cache",
                "{localappdata}\\Yandex\\YandexBrowser\\User Data\\Default\\Cache",
                "{localappdata}\\BraveSoftware\\Brave-Browser\\User Data\\Default\\Cache",
                "{localappdata}\\Opera Software\\Opera GX Stable\\Cache"
            ]
        },
        new()
        {
            Id = "apps.cache",
            Title = L10n.T("Кэш Discord и Spotify"),
            Description = L10n.T("Discord хранит картинки, эмодзи и гифки из всех серверов, Spotify — скачанные для потока фрагменты треков. ") +
                          L10n.T("Оба кэша растут до гигабайтов. Переписка и скачанные для офлайна треки не затрагиваются."),
            Consequence = L10n.T("Картинки в Discord и треки в Spotify подгрузятся заново при первом открытии."),
            Kind = CleanupKind.Apps,
            Safety = CleanupSafety.Regenerates,
            Paths =
            [
                "{appdata}\\discord\\Cache",
                "{appdata}\\discord\\Code Cache",
                "{appdata}\\discord\\GPUCache",
                "{localappdata}\\Spotify\\Data"
            ]
        },
        new()
        {
            Id = "windows.old",
            Title = L10n.T("Предыдущая версия Windows"),
            Description = L10n.T("После крупного обновления Windows сохраняет старую систему целиком, чтобы можно было откатиться. ") +
                          L10n.T("Обычно это 15–30 гигабайт. Windows удаляет её сама через 10 дней, но не всегда."),
            Consequence = L10n.T("Откатиться на предыдущую версию Windows станет невозможно. Если обновление прошло больше двух недель назад и всё работает, держать её смысла нет."),
            Kind = CleanupKind.WindowsUpdate,
            Safety = CleanupSafety.PointOfNoReturn,
            RequiresElevation = true,
            Paths = ["C:\\Windows.old"]
        }
    ];

    /// <summary>Раскрывает подстановки в пути категории.</summary>
    public static string Resolve(string template) => template
        .Replace("{temp}", Path.GetTempPath().TrimEnd('\\'))
        .Replace("{windows}", Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        .Replace("{localappdata}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        .Replace("{programdata}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
        .Replace("{userprofile}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
