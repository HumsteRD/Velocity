using System.Runtime.Versioning;
using System.Windows;

namespace Velocity.App;

/// <summary>
/// Диалог обновления: предложить, скачать, поставить.
///
/// Вынесен отдельно, потому что вызывается из двух мест — автоматической проверки
/// при запуске и кнопки в настройках, — и логика подтверждения должна быть общей.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpdateFlow
{
    private static bool _busy;

    /// <summary>Проверка при запуске. Молчит, если обновлений нет или проверка выключена.</summary>
    public static async Task CheckOnStartupAsync(Window? owner)
    {
        var settings = AppSettings.Current;
        if (!settings.CheckUpdates) return;

        // Чаще раза в сутки дёргать GitHub незачем.
        if (settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromDays(1))
            return;

        var update = await Updater.CheckAsync();

        settings.LastUpdateCheck = DateTimeOffset.Now;
        settings.Save();

        if (update is null) return;

        // Версию, от которой пользователь уже отказался, второй раз не навязываем.
        if (settings.SkippedVersion == update.Version) return;

        await OfferAsync(update, owner);
    }

    public static async Task OfferAsync(UpdateInfo update, Window? owner)
    {
        if (_busy) return;

        var sizeMb = Math.Round(update.SizeBytes / 1024d / 1024d, 1);
        var notes = Summarize(update.Notes);

        var answer = MessageBox.Show(
            Loc.F("Доступна версия {0}. Установлена {1}.", update.Version, Updater.CurrentVersion) +
            $"{Environment.NewLine}{Environment.NewLine}{notes}" +
            Loc.F("{0}{1}Размер загрузки: {2} МБ.", Environment.NewLine, Environment.NewLine, sizeMb) +
            Loc.F("{0}{1}Скачать и установить?", Environment.NewLine, Environment.NewLine),
            Loc.T("Доступно обновление"), MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
        {
            // Запоминаем отказ, чтобы не спрашивать про эту же версию при каждом запуске.
            AppSettings.Current.SkippedVersion = update.Version;
            AppSettings.Current.Save();
            return;
        }

        await DownloadAndInstallAsync(update, owner);
    }

    private static async Task DownloadAndInstallAsync(UpdateInfo update, Window? owner)
    {
        _busy = true;
        var main = owner as Views.MainWindow;

        try
        {
            var progress = new Progress<double>(fraction =>
                main?.SetStatus(Loc.F("Загрузка обновления: {0:0}%", fraction * 100)));

            main?.SetStatus(Loc.T("Загрузка обновления…"));
            var installer = await Updater.DownloadAsync(update, progress);

            main?.SetStatus(Loc.T("Обновление загружено"));

            var answer = MessageBox.Show(
                Loc.F("Версия {0} загружена.", update.Version) +
                $"{Environment.NewLine}{Environment.NewLine}" +
                Loc.T("Программа закроется, установится обновление и запустится снова. ") +
                Loc.T("Применённые настройки и точки отката сохранятся.") +
                Loc.F("{0}{1}Установить сейчас?", Environment.NewLine, Environment.NewLine),
                Loc.T("Готово к установке"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                Updater.Install(installer);
                return;
            }

            main?.SetStatus(Loc.T("Обновление загружено и ждёт установки"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.F("Не удалось загрузить обновление: {0}", ex.Message) +
                $"{Environment.NewLine}{Environment.NewLine}" +
                Loc.T("Скачанная часть сохранена — следующая попытка продолжит с того же места. ") +
                Loc.F("Можно скачать установщик вручную: {0}", update.PageUrl),
                Loc.T("Ошибка загрузки"), MessageBoxButton.OK, MessageBoxImage.Warning);

            main?.SetStatus(Loc.T("Загрузка обновления не удалась"));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Достаёт из описания релиза несколько первых осмысленных строк.
    /// Целиком оно длинное и в окне сообщения нечитаемо.
    /// </summary>
    private static string Summarize(string notes)
    {
        var lines = notes
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("---") && !l.StartsWith('['))
            .Take(4)
            .ToList();

        return lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : Loc.T("Описание изменений доступно на странице релиза.");
    }
}
