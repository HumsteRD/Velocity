using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Velocity.App;

/// <summary>
/// Оформление целиком, как в FI: тема, свой акцент, шрифт, углы и масштаб.
/// Пустые значения означают «как в теме».
/// </summary>
public sealed record Look(string Theme, string? Accent = null, string? Font = null, string? Radius = null, int Scale = 0,
    bool Motion = true, int Transparency = 0, bool Blur = true)
{
    /// <summary>Прозрачность фона окна в процентах: 0, 15, 30, 45.</summary>
    public double BackgroundOpacity => 1 - Math.Clamp(Transparency, 0, 60) / 100.0;

    /// <summary>Анимации включены и в оформлении, и в Windows («Показывать анимацию»).</summary>
    public bool AnimationsOn => Motion && SystemParameters.ClientAreaAnimation;

    public double ScaleFactor => Scale is 85 or 90 or 110 or 120 or 130 ? Scale / 100.0 : 1.0;

    /// <summary>Стиль главных кнопок: filled (по умолчанию), soft, outline.</summary>
    public string? ButtonStyle { get; init; }

    /// <summary>Свой цвет главных кнопок «#RRGGBB»; null — цвет акцента.</summary>
    public string? ButtonColor { get; init; }

    /// <summary>Свой цвет фона «#RRGGBB»; остальные поверхности выводятся из него.</summary>
    public string? Background { get; init; }

    /// <summary>Анимация фона: none, waves, aurora, particles, petals, grid; null — как в теме.</summary>
    public string? BackgroundAnim { get; init; }

    /// <summary>Эффект нажатия: none, ripple, rings, glow; null — как в теме.</summary>
    public string? ClickEffect { get; init; }

    /// <summary>Тени карточек: none, soft, strong; null — как в теме.</summary>
    public string? Shadows { get; init; }

    /// <summary>Прозрачность карточек, %: 0, 10, 20, 35.</summary>
    public int CardTransparency { get; init; }
}

/// <summary>Тема в терминах FI: набор CSS-переменных поверх базовой тёмной.</summary>
public sealed class FiTheme
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, string> Vars { get; init; }
    public bool IsLight { get; init; }

    /// <summary>Анимация фона темы по умолчанию.</summary>
    public string Anim { get; init; } = "none";

    /// <summary>Эффект нажатия темы по умолчанию.</summary>
    public string Click { get; init; } = "ripple";

    /// <summary>Тени карточек темы по умолчанию.</summary>
    public string Shadows { get; init; } = "none";

    public string Var(string name) => Vars.TryGetValue(name, out var v) ? v : ThemeManager.Root[name];

    // Миниатюра в настройках — фон, панель и акцент самой темы, как в карточках FI.
    public Brush PreviewBg => ThemeManager.Frozen(ThemeManager.Solid(Var("--bg"), "#000000"));
    public Brush PreviewPanel => ThemeManager.Frozen(ThemeManager.Solid(Var("--panel"), Var("--bg")));
    public Brush PreviewAccent => ThemeManager.Frozen(ThemeManager.Solid(Var("--violet"), Var("--bg")));
    public Brush PreviewText => ThemeManager.Frozen(ThemeManager.Solid(Var("--text-3"), Var("--bg")));
    public bool IsCurrent => ThemeManager.Current.Theme == Id;
}

/// <summary>
/// Перенос системы оформления FI в WPF.
///
/// Палитры и формула акцента взяты из FI один к одному (app.css, accentVars). Разница одна:
/// в WPF нет полупрозрачных CSS-переменных поверх фона, поэтому цвета с альфой
/// («Аниме», оттенки акцента) заранее смешиваются с подложкой в сплошные.
///
/// Экраны берут цвета через StaticResource, поэтому смена оформления подменяет словари
/// и пересоздаёт главное окно; данные скана живут в AppState и не теряются.
/// </summary>
public static class ThemeManager
{
    public const string DefaultTheme = "graphite";

    /// <summary>Базовая тёмная тема FI (:root). Остальные темы переопределяют её частично.</summary>
    public static readonly Dictionary<string, string> Root = new()
    {
        ["--bg"] = "#0B0A0D", ["--sunken"] = "#08070A", ["--panel"] = "#131116", ["--raised"] = "#1B1820",
        ["--hover"] = "#24202A", ["--line"] = "#26222B", ["--line-strong"] = "#36313C",
        ["--text"] = "#F1EEF3", ["--text-2"] = "#A7A1AD", ["--text-3"] = "#6F6977",
        ["--violet"] = "#7C4FE0", ["--violet-hover"] = "#8A5FEA", ["--violet-text"] = "#B294F7",
        ["--violet-tint"] = "rgba(154, 108, 240, 0.14)", ["--violet-line"] = "rgba(154, 108, 240, 0.45)",
        ["--red"] = "#E5484D", ["--red-text"] = "#F2787C", ["--red-tint"] = "rgba(229, 72, 77, 0.14)",
        ["--amber-text"] = "#F0BA62", ["--amber-tint"] = "rgba(235, 169, 69, 0.14)",
        ["--r-lg"] = "12", ["--r-md"] = "8", ["--r-sm"] = "6",
        ["--violet-pressed"] = "#6A40CC", ["--neutral-tint"] = "rgba(255, 255, 255, 0.06)",
        ["--seg-sel"] = "#2A2530", ["--knob-off"] = "#A7A1AD",
        // --glow из FI: красное и фиолетовое свечение за главной карточкой.
        ["--glow-a"] = "rgba(229, 72, 77, 0.17)", ["--glow-b"] = "rgba(140, 92, 242, 0.08)",
    };

    public static IReadOnlyList<FiTheme> Themes { get; } =
    [
        new()
        {
            // Строгая рабочая тема по умолчанию: нейтральный графит и синий акцент, без декора.
            Id = "graphite", Name = Loc.T("Графит"), Anim = "aurora", Vars = new()
            {
                ["--bg"] = "#0E1013", ["--sunken"] = "#0A0C0E", ["--panel"] = "#15181D", ["--raised"] = "#1B1F25",
                ["--hover"] = "#232830", ["--line"] = "#232830", ["--line-strong"] = "#333A44",
                ["--text"] = "#E9EDF2", ["--text-2"] = "#A3ACB8", ["--text-3"] = "#6B7480",
                ["--violet"] = "#3B82F6", ["--violet-hover"] = "#5B95F7", ["--violet-text"] = "#8AB4FA",
                ["--violet-tint"] = "rgba(59, 130, 246, 0.14)", ["--violet-line"] = "rgba(59, 130, 246, 0.45)",
                ["--violet-pressed"] = "#2563EB", ["--neutral-tint"] = "rgba(255, 255, 255, 0.05)",
                ["--seg-sel"] = "#262C35", ["--knob-off"] = "#A3ACB8",
                ["--glow-a"] = "rgba(59, 130, 246, 0.10)", ["--glow-b"] = "rgba(120, 140, 170, 0.05)",
                ["--r-lg"] = "10", ["--r-md"] = "7", ["--r-sm"] = "5",
            }
        },
        new() { Id = "dark", Name = Loc.T("Тёмная"), Anim = "aurora", Vars = [] },
        new()
        {
            // Вода: глубокая бирюза, волны на фоне и круги от нажатий, как от капли.
            Id = "water", Name = Loc.T("Вода"), Anim = "waves", Click = "rings", Vars = new()
            {
                ["--bg"] = "#05161D", ["--sunken"] = "#04121A", ["--panel"] = "#0A2129", ["--raised"] = "#0F2B35",
                ["--hover"] = "#143542", ["--line"] = "#143A47", ["--line-strong"] = "#1F5466",
                ["--text"] = "#E6F9FF", ["--text-2"] = "#9AC5D1", ["--text-3"] = "#5E8C99",
                ["--violet"] = "#22C3E6", ["--violet-hover"] = "#45D0EE", ["--violet-text"] = "#7FE3F7",
                ["--violet-tint"] = "rgba(34, 195, 230, 0.14)", ["--violet-line"] = "rgba(34, 195, 230, 0.45)",
                ["--violet-pressed"] = "#13A6C7", ["--neutral-tint"] = "rgba(200, 240, 255, 0.06)",
                ["--seg-sel"] = "#17404F", ["--knob-off"] = "#9AC5D1",
                ["--glow-a"] = "rgba(34, 195, 230, 0.20)", ["--glow-b"] = "rgba(40, 120, 220, 0.10)",
                ["--on-accent"] = "#031418", ["--r-lg"] = "14", ["--r-md"] = "10", ["--r-sm"] = "7",
            }
        },
        new()
        {
            Id = "gray", Name = Loc.T("Серая"), Vars = new()
            {
                ["--neutral-tint"] = "rgba(255, 255, 255, 0.07)", ["--seg-sel"] = "#43454B", ["--knob-off"] = "#B5BAC1", ["--glow-a"] = "rgba(229, 72, 77, 0.12)", ["--glow-b"] = "rgba(140, 92, 242, 0.06)",
                ["--bg"] = "#1E1F22", ["--sunken"] = "#18191C", ["--panel"] = "#2B2D31", ["--raised"] = "#313338",
                ["--hover"] = "#3A3C42", ["--line"] = "#3A3C41", ["--line-strong"] = "#4B4E55",
                ["--text"] = "#F2F3F5", ["--text-2"] = "#B5BAC1", ["--text-3"] = "#80848E",
            }
        },
        new()
        {
            Id = "light", Name = Loc.T("Светлая"), IsLight = true, Shadows = "soft", Vars = new()
            {
                ["--violet-pressed"] = "#5E33C0", ["--neutral-tint"] = "rgba(22, 19, 26, 0.05)", ["--seg-sel"] = "#FFFFFF", ["--knob-off"] = "#FFFFFF", ["--glow-a"] = "rgba(229, 72, 77, 0.10)", ["--glow-b"] = "rgba(110, 63, 216, 0.05)",
                ["--bg"] = "#F6F4F8", ["--sunken"] = "#EDEAF0", ["--panel"] = "#FFFFFF", ["--raised"] = "#F3F0F5",
                ["--hover"] = "#EAE6EE", ["--line"] = "#E6E2EA", ["--line-strong"] = "#D4CEDA",
                ["--text"] = "#16131A", ["--text-2"] = "#5E5865", ["--text-3"] = "#8E8895",
                ["--violet"] = "#6E3FD8", ["--violet-hover"] = "#7B4EE3", ["--violet-text"] = "#6437CC",
                ["--violet-tint"] = "rgba(110, 63, 216, 0.10)", ["--violet-line"] = "rgba(110, 63, 216, 0.40)",
                ["--red"] = "#D1343A", ["--red-text"] = "#C42B31", ["--red-tint"] = "rgba(209, 52, 58, 0.10)",
                ["--amber-text"] = "#9A6412", ["--amber-tint"] = "rgba(201, 138, 43, 0.14)",
            }
        },
        new()
        {
            Id = "anime", Name = Loc.T("Аниме"), Anim = "petals", Vars = new()
            {
                ["--violet-pressed"] = "#E64A92", ["--neutral-tint"] = "rgba(255, 220, 240, 0.07)", ["--seg-sel"] = "#4A2C58", ["--knob-off"] = "#D9B6CB", ["--glow-a"] = "rgba(255, 95, 168, 0.28)", ["--glow-b"] = "rgba(160, 120, 255, 0.12)",
                ["--bg"] = "#1A101F", ["--sunken"] = "#140C18", ["--panel"] = "rgba(40, 24, 48, 0.72)",
                ["--raised"] = "rgba(58, 36, 70, 0.8)", ["--hover"] = "#3D2649",
                ["--line"] = "rgba(255, 170, 214, 0.14)", ["--line-strong"] = "rgba(255, 170, 214, 0.26)",
                ["--text"] = "#FFF0F7", ["--text-2"] = "#D9B6CB", ["--text-3"] = "#9C7A90",
                ["--violet"] = "#FF5FA8", ["--violet-hover"] = "#FF78B7", ["--violet-text"] = "#FF9CCB",
                ["--violet-tint"] = "rgba(255, 95, 168, 0.16)", ["--violet-line"] = "rgba(255, 95, 168, 0.5)",
                ["--r-lg"] = "18", ["--r-md"] = "12", ["--r-sm"] = "9",
            }
        },
        new()
        {
            Id = "mono", Name = Loc.T("Ч/Б"), Click = "none", Vars = new()
            {
                ["--violet-pressed"] = "#CCCCCC", ["--neutral-tint"] = "rgba(255, 255, 255, 0.07)", ["--seg-sel"] = "#2A2A2A", ["--knob-off"] = "#B3B3B3", ["--glow-a"] = "rgba(0, 0, 0, 0)", ["--glow-b"] = "rgba(0, 0, 0, 0)",
                ["--bg"] = "#000000", ["--sunken"] = "#000000", ["--panel"] = "#0D0D0D", ["--raised"] = "#171717",
                ["--hover"] = "#222222", ["--line"] = "#2A2A2A", ["--line-strong"] = "#444444",
                ["--text"] = "#FFFFFF", ["--text-2"] = "#B3B3B3", ["--text-3"] = "#7A7A7A",
                ["--violet"] = "#FFFFFF", ["--violet-hover"] = "#E6E6E6", ["--violet-text"] = "#FFFFFF",
                ["--violet-tint"] = "rgba(255, 255, 255, 0.10)", ["--violet-line"] = "rgba(255, 255, 255, 0.45)",
                ["--red"] = "#FFFFFF", ["--red-text"] = "#FFFFFF", ["--red-tint"] = "rgba(255, 255, 255, 0.16)",
                ["--amber-text"] = "#D9D9D9", ["--amber-tint"] = "rgba(255, 255, 255, 0.10)",
                ["--on-accent"] = "#000000", ["--ui"] = "mono",
            }
        },
        new()
        {
            Id = "neon", Name = Loc.T("Неон"), Anim = "grid", Click = "glow", Vars = new()
            {
                ["--violet-pressed"] = "#00B8D1", ["--neutral-tint"] = "rgba(160, 200, 255, 0.07)", ["--seg-sel"] = "#222A52", ["--knob-off"] = "#9DB3CC", ["--glow-a"] = "rgba(255, 46, 136, 0.22)", ["--glow-b"] = "rgba(0, 217, 245, 0.12)",
                ["--bg"] = "#070812", ["--sunken"] = "#05060D", ["--panel"] = "#0D1020", ["--raised"] = "#141831",
                ["--hover"] = "#1C2242", ["--line"] = "#1D2444", ["--line-strong"] = "#2C3562",
                ["--text"] = "#EAF6FF", ["--text-2"] = "#9DB3CC", ["--text-3"] = "#627790",
                ["--violet"] = "#00D9F5", ["--violet-hover"] = "#33E3F8", ["--violet-text"] = "#5CEBFF",
                ["--violet-tint"] = "rgba(0, 217, 245, 0.13)", ["--violet-line"] = "rgba(0, 217, 245, 0.5)",
                ["--red"] = "#FF2E88", ["--red-text"] = "#FF6AAB", ["--red-tint"] = "rgba(255, 46, 136, 0.15)",
                ["--on-accent"] = "#041018",
            }
        },
        new()
        {
            Id = "forest", Name = Loc.T("Лес"), Anim = "particles", Vars = new()
            {
                ["--violet-pressed"] = "#33A05C", ["--neutral-tint"] = "rgba(220, 255, 230, 0.06)", ["--seg-sel"] = "#263A2E", ["--knob-off"] = "#A7BBAD", ["--glow-a"] = "rgba(63, 182, 107, 0.2)", ["--glow-b"] = "rgba(200, 220, 120, 0.07)",
                ["--bg"] = "#0C1410", ["--sunken"] = "#08100C", ["--panel"] = "#121D17", ["--raised"] = "#19271F",
                ["--hover"] = "#213228", ["--line"] = "#1F2E25", ["--line-strong"] = "#2F4337",
                ["--text"] = "#EEF6F0", ["--text-2"] = "#A7BBAD", ["--text-3"] = "#6E8476",
                ["--violet"] = "#3FB66B", ["--violet-hover"] = "#52C47C", ["--violet-text"] = "#7DDB9E",
                ["--violet-tint"] = "rgba(63, 182, 107, 0.15)", ["--violet-line"] = "rgba(63, 182, 107, 0.45)",
            }
        },
        new()
        {
            Id = "ocean", Name = Loc.T("Океан"), Anim = "waves", Click = "rings", Vars = new()
            {
                ["--violet-pressed"] = "#2478D6", ["--neutral-tint"] = "rgba(200, 230, 255, 0.06)", ["--seg-sel"] = "#22374E", ["--knob-off"] = "#9FB6CB", ["--glow-a"] = "rgba(47, 140, 240, 0.22)", ["--glow-b"] = "rgba(60, 220, 200, 0.08)",
                ["--bg"] = "#08111C", ["--sunken"] = "#060D16", ["--panel"] = "#0E1A28", ["--raised"] = "#142334",
                ["--hover"] = "#1B2D42", ["--line"] = "#1A2B3E", ["--line-strong"] = "#29405A",
                ["--text"] = "#EAF3FB", ["--text-2"] = "#9FB6CB", ["--text-3"] = "#66819A",
                ["--violet"] = "#2F8CF0", ["--violet-hover"] = "#4A9DF4", ["--violet-text"] = "#7DB8FA",
                ["--violet-tint"] = "rgba(47, 140, 240, 0.15)", ["--violet-line"] = "rgba(47, 140, 240, 0.45)",
            }
        },
    ];

    /// <summary>Готовые акценты FI.</summary>
    public static readonly string[] Accents =
        ["#7C4FE0", "#FF5FA8", "#E5484D", "#F59E0B", "#3FB66B", "#00D9F5", "#2F8CF0", "#FFFFFF"];

    public static readonly (string Id, string Name, string Family)[] Fonts =
    [
        ("", Loc.T("Как в теме"), ""),
        ("onest", "Onest", "Onest, Segoe UI Variable Text, Segoe UI"),
        ("segoe", "Segoe UI", "Segoe UI Variable Text, Segoe UI"),
        ("bahnschrift", "Bahnschrift", "Bahnschrift, Segoe UI"),
        ("mono", Loc.T("Моноширинный"), "JetBrains Mono, Cascadia Mono, Consolas"),
        ("georgia", Loc.T("С засечками"), "Georgia, Times New Roman"),
        ("handwritten", Loc.T("Рукописный"), "Segoe Print, Comic Sans MS"),
    ];

    public static readonly (string Id, string Name)[] Radii = [("sharp", Loc.T("Острые")), ("", Loc.T("Обычные")), ("round", Loc.T("Круглые"))];
    public static readonly (int Value, string Name)[] Scales =
        [(85, "85%"), (90, "90%"), (0, "100%"), (110, "110%"), (120, "120%"), (130, "130%")];

    public static readonly (string Id, string Name)[] ButtonStyles =
        [("", Loc.T("Заливка")), ("soft", Loc.T("Мягкие")), ("outline", Loc.T("Контур"))];

    public static readonly (string Id, string Name)[] Animations =
        [("", Loc.T("Как в теме")), ("none", Loc.T("Нет")), ("aurora", Loc.T("Сияние")), ("waves", Loc.T("Волны")),
         ("particles", Loc.T("Частицы")), ("grid", Loc.T("Сетка")), ("petals", Loc.T("Лепестки"))];

    public static readonly (string Id, string Name)[] ClickEffects =
        [("", Loc.T("Как в теме")), ("none", Loc.T("Нет")), ("ripple", Loc.T("Волна")), ("rings", Loc.T("Круги на воде")), ("glow", Loc.T("Вспышка"))];

    public static readonly (string Id, string Name)[] ShadowLevels =
        [("", Loc.T("Как в теме")), ("none", Loc.T("Нет")), ("soft", Loc.T("Мягкие")), ("strong", Loc.T("Глубокие"))];

    public static readonly (int Value, string Name)[] CardLevels =
        [(0, Loc.T("Плотные")), (10, "10%"), (20, "20%"), (35, "35%")];

    /// <summary>Действующая анимация фона: выбранная или темы; без анимаций — статичная.</summary>
    public static string EffectiveAnim(Look look) =>
        string.IsNullOrEmpty(look.BackgroundAnim) ? Find(look.Theme).Anim : look.BackgroundAnim;

    public static string EffectiveClick(Look look) =>
        string.IsNullOrEmpty(look.ClickEffect) ? Find(look.Theme).Click : look.ClickEffect;

    public static string EffectiveShadows(Look look) =>
        string.IsNullOrEmpty(look.Shadows) ? Find(look.Theme).Shadows : look.Shadows;

    public static Look Current { get; private set; } = new(DefaultTheme);

    public static FiTheme Find(string? id) =>
        Themes.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];

    /// <summary>Темы прежних версий VELOCITY переводятся в ближайшие темы FI.</summary>
    public static string Migrate(string? id) => id?.ToLowerInvariant() switch
    {
        "rose" => "anime",
        "mint" => "forest",
        "blue" => "ocean",
        null or "" => DefaultTheme,
        var other => Themes.Any(t => t.Id == other) ? other : DefaultTheme
    };

    // ───────────────────────────── Применение ─────────────────────────────

    public static void Apply(Look look)
    {
        var app = Application.Current;
        if (app is null) return;

        var theme = WithBackground(Find(look.Theme), look.Background);
        string bg = theme.Var("--bg");
        Color panel = Solid(theme.Var("--panel"), bg);
        string panelHex = Hex(panel);

        // Акцент: свой цвет считается по формуле accentVars из FI, иначе — значения темы.
        string accent, accentHover, accentPressed, accentInk, accentTint, accentLine, onAccent;
        if (TryParseHex(look.Accent, out var custom))
        {
            double lum = Luminance(custom);
            accent = Hex(custom);
            accentHover = Hex(Mix(custom, lum > 0.6 ? Colors.Black : Colors.White, 0.12));
            accentPressed = Hex(Mix(custom, Colors.Black, 0.18));
            accentInk = Hex(theme.IsLight ? Mix(custom, Colors.Black, 0.25) : Mix(custom, Colors.White, 0.3));
            accentTint = $"rgba({custom.R}, {custom.G}, {custom.B}, 0.14)";
            accentLine = $"rgba({custom.R}, {custom.G}, {custom.B}, 0.45)";
            onAccent = lum > 0.6 ? "#000000" : "#FFFFFF";
        }
        else
        {
            accent = theme.Var("--violet");
            accentHover = theme.Var("--violet-hover");
            accentPressed = theme.Var("--violet-pressed");
            accentInk = theme.Var("--violet-text");
            accentTint = theme.Var("--violet-tint");
            accentLine = theme.Var("--violet-line");
            onAccent = theme.Vars.TryGetValue("--on-accent", out var on) ? on
                : Luminance(Solid(accent, bg)) > 0.6 ? "#000000" : "#FFFFFF";
        }

        var text3 = Solid(theme.Var("--text-3"), bg);
        var colors = new Dictionary<string, Color>
        {
            ["BgBase"] = Solid(bg, "#000000"),
            ["BgInset"] = Solid(theme.Var("--sunken"), bg),
            ["BgSurface"] = panel,
            ["BgElevated"] = Solid(theme.Var("--raised"), bg),
            ["BgHover"] = Solid(theme.Var("--hover"), bg),
            ["BorderSubtle"] = Solid(theme.Var("--line"), panelHex),
            ["BorderStrong"] = Solid(theme.Var("--line-strong"), panelHex),
            ["BorderAccent"] = Solid(accentLine, panelHex),
            ["TextPrimary"] = Solid(theme.Var("--text"), bg),
            ["TextSecondary"] = Solid(theme.Var("--text-2"), bg),
            ["TextTertiary"] = text3,
            ["TextDisabled"] = Mix(text3, Solid(bg, "#000000"), 0.45),
            ["Accent"] = Solid(accent, bg),
            ["AccentHover"] = Solid(accentHover, bg),
            ["AccentDim"] = Solid(accentTint, panelHex),
            ["AccentText"] = Solid(onAccent, bg),
            ["AccentInk"] = Solid(accentInk, bg),
            ["Critical"] = Solid(theme.Var("--red-text"), bg),
            ["CriticalDim"] = Solid(theme.Var("--red-tint"), panelHex),
            ["Warning"] = Solid(theme.Var("--amber-text"), bg),
            ["WarningDim"] = Solid(theme.Var("--amber-tint"), panelHex),
            // Отдельного «информационного» цвета в FI нет: справочные блоки там окрашены акцентом.
            ["Info"] = Solid(accentInk, bg),
            ["InfoDim"] = Solid(accentTint, panelHex),
            // Зелёного токена в FI тоже нет — подбираем под светлоту темы; в Ч/Б всё белое, как в FI.
            ["Success"] = theme.Id == "mono" ? Colors.White
                : theme.IsLight ? Parse("#1E8A4C") : Parse("#4CC38A"),
        };

        var tokens = new ResourceDictionary
        {
            Source = new Uri("/Velocity;component/Theme/Tokens.xaml", UriKind.Relative)
        };
        colors["AccentPressed"] = Solid(accentPressed, bg);
        colors["SegSelected"] = Solid(theme.Var("--seg-sel"), panelHex);
        colors["KnobOff"] = Solid(theme.Var("--knob-off"), bg);
        colors["BgPopup"] = Solid(theme.Var("--raised"), bg);

        foreach (var (key, color) in colors)
        {
            tokens[key] = Frozen(color);
            if (tokens.Contains(key + "Color")) tokens[key + "Color"] = color;
        }

        // Полупрозрачные токены остаются полупрозрачными: они кладутся поверх любой подложки.
        tokens["NeutralTint"] = Frozen(Alpha(theme.Var("--neutral-tint")));
        tokens["AccentLine"] = Frozen(Alpha(accentLine));
        tokens["AccentTint"] = Frozen(Alpha(accentTint));

        // «Аниме» в FI — полупрозрачные панели поверх свечения и лепестков. Повторяем это:
        // панели и линии этой темы не смешиваются с фоном заранее.
        if (theme.Id == "anime")
        {
            tokens["BgSurface"] = Frozen(Alpha(theme.Var("--panel")));
            tokens["BgElevated"] = Frozen(Alpha(theme.Var("--raised")));
            tokens["BorderSubtle"] = Frozen(Alpha(theme.Var("--line")));
            tokens["BorderStrong"] = Frozen(Alpha(theme.Var("--line-strong")));
        }

        // Фон окна и свечения тем — как body в app.css FI.
        tokens["HeroGlow"] = Glow(theme.Var("--glow-a"), theme.Var("--glow-b"));
        var (glowTop, glowBottom, grid) = WindowBackdrop(theme.Id);
        tokens["BackdropTop"] = glowTop;
        tokens["BackdropBottom"] = glowBottom;
        tokens["BackdropGrid"] = grid;

        // У «Неона» главная кнопка светится, как .btn.primary в FI.
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 0,
            Color = Solid(accent, bg),
            Opacity = theme.Id == "neon" ? 0.35 : 0
        };
        shadow.Freeze();
        tokens["PrimaryShadow"] = shadow;

        // Главные кнопки: заливка, мягкие или контур; цвет — свой или акцента.
        var buttonBase = TryParseHex(look.ButtonColor, out var bc) ? bc : Solid(accent, bg);
        var buttonInk = theme.IsLight ? Mix(buttonBase, Colors.Black, 0.25) : Mix(buttonBase, Colors.White, 0.3);
        if (!TryParseHex(look.ButtonColor, out _)) buttonInk = Solid(accentInk, bg);
        var buttonOn = TryParseHex(look.ButtonColor, out _)
            ? (Luminance(buttonBase) > 0.6 ? Colors.Black : Colors.White)
            : Solid(onAccent, bg);
        Color WithA(Color c, double a) => Color.FromArgb((byte)Math.Round(255 * a), c.R, c.G, c.B);
        (Color Bg, Color Hover, Color Pressed, Color Fg, Color Border) primary = look.ButtonStyle switch
        {
            "soft" => (WithA(buttonBase, 0.16), WithA(buttonBase, 0.24), WithA(buttonBase, 0.32), buttonInk, WithA(buttonBase, 0.32)),
            "outline" => (Colors.Transparent, WithA(buttonBase, 0.10), WithA(buttonBase, 0.18), buttonInk, WithA(buttonBase, 0.65)),
            _ => (buttonBase, Mix(buttonBase, Luminance(buttonBase) > 0.6 ? Colors.Black : Colors.White, 0.12),
                  Mix(buttonBase, Colors.Black, 0.18), buttonOn, Colors.Transparent)
        };
        tokens["PrimaryBg"] = Frozen(primary.Bg);
        tokens["PrimaryHover"] = Frozen(primary.Hover);
        tokens["PrimaryPressed"] = Frozen(primary.Pressed);
        tokens["PrimaryFg"] = Frozen(primary.Fg);
        tokens["PrimaryBorder"] = Frozen(primary.Border);

        // Тени карточек: подложка под карточкой, а не рамка. Без теней ключа нет вовсе —
        // DynamicResource оставляет Effect пустым и ничего не стоит видеокарте.
        var shadowLevel = EffectiveShadows(look);
        if (shadowLevel != "none")
        {
            var cardShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = shadowLevel == "strong" ? 6 : 2,
                BlurRadius = shadowLevel == "strong" ? 28 : 14,
                Opacity = (shadowLevel == "strong" ? 0.38 : 0.18) * (theme.IsLight ? 0.6 : 1),
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            cardShadow.Freeze();
            tokens["CardShadow"] = cardShadow;
        }

        // Прозрачные карточки: сквозь них видны фон и анимация темы.
        if (look.CardTransparency > 0)
        {
            double keep = 1 - Math.Clamp(look.CardTransparency, 0, 60) / 100.0;
            foreach (var key in new[] { "BgSurface", "BgElevated" })
            {
                var c = ((SolidColorBrush)tokens[key]).Color;
                tokens[key] = Frozen(Color.FromArgb((byte)Math.Round(c.A * keep), c.R, c.G, c.B));
            }
        }

        // Углы: «Острые» и «Круглые» перекрывают значения темы, как data-radius в FI.
        (double lg, double md, double sm) = look.Radius switch
        {
            "sharp" => (4, 3, 2),
            "round" => (20, 14, 10),
            _ => (Num(theme.Var("--r-lg")), Num(theme.Var("--r-md")), Num(theme.Var("--r-sm")))
        };
        tokens["RadiusLg"] = new CornerRadius(lg);
        tokens["RadiusMd"] = new CornerRadius(md);
        tokens["RadiusSm"] = new CornerRadius(sm);

        // Шрифт: выбранный в оформлении или шрифт темы (у Ч/Б — моноширинный).
        var fontId = !string.IsNullOrEmpty(look.Font) ? look.Font
            : theme.Vars.TryGetValue("--ui", out var themeFont) ? themeFont : "";
        var family = Fonts.FirstOrDefault(f => f.Id == fontId).Family;
        if (!string.IsNullOrEmpty(family))
        {
            tokens["FontUi"] = new FontFamily(family);
            tokens["FontDisplay"] = new FontFamily(family);
        }
        else
        {
            tokens["FontUi"] = new FontFamily("Onest, Segoe UI Variable Text, Segoe UI");
            tokens["FontDisplay"] = new FontFamily("Onest, Segoe UI Variable Display, Segoe UI Semibold, Segoe UI");
        }

        // Сначала токены попадают в ресурсы приложения, потом грузятся стили, которые на них ссылаются.
        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(tokens);
        merged.Add(new ResourceDictionary
        {
            Source = new Uri("/Velocity;component/Theme/Controls.xaml", UriKind.Relative)
        });

        Current = look with { Theme = theme.Id };
    }

    /// <summary>
    /// Тема со своим цветом фона: поверхности, линии и текст выводятся из него, чтобы
    /// любой выбранный цвет давал согласованную палитру, а не одну перекрашенную подложку.
    /// </summary>
    private static FiTheme WithBackground(FiTheme theme, string? background)
    {
        if (!TryParseHex(background, out var bg)) return theme;
        bool light = Luminance(bg) > 0.55;
        var ink = light ? Parse("#16131A") : Parse("#F1EEF3");
        var vars = new Dictionary<string, string>(theme.Vars)
        {
            ["--bg"] = Hex(bg),
            ["--sunken"] = Hex(Mix(bg, light ? ink : Colors.Black, light ? 0.04 : 0.25)),
            ["--panel"] = Hex(Mix(bg, light ? Colors.White : ink, light ? 0.6 : 0.045)),
            ["--raised"] = Hex(Mix(bg, ink, light ? 0.03 : 0.08)),
            ["--hover"] = Hex(Mix(bg, ink, light ? 0.07 : 0.12)),
            ["--line"] = Hex(Mix(bg, ink, light ? 0.09 : 0.11)),
            ["--line-strong"] = Hex(Mix(bg, ink, light ? 0.16 : 0.19)),
            ["--seg-sel"] = Hex(Mix(bg, light ? Colors.White : ink, light ? 1 : 0.14)),
            ["--text"] = Hex(ink),
            ["--text-2"] = Hex(Mix(ink, bg, 0.35)),
            ["--text-3"] = Hex(Mix(ink, bg, 0.55)),
            ["--knob-off"] = Hex(Mix(ink, bg, 0.35)),
        };
        return new FiTheme
        {
            Id = theme.Id, Name = theme.Name, IsLight = light, Vars = vars,
            Anim = theme.Anim, Click = theme.Click, Shadows = theme.Shadows
        };
    }

    /// <summary>Свечение --glow из FI: радиальный градиент от центра к прозрачному краю.</summary>
    private static Brush Glow(string a, string b)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Alpha(a), 0),
                new GradientStop(Alpha(b), 0.6),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Фон окна у тем с декором: свечения «Аниме» и сетка «Неона», как body в FI.</summary>
    private static (Brush Top, Brush Bottom, Brush Grid) WindowBackdrop(string themeId)
    {
        Brush none = Brushes.Transparent;
        switch (themeId)
        {
            case "anime":
                return (Radial(Color.FromArgb(41, 255, 95, 168), new Point(1, 0), 1.2, 0.6, 0.6),
                        Radial(Color.FromArgb(36, 160, 120, 255), new Point(0, 1), 0.9, 0.5, 0.6),
                        none);
            case "neon":
                var lines = new DrawingBrush(new GeometryDrawing(
                    Frozen(Color.FromArgb(15, 0, 217, 245)), null, new RectangleGeometry(new Rect(0, 21, 22, 1))))
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 22, 22),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, 22, 22),
                    ViewboxUnits = BrushMappingMode.Absolute
                };
                lines.Freeze();
                return (Radial(Color.FromArgb(26, 255, 46, 136), new Point(0.5, 0), 0.8, 0.4, 0.7), none, lines);
            default:
                return (none, none, none);
        }
    }

    private static Brush Radial(Color color, Point center, double rx, double ry, double fadeAt)
    {
        var brush = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = rx,
            RadiusY = ry,
            GradientStops = { new GradientStop(color, 0), new GradientStop(Colors.Transparent, fadeAt) }
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Цвет CSS как есть, вместе с прозрачностью.</summary>
    public static Color Alpha(string css)
    {
        css = css.Trim();
        if (!css.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)) return Parse(css);
        var parts = css[(css.IndexOf('(') + 1)..css.IndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
        var a = double.Parse(parts[3], CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)Math.Round(a * 255), byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    }

    // ───────────────────────────── Оформление из FI ─────────────────────────────

    /// <summary>Файл, в котором FI хранит выбранное оформление.</summary>
    public static string FiLookFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FI", "tray.json");

    /// <summary>Оформление, выбранное в FI, или null, если FI не установлен.</summary>
    public static Look? ReadFiLook()
    {
        try
        {
            if (!File.Exists(FiLookFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FiLookFile));
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var theme = Str("theme");
            if (theme is null) return null;
            int scale = root.TryGetProperty("scale", out var s) && s.TryGetInt32(out var sv) ? sv : 0;
            bool motion = !root.TryGetProperty("motion", out var m) || m.ValueKind != JsonValueKind.False;
            return new Look(Migrate(theme), NullIfEmpty(Str("accent")), NullIfEmpty(Str("font")),
                NullIfEmpty(Str("radius")), scale, motion);
        }
        catch { return null; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ───────────────────────────── Цвет ─────────────────────────────

    /// <summary>
    /// Цвет CSS («#RRGGBB» или «rgba(r, g, b, a)») как сплошной: полупрозрачный смешивается с подложкой.
    /// </summary>
    public static Color Solid(string css, string under)
    {
        css = css.Trim();
        if (css.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
        {
            var parts = css[(css.IndexOf('(') + 1)..css.IndexOf(')')]
                .Split(',', StringSplitOptions.TrimEntries);
            var top = Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
            var alpha = double.Parse(parts[3], CultureInfo.InvariantCulture);
            return Mix(Solid(under, "#000000"), top, alpha);
        }
        return Parse(css);
    }

    /// <summary>Смешивание как mixRGB в FI: t = 0 — первый цвет, t = 1 — второй.</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    public static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
            return false;
        color = Color.FromRgb((byte)(n >> 16), (byte)((n >> 8) & 255), (byte)(n & 255));
        return true;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
