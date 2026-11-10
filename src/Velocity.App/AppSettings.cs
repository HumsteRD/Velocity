using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velocity.App;

/// <summary>
/// Настройки самой программы. Лежат рядом с точками отката, в папке данных.
///
/// Сохранение никогда не бросает исключений: невозможность записать настройку —
/// не повод прерывать работу, пользователь просто потеряет одну галочку.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Velocity");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Проверять обновления при запуске.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Скачивать и предлагать установку без отдельного вопроса о скачивании.</summary>
    public bool AutoDownloadUpdates { get; set; }

    /// <summary>Версия, о которой пользователь уже сказал «напомни позже».</summary>
    public string? SkippedVersion { get; set; }

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Тема FI: dark, gray, light, anime, mono, neon, forest, ocean. null — ещё не выбиралась.</summary>
    public string? Theme { get; set; }

    /// <summary>Свой акцент «#RRGGBB»; null — акцент темы.</summary>
    public string? Accent { get; set; }

    /// <summary>Шрифт: onest, segoe, bahnschrift, mono, georgia, handwritten; null — как в теме.</summary>
    public string? Font { get; set; }

    /// <summary>Углы: sharp, round; null — как в теме.</summary>
    public string? Radius { get; set; }

    /// <summary>Масштаб интерфейса в процентах: 90, 110, 120; 0 — обычный.</summary>
    public int Scale { get; set; }

    /// <summary>Анимации интерфейса (лепестки «Аниме», появление экранов).</summary>
    public bool Motion { get; set; } = true;

    /// <summary>Прозрачность фона окна, %: 0, 15, 30, 45.</summary>
    public int Transparency { get; set; }

    /// <summary>Размывать то, что под прозрачным окном.</summary>
    public bool Blur { get; set; } = true;

    public string? ButtonStyle { get; set; }
    public string? ButtonColor { get; set; }
    public string? Background { get; set; }
    public string? BackgroundAnim { get; set; }
    public string? ClickEffect { get; set; }
    public string? Shadows { get; set; }
    public int CardTransparency { get; set; }

    /// <summary>Окно поверх всех окон.</summary>
    public bool Topmost { get; set; }

    /// <summary>Крестик сворачивает в трей, а не закрывает программу.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Язык интерфейса: ru, en.</summary>
    public string Language { get; set; } = "ru";

    [JsonIgnore]
    public Look Look
    {
        get => new(ThemeManager.Migrate(Theme), Accent, Font, Radius, Scale, Motion, Transparency, Blur)
        {
            ButtonStyle = ButtonStyle, ButtonColor = ButtonColor, Background = Background,
            BackgroundAnim = BackgroundAnim, ClickEffect = ClickEffect, Shadows = Shadows,
            CardTransparency = CardTransparency
        };
        set
        {
            Theme = value.Theme;
            Accent = value.Accent;
            Font = value.Font;
            Radius = value.Radius;
            Scale = value.Scale;
            Motion = value.Motion;
            Transparency = value.Transparency;
            Blur = value.Blur;
            ButtonStyle = value.ButtonStyle;
            ButtonColor = value.ButtonColor;
            Background = value.Background;
            BackgroundAnim = value.BackgroundAnim;
            ClickEffect = value.ClickEffect;
            Shadows = value.Shadows;
            CardTransparency = value.CardTransparency;
        }
    }

    /// <summary>Отправлять анонимную статистику применения настроек.</summary>
    public bool ShareTelemetry { get; set; }

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                       ?? new AppSettings();
        }
        catch { /* повреждённый файл настроек — начинаем с умолчаний */ }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* не смогли записать — не повод падать */ }
    }
}
