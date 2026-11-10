using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows;
using Velocity.Tweaks;

namespace Velocity.App;

/// <summary>
/// Повышение прав по требованию.
///
/// Программа стартует без прав администратора: скан системы, просмотр находок
/// и профили игр в них не нуждаются. Права нужны только части настроек — их
/// и запрашиваем, причём в тот момент, когда пользователь осознанно нажал «Применить».
/// </summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    public static bool IsElevated => TweakEngine.IsElevated();

    /// <summary>
    /// Предлагает перезапуститься с правами администратора.
    /// Возвращает true, если перезапуск начался и текущий экземпляр должен закрыться.
    /// </summary>
    public static bool RequestRestart(string why)
    {
        if (IsElevated) return false;

        var answer = MessageBox.Show(
            $"{why}\n\n" +
            Loc.T("Эти настройки хранятся в системной части реестра, и изменить их можно только ") +
            Loc.T("с правами администратора.\n\n") +
            Loc.T("Перезапустить программу с повышенными правами? Результаты скана сохранятся."),
            Loc.T("Нужны права администратора"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return false;

        return Restart();
    }

    public static bool Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;

            // Глагол runas — это и есть запрос UAC. Без UseShellExecute он не работает.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            });

            Application.Current.Shutdown();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Пользователь нажал «Нет» в окне UAC — это нормальный сценарий, не ошибка.
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Не удалось перезапустить программу: {0}", ex.Message), "VELOCITY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
