using Velocity.Core;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Velocity.Tweaks.Actions;

/// <summary>
/// Запуск системных утилит. Список исполняемых файлов — БЕЛЫЙ и закрытый.
///
/// Это не паранойя: каталог твиков загружается с сервера, а значит подменённый
/// или скомпрометированный каталог не должен превращаться в запуск произвольного кода.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CommandExecutor
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "powercfg", "powercfg.exe",
        "bcdedit", "bcdedit.exe",
        "netsh", "netsh.exe",
        "sc", "sc.exe",
        "dism", "dism.exe",
        "fsutil", "fsutil.exe"
    };

    /// <summary>Символы, которыми можно склеить вторую команду через оболочку.</summary>
    private static readonly char[] Forbidden = ['&', '|', ';', '>', '<', '^', '`', '\n', '\r'];

    public static void Apply(CommandAction action)
    {
        if (!Allowed.Contains(action.Executable))
            throw new InvalidOperationException(
                L10n.F("Запуск «{0}» запрещён: утилиты нет в белом списке", action.Executable));

        if (action.Arguments.IndexOfAny(Forbidden) >= 0)
            throw new InvalidOperationException(L10n.T("В аргументах есть символы объединения команд"));

        var psi = new ProcessStartInfo(action.Executable, action.Arguments)
        {
            CreateNoWindow = true,
            // Обязательно false: через оболочку аргументы интерпретировались бы заново.
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(L10n.F("{0} не запустился", action.Executable));

        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);

        if (proc.HasExited && proc.ExitCode != 0)
            throw new InvalidOperationException(
                L10n.F("{0} вернул код {1}. {2}", action.Executable, proc.ExitCode, error.Trim()));
    }
}
