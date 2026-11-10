using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velocity.Hardware.Bench;
using Velocity.Tweaks;
using Velocity.Tweaks.Actions;

namespace Velocity.App.Views;

public sealed record InfoRow(string Label, string Value);

public sealed class PingRow
{
    public required string Label { get; init; }
    public required string Address { get; init; }
    public required string Avg { get; init; }
    public required string Max { get; init; }
    public required string Jitter { get; init; }
    public required string Loss { get; init; }
    public required Brush JitterBrush { get; init; }
    public required Brush LossBrush { get; init; }
}

public sealed class DnsRow
{
    public required string Name { get; init; }
    public required string Details { get; init; }
    public required string Time { get; init; }
    public required Brush TimeBrush { get; init; }
    public bool IsBest { get; init; }
    public bool IsCurrent { get; init; }
    public bool CanApply { get; init; }
    public DnsProvider? Provider { get; init; }
}

[SupportedOSPlatform("windows")]
public partial class NetworkView : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private ConnectionInfo? _connection;

    public NetworkView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshConnection();
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    // ───────────────────────────── Подключение ─────────────────────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshConnection();

    private async void RefreshConnection()
    {
        var connections = await Task.Run(NetworkProbe.ActiveConnections);
        var physical = connections.Where(c => !NetworkProbe.IsVirtual(c.Description)).ToList();
        _connection = physical.FirstOrDefault() ?? connections.FirstOrDefault();

        if (_connection is null)
        {
            ConnectionRows.ItemsSource = null;
            NoConnection.Visibility = Visibility.Visible;
            WifiHint.Visibility = Visibility.Collapsed;
            VpnHint.Visibility = Visibility.Collapsed;
            ResetDnsButton.Visibility = Visibility.Collapsed;
            return;
        }

        NoConnection.Visibility = Visibility.Collapsed;
        var c = _connection;
        var staticDns = DnsExecutor.ReadStatic(c.Id);
        bool manualDns = !string.IsNullOrWhiteSpace(staticDns);

        var rows = new List<InfoRow>
        {
            new(Loc.T("Адаптер"), c.Description),
            new(Loc.T("Тип"), c.IsWireless ? "Wi-Fi" : Loc.T("Кабель (Ethernet)")),
            new(Loc.T("Скорость линии"), c.SpeedMbps > 0 ? Loc.F("{0:N0} Мбит/с", c.SpeedMbps).Replace(',', ' ') : Loc.T("неизвестно")),
            new(Loc.T("Адрес компьютера"), c.LocalAddress ?? "—"),
            new(Loc.T("Роутер (шлюз)"), c.Gateway ?? "—"),
            new("DNS", (c.DnsServers.Count == 0 ? "—" : string.Join(", ", c.DnsServers))
                       + (manualDns ? Loc.T(" · прописаны вручную") : Loc.T(" · от роутера")))
        };
        if (!c.IsWireless && c.SpeedMbps is > 0 and < 1000)
            rows.Add(new(Loc.T("Внимание"), Loc.F("Линия {0} Мбит/с вместо гигабита — проверь кабель (нужен Cat5e или лучше) и порт роутера", c.SpeedMbps)));
        ConnectionRows.ItemsSource = rows;

        WifiHint.Visibility = c.IsWireless ? Visibility.Visible : Visibility.Collapsed;
        ResetDnsButton.Visibility = manualDns ? Visibility.Visible : Visibility.Collapsed;

        var vpn = connections.Where(x => NetworkProbe.IsVirtual(x.Description)).ToList();
        var interceptors = await Task.Run(NetworkProbe.TrafficInterceptors);
        var notes = new List<string>();
        if (vpn.Count > 0)
            notes.Add(Loc.F("Активно виртуальное подключение: {0}.", string.Join(", ", vpn.Select(v => v.Description))));
        if (interceptors.Count > 0)
            notes.Add(Loc.F("Запущены программы, через которые идёт трафик: {0}.", string.Join(", ", interceptors)));
        if (notes.Count > 0)
            notes.Add(Loc.T("Они меняют пинг и маршрут, а некоторые сами отвечают на проверку связи — тогда замеры показывают их работу, а не сеть. Для честной проверки и для игры их лучше выключить."));
        VpnHint.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VpnHint.Foreground = Res("Warning");
        VpnHint.Text = string.Join(" ", notes);
    }

    // ───────────────────────────── Пинг ─────────────────────────────

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        PingButton.IsEnabled = false;
        PingButton.Content = Loc.T("Проверяю…");
        Main?.SetStatus(Loc.T("Проверка соединения: около 3 секунд"));

        try
        {
            var gateway = _connection?.Gateway is { } gw && IPAddress.TryParse(gw, out var gwAddress) ? gwAddress : null;
            var tasks = new List<Task<PingStats>>();
            if (gateway is not null) tasks.Add(NetworkProbe.PingAsync(Loc.T("Роутер"), gateway, 60, 40));
            tasks.Add(NetworkProbe.PingAsync("Cloudflare", IPAddress.Parse("1.1.1.1"), 60, 40));
            tasks.Add(NetworkProbe.PingAsync("Google", IPAddress.Parse("8.8.8.8"), 60, 40));
            var results = await Task.WhenAll(tasks);

            PingRows.ItemsSource = results.Select(ToRow).ToList();
            PingResults.Visibility = Visibility.Visible;
            ShowVerdict(gateway is null ? null : results[0], results.Skip(gateway is null ? 0 : 1).ToList());
            Main?.SetStatus(Loc.T("Проверка соединения завершена"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Проверка не удалась: {0}", ex.Message), "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PingButton.IsEnabled = true;
            PingButton.Content = Loc.T("Проверить соединение");
        }
    }

    private static PingRow ToRow(PingStats s)
    {
        bool none = s.Received == 0;
        return new PingRow
        {
            Label = s.Label,
            Address = s.Address,
            Avg = none ? Loc.T("нет ответа") : Loc.F("{0} мс", s.AvgMs.ToString("0.0", Ru)),
            Max = none ? "—" : Loc.F("{0} мс", s.MaxMs.ToString("0", Ru)),
            Jitter = none ? "—" : Loc.F("{0} мс", s.JitterMs.ToString("0.0", Ru)),
            Loss = $"{s.LossPercent.ToString("0.#", Ru)}%",
            JitterBrush = s.JitterMs > 5 ? Res("Warning") : Res("TextSecondary"),
            LossBrush = s.LossPercent > 0 ? Res("Critical") : Res("TextSecondary")
        };
    }

    private void ShowVerdict(PingStats? router, List<PingStats> internet)
    {
        VerdictLinks.Children.Clear();
        var reachable = internet.Where(i => i.Received > 0).ToList();
        double inetJitter = reachable.Count == 0 ? 0 : reachable.Average(i => i.JitterMs);
        double inetLoss = internet.Average(i => i.LossPercent);

        string title, text;
        string brush;

        if (internet.Any(i => NetworkProbe.LooksIntercepted(router, i)))
        {
            var who = NetworkProbe.TrafficInterceptors();
            title = Loc.T("Пинг до интернета недостоверен");
            text = Loc.T("Публичные серверы «ответили» быстрее, чем возможно физически, — на проверку связи ответила программа на этом компьютере") +
                   (who.Count > 0 ? $" ({string.Join(", ", who)})" : Loc.T(" (VPN или прокси в режиме TUN)")) +
                   Loc.T(". Цифры до роутера верные, до интернета — нет. Выключи её и повтори проверку.");
            brush = "WarningDim";
        }
        else if (reachable.Count == 0)
        {
            title = Loc.T("Публичные серверы не ответили");
            text = Loc.T("Нет ответа от 1.1.1.1 и 8.8.8.8. Возможно, провайдер или фаервол блокирует ping, либо нет доступа в интернет.");
            brush = "WarningDim";
        }
        else if (router is not null && (router.LossPercent > 0 || router.JitterMs > 3 || router.MaxMs > 20))
        {
            title = Loc.T("Проблема дома: соединение с роутером неровное");
            text = Loc.F("До роутера разброс {0} мс, максимум {1} мс, потери {2}%. ", router.JitterMs.ToString("0.0", Ru), router.MaxMs.ToString("0", Ru), router.LossPercent.ToString("0.#", Ru)) +
                   Loc.T("По кабелю здесь должно быть около 1 мс без потерь. Причина — Wi-Fi, кабель или роутер, а не провайдер и не настройки Windows.");
            brush = "WarningDim";
            AddGuideLink(Loc.T("Кабель вместо Wi-Fi"), "net.ethernet");
        }
        else if (inetLoss > 1 || inetJitter > 5)
        {
            title = Loc.T("До роутера ровно, проблема дальше");
            text = Loc.F("До интернета разброс {0} мс, потери {1}%. ", inetJitter.ToString("0.0", Ru), inetLoss.ToString("0.#", Ru)) +
                   Loc.T("Если в это время дома кто-то качал или смотрел видео — это bufferbloat, он лечится QoS на роутере. Если сеть была свободна — дело в провайдере или маршруте.");
            brush = "WarningDim";
            AddGuideLink(Loc.T("Bufferbloat и QoS"), "net.bufferbloat");
            AddGuideLink(Loc.T("Маршрут до сервера"), "net.routing");
        }
        else
        {
            title = Loc.T("Соединение ровное");
            text = Loc.T("Потерь нет, разброс в норме. Если в игре всё равно лагает — проверь пинг под нагрузкой (тест Bufferbloat) и сервер, к которому подключает игра.");
            brush = "BgInset";
            AddGuideLink(Loc.T("Тест под нагрузкой"), "net.bufferbloat");
        }

        VerdictTitle.Text = title;
        VerdictText.Text = text;
        VerdictPanel.Background = Res(brush);
    }

    private void AddGuideLink(string text, string guideId)
    {
        var button = new Button
        {
            Content = text,
            Tag = guideId,
            Style = (Style)FindResource("ButtonLink"),
            Margin = new Thickness(0, 0, 18, 0)
        };
        button.Click += Guide_Click;
        VerdictLinks.Children.Add(button);
    }

    // ───────────────────────────── DNS ─────────────────────────────

    private async void Dns_Click(object sender, RoutedEventArgs e)
    {
        DnsButton.IsEnabled = false;
        DnsButton.Content = Loc.T("Проверяю…");
        Main?.SetStatus(Loc.T("Замер DNS: несколько секунд"));

        try
        {
            var current = _connection?.DnsServers ?? [];
            var providers = new List<(DnsProvider Provider, bool IsCurrent)>();

            // Текущий сервер замеряем первым: с ним и сравниваем.
            if (current.Count > 0)
            {
                var known = NetworkProbe.PublicDns.FirstOrDefault(p => p.Servers.Contains(current[0]));
                providers.Add((known ?? new DnsProvider(Loc.T("Текущий DNS"), current[0], current.ElementAtOrDefault(1),
                    IsPrivate(current[0]) ? Loc.T("роутер или провайдер") : null), true));
            }
            foreach (var p in NetworkProbe.PublicDns)
                if (!providers.Any(x => x.Provider.Primary == p.Primary)) providers.Add((p, false));

            // Серверы опрашиваем одновременно: у каждого свой сокет, а недоступный
            // не должен заставлять ждать остальных.
            var measured = await Task.WhenAll(providers.Select(p => NetworkProbe.BenchDnsAsync(p.Provider, rounds: 2)));
            var results = measured.Select((r, i) => (Result: r, IsCurrent: providers[i].IsCurrent)).ToList();

            var best = results.Where(r => !r.Result.Failed).OrderBy(r => r.Result.MedianMs).FirstOrDefault();
            var currentResult = results.FirstOrDefault(r => r.IsCurrent);

            // Публичный DNS за полторы миллисекунды физически не отвечает: запросы
            // перехватывает программа на этом компьютере. Сравнивать и «применять» тогда нечего.
            bool intercepted = results.Any(r => !r.Result.Failed && r.Result.MedianMs < 1.5
                                                && NetworkProbe.PublicDns.Contains(r.Result.Provider));

            DnsRows.ItemsSource = results.Select(r => new DnsRow
            {
                Name = Loc.T(r.Result.Provider.Name),
                Details = string.Join(", ", r.Result.Provider.Servers)
                          + (r.Result.Provider.Note is { } note ? " · " + Loc.T(note) : "")
                          + (r.Result.Answered < r.Result.Asked && !r.Result.Failed
                              ? Loc.F(" · ответил на {0} из {1}", r.Result.Answered, r.Result.Asked) : ""),
                Time = r.Result.Failed ? Loc.T("нет ответа") : Loc.F("{0} мс", r.Result.MedianMs.ToString("0", Ru)),
                TimeBrush = r.Result.Failed ? Res("Critical")
                    : ReferenceEquals(r.Result, best.Result) ? Res("Success") : Res("TextSecondary"),
                IsBest = !intercepted && best.Result is not null && ReferenceEquals(r.Result, best.Result),
                IsCurrent = r.IsCurrent,
                CanApply = !intercepted && !r.IsCurrent && !r.Result.Failed && NetworkProbe.PublicDns.Contains(r.Result.Provider),
                Provider = r.Result.Provider
            }).ToList();

            DnsResults.Visibility = Visibility.Visible;
            var who = intercepted ? NetworkProbe.TrafficInterceptors() : [];
            DnsVerdict.Foreground = intercepted ? Res("Warning") : Res("TextSecondary");
            DnsVerdict.Text = intercepted
                ? Loc.T("Публичные DNS «ответили» за 0–1 мс — так быстро они физически не могут. DNS-запросы перехватывает программа на этом компьютере") +
                  (who.Count > 0 ? $" ({string.Join(", ", who)})" : Loc.T(" (VPN или прокси)")) +
                  Loc.T(", поэтому сравнение недостоверно и менять DNS сейчас бессмысленно. Выключи её и повтори проверку.")
                : best.Result is null
                ? Loc.T("Ни один сервер не ответил — похоже, провайдер блокирует сторонние DNS-запросы.")
                : currentResult.Result is null || currentResult.Result.Failed
                    ? Loc.F("Быстрее всех — {0}.", best.Result.Provider.Name)
                    : best.IsCurrent || currentResult.Result.MedianMs - best.Result.MedianMs < 10
                        ? Loc.T("Текущий DNS и так среди самых быстрых — менять незачем.")
                        : Loc.F("{0} быстрее текущего на {1} мс. Разница заметна только при открытии сайтов и входе в лаунчеры.", best.Result.Provider.Name, (currentResult.Result.MedianMs - best.Result.MedianMs).ToString("0", Ru));
            Main?.SetStatus(Loc.T("Замер DNS завершён"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("Замер DNS не удался: {0}", ex.Message), "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            DnsButton.IsEnabled = true;
            DnsButton.Content = Loc.T("Проверить DNS");
        }
    }

    private static bool IsPrivate(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.GetAddressBytes() is var b
        && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));

    private void ApplyDns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DnsRow { Provider: { } provider } }) return;
        ApplyDns(provider.Servers, $"DNS: {provider.Name}");
    }

    private void ResetDns_Click(object sender, RoutedEventArgs e)
        => ApplyDns([], Loc.T("DNS: автоматически от роутера"));

    /// <summary>
    /// DNS меняется через общий движок: снимок прежних серверов пишется в точку отката,
    /// и вернуть всё можно из раздела «Точки отката».
    /// </summary>
    private async void ApplyDns(List<string> servers, string title)
    {
        var state = AppState.Current;
        if (state.Engine is null) return;

        if (!Elevation.IsElevated
            && Elevation.RequestRestart(Loc.T("Смена DNS-серверов адаптера требует прав администратора.")))
            return;
        if (!Elevation.IsElevated) return;

        var tweak = new TweakDefinition
        {
            Id = "net.dns.custom",
            Category = TweakCategory.Network,
            Title = title,
            Description = Loc.T("DNS-серверы физических сетевых адаптеров"),
            RequiresElevation = true,
            RequiresGameClosed = false,
            Apply = [new DnsAction { Servers = servers }]
        };

        var session = await Task.Run(() => state.Engine.Apply([tweak],
            new TweakEngine.ApplyOptions { Reason = title, IgnoreAntiCheat = true }));
        var result = session.Results.FirstOrDefault();

        if (result?.Outcome == ApplyOutcome.Applied)
        {
            MessageBox.Show(Loc.F("{0} — применено.\n\nВернуть прежние серверы можно в разделе «Точки отката»", title) +
                            (session.SnapshotId is { } id ? Loc.F(" (точка {0}).", id) : "."),
                Loc.T("Готово"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(Loc.F("Не удалось изменить DNS: {0}", result?.Message ?? session.AbortReason ?? Loc.T("неизвестная ошибка")),
                "VELOCITY", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshConnection();
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) Main?.OpenGuide(id);
    }

    private static Brush Res(string key) => Application.Current.Resources[key] as Brush ?? Brushes.Gray;
}
