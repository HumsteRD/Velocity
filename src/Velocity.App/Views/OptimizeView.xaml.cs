using Velocity.Core;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Velocity.App.ViewModels;
using Velocity.Tweaks;

namespace Velocity.App.Views;

[SupportedOSPlatform("windows")]
public partial class OptimizeView : UserControl, ISearchable
{
    private string _query = "";
    private readonly ObservableCollection<TweakItem> _items = [];
    private TweakPreset? _preset = TweakPreset.Safe;
    private readonly TweakCategory? _category;
    private readonly string _sectionTitle;
    private readonly string? _sectionDescription;

    // XAML выставляет IsChecked у пресета по умолчанию ещё внутри InitializeComponent,
    // из-за чего Preset_Checked срабатывает до того, как созданы остальные элементы.
    // Флаг отсекает эти преждевременные вызовы.
    private bool _ready;

    private static readonly Dictionary<string, string> PresetHints = new()
    {
        ["Safe"] = Loc.T("Только обратимые настройки, которые не меняют поведение системы и не требуют перезагрузки. Подходит всем без исключения."),
        ["Gaming"] = Loc.T("Безопасный набор плюс настройки питания и фоновых процессов. Разумный выбор для большинства."),
        ["Competitive"] = Loc.T("Упор на задержку ввода и ровный frametime: мышь, клавиатура, приоритеты планировщика, настройки драйвера."),
        ["Maximum"] = Loc.T("Добавляются настройки, требующие перезагрузки. Эффект неоднозначный — после применения обязательно сравни ощущения."),
        ["Extreme"] = Loc.T("Включает отключение защитных механизмов Windows ради кадров. Каждый такой пункт отмечен как рискованный — читай описание до применения."),
        ["All"] = Loc.T("Полный каталог без фильтра. Здесь ты сам решаешь, что включать.")
    };

    public OptimizeView() : this(null, Loc.T("Оптимизация"), null) { }

    public OptimizeView(TweakCategory? category, string sectionTitle, string? sectionDescription)
    {
        _category = category;
        _sectionTitle = sectionTitle;
        _sectionDescription = sectionDescription;
        InitializeComponent();
        TitleText.Text = _sectionTitle;

        var source = (CollectionViewSource)Resources["GroupedTweaks"];
        source.Source = _items;

        // Подписку держим на паре Loaded/Unloaded, а не на конструкторе:
        // представления кэшируются в MainWindow, и после первого ухода со вкладки
        // подписка из конструктора терялась навсегда.
        Loaded += (_, _) => AppState.Current.ReportChanged += Reload;
        Unloaded += (_, _) => AppState.Current.ReportChanged -= Reload;

        _ready = true;
        // Подсказку к набору по умолчанию ставим сами: событие выбора сработало
        // ещё при разборе разметки, когда самой подсказки не было.
        PresetHint.Text = PresetHints["Safe"];

        if (AppState.Current.HasReport) Reload();
        UpdateSelectionSummary();
    }

    private void Reload()
    {
        if (!_ready) return;

        Dispatcher.Invoke(() =>
        {
            var state = AppState.Current;
            if (state.Engine is null || state.Facts is null) return;

            var pool = _preset is { } preset
                ? state.Catalog.ByPreset(preset)
                : state.Catalog.All.ToList();

            // Твики, не подходящие системе, в список не попадают вовсе.
            // Показывать неактивные строки «не для вашего железа» — шум.
            // Поиск идёт по всему каталогу, а не только по выбранному набору:
            // человек ищет конкретную настройку и не обязан помнить, в каком она наборе.
            var words = _query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0) pool = state.Catalog.All.ToList();

            var applicable = pool
                .Where(t => words.Length == 0 || words.All(w => Matches(t, w)))
                .Where(t => _category is null || t.Category == _category)
                .Where(t => state.Facts.MatchesAll(t.Requires))
                .ToList();

            _items.Clear();
            foreach (var tweak in applicable.OrderBy(t => t.Category).ThenBy(t => t.Risk))
            {
                var status = state.Engine.GetStatus(tweak);
                var item = new TweakItem
                {
                    Tweak = tweak,
                    State = status.State,
                    // По умолчанию отмечаем то, что ещё не применено: пользователь
                    // нажимает «Применить» и получает ожидаемый результат без лишних кликов.
                    IsSelected = !tweak.IsManual && status.State is TweakState.NotApplied or TweakState.Unknown
                };
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TweakItem.IsSelected)) UpdateSelectionSummary();
                };
                _items.Add(item);
            }

            var applied = _items.Count(i => i.State == TweakState.Applied);
            var summary = words.Length > 0
                ? Loc.F("Найдено по запросу «{0}»: {1}", _query, _items.Count)
                : $"{Loc.Plural(_items.Count, Loc.T("настройка"), Loc.T("настройки"), Loc.T("настроек"))} " +
                  Loc.F("для этой системы · уже применено: {0}", applied);

            EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (words.Length > 0)
            {
                EmptyTitle.Text = Loc.F("Ничего не найдено по запросу «{0}»", _query);
                EmptyHint.Text = Loc.T("Поиск идёт по названиям и описаниям всех настроек. Попробуйте другое слово, например «мышь», «сеть» или «питание». Esc — очистить.");
            }
            else
            {
                EmptyTitle.Text = Loc.T("В этом наборе нет подходящих настроек");
                EmptyHint.Text = Loc.T("Настройки, которые не подходят этой системе, скрыты. Выберите другой набор или категорию.");
            }
            SubtitleText.Text = _sectionDescription is null ? summary : $"{_sectionDescription} · {summary}";

            UpdateSelectionSummary();
        });
    }

    private static bool Matches(TweakDefinition tweak, string word) =>
        new[] { tweak.Title, tweak.Description, tweak.Effect, tweak.ExpectedGain, tweak.Id,
                Loc.T(tweak.Title), Loc.T(tweak.Description) }
            .Any(text => text?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true);

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AppState.Current.HasReport) Reload();
    }

    private void Search_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) SearchBox.Clear();
    }

    private void UpdateSelectionSummary()
    {
        if (!_ready) return;

        var selected = _items.Where(i => i.IsSelected && i.CanApply).ToList();
        var reboot = selected.Count(i => i.RequiresReboot);

        SelectionText.Text = selected.Count == 0
            ? Loc.T("Ничего не выбрано")
            : Loc.F("Выбрано: {0}", Loc.Plural(selected.Count, Loc.T("настройка"), Loc.T("настройки"), Loc.T("настроек")));

        SelectionHint.Text = reboot > 0
            ? Loc.F("{0} перезагрузки", Loc.Plural(reboot, Loc.T("настройка требует"), Loc.T("настройки требуют"), Loc.T("настроек требуют")))
            : Loc.T("Перед применением будет создана точка отката");

        ApplyButton.IsEnabled = selected.Count > 0;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;

        _preset = tag == "All" ? null : Enum.Parse<TweakPreset>(tag);
        if (PresetHint is not null) PresetHint.Text = PresetHints.GetValueOrDefault(tag, "");
        if (AppState.Current.HasReport) Reload();
    }

    /// <summary>
    /// Клик по строке раскрывает справку. Переключатель при этом не трогаем:
    /// он обрабатывает своё нажатие сам и до сюда событие не доводит.
    /// </summary>
    private void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TweakItem item })
            item.IsExpanded = !item.IsExpanded;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // Кнопка работает как переключатель: второе нажатие снимает выбор.
        bool anyUnselected = _items.Any(i => i.CanApply && !i.IsSelected);
        foreach (var item in _items.Where(i => i.CanApply)) item.IsSelected = anyUnselected;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Current;
        if (state.Engine is null) return;

        var selected = _items.Where(i => i.IsSelected && i.CanApply).Select(i => i.Tweak).ToList();
        if (selected.Count == 0) return;

        // Если среди выбранного есть настройки, требующие админа, а его нет —
        // предлагаем перезапуск ДО применения, иначе половина твиков молча пропустится.
        var needElevation = selected.Where(t => t.RequiresElevation).ToList();
        if (needElevation.Count > 0 && !Elevation.IsElevated)
        {
            var names = string.Join(Environment.NewLine,
                needElevation.Take(5).Select(t => "  • " + t.Title));
            if (needElevation.Count > 5)
                names += Loc.F("{0}  и ещё {1}", Environment.NewLine, needElevation.Count - 5);

            if (Elevation.RequestRestart(
                    Loc.F("Выбранным настройкам нужны права администратора:{0}{1}{2}", Environment.NewLine, Environment.NewLine, names)))
                return;

            var proceed = MessageBox.Show(
                Loc.F("Без прав администратора будет пропущено настроек: {0}.", needElevation.Count) +
                Loc.F("{0}{1}Применить остальные?", Environment.NewLine, Environment.NewLine),
                Loc.T("Часть настроек будет пропущена"), MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (proceed != MessageBoxResult.Yes) return;
        }

        var risky = selected.Where(t => t.Risk == TweakRisk.Expert).ToList();
        if (risky.Count > 0)
        {
            var names = string.Join("\n", risky.Select(t => "  • " + t.Title));
            var answer = MessageBox.Show(
                Loc.F("Среди выбранного есть настройки с риском:\n\n{0}\n\n", names) +
                Loc.T("Они снижают защиту системы или заметно повышают нагрев и потребление ради отклика. ") +
                Loc.T("Это осознанный размен — прочитай описание каждой. Откатить можно в любой момент.\n\n") +
                Loc.T("Применить?"),
                Loc.T("Подтверждение"), MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;
        }

        ApplyButton.IsEnabled = false;
        ApplyButton.Content = Loc.T("Применяю…");

        var session = await Task.Run(() => state.Engine.Apply(selected,
            new TweakEngine.ApplyOptions { Reason = Loc.F("Набор «{0}»", _preset?.ToString() ?? Loc.T("Все")) }));

        ApplyButton.Content = Loc.T("Применить");

        if (session.AbortReason is { } abort)
        {
            MessageBox.Show(abort, Loc.T("Применение отменено"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ApplyButton.IsEnabled = true;
            return;
        }

        ShowResult(session);

        // Именно пересканирование, а не пересчёт находок по старым данным:
        // состояние системы изменилось, и отчёт обязан это увидеть.
        await state.ScanAsync();
        Reload();
    }

    private static void ShowResult(TweakEngine.ApplySession session)
    {
        var applied = session.Results.Count(r => r.Outcome == ApplyOutcome.Applied);
        var failed = session.Results.Where(r => r.Outcome is ApplyOutcome.Failed or ApplyOutcome.VerifyFailed).ToList();
        var skipped = session.Results.Count(r => r.Outcome == ApplyOutcome.Skipped);

        var text = Loc.F("Применено: {0}", applied);
        if (skipped > 0) text += Loc.F("\nПропущено: {0}", skipped);

        if (failed.Count > 0)
        {
            text += Loc.F("\n\nНе удалось применить ({0}):\n", failed.Count) +
                    string.Join("\n", failed.Select(f => $"  • {f.Title} — {f.Message}"));
        }

        if (session.RebootRequired)
            text += Loc.T("\n\nЧасть настроек вступит в силу после перезагрузки.");

        if (session.SnapshotId is { } id)
            text += Loc.F("\n\nТочка отката: {0}\nОткатить всё можно в разделе «Точки отката».", id);

        MessageBox.Show(text, Loc.T("Готово"), MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
