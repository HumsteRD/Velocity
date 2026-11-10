using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

namespace Velocity.Hardware.Bench;

public sealed record PingStats(string Label, string Address, int Sent, int Received,
    double AvgMs, double MinMs, double MaxMs, double JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
}

public sealed record ConnectionInfo(
    string Id,
    string Name,
    string Description,
    bool IsWireless,
    long SpeedMbps,
    string? LocalAddress,
    string? Gateway,
    List<string> DnsServers);

public sealed record DnsProvider(string Name, string Primary, string? Secondary, string? Note = null)
{
    public List<string> Servers => Secondary is null ? [Primary] : [Primary, Secondary];
}

public sealed record DnsBenchResult(DnsProvider Provider, double MedianMs, int Answered, int Asked)
{
    public bool Failed => Answered == 0;
}

/// <summary>
/// Сетевая диагностика без сторонних утилит: активные подключения, пинг с разбросом
/// и потерями, скорость ответа DNS-серверов.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetworkProbe
{
    /// <summary>Публичные DNS, которые имеет смысл сравнивать с провайдерским.</summary>
    public static readonly IReadOnlyList<DnsProvider> PublicDns =
    [
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("Quad9", "9.9.9.9", "149.112.112.112", "блокирует известные вредоносные сайты"),
        new("Яндекс", "77.88.8.8", "77.88.8.1"),
        new("AdGuard", "94.140.14.14", "94.140.15.15", "блокирует рекламу и трекеры"),
    ];

    /// <summary>Домены, которые реально запрашивают лаунчеры и античиты.</summary>
    private static readonly string[] Domains =
    [
        "steamcommunity.com", "store.steampowered.com", "riotgames.com", "epicgames.com",
        "faceit.com", "ea.com", "discord.com", "microsoft.com"
    ];

    // ───────────────────────────── Подключения ─────────────────────────────

    /// <summary>Подключения, через которые реально идёт трафик: поднятые, с IPv4 и шлюзом.</summary>
    public static List<ConnectionInfo> ActiveConnections()
    {
        var result = new List<ConnectionInfo>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return result; }

        foreach (var nic in all)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gateway is null) continue;

                var local = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                var dns = props.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString()).ToList();

                result.Add(new ConnectionInfo(
                    nic.Id, nic.Name, nic.Description,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    nic.Speed > 0 ? nic.Speed / 1_000_000 : 0,
                    local?.ToString(), gateway.ToString(), dns));
            }
            catch { }
        }

        // Сначала физические: у виртуальных адаптеров VPN шлюз тоже бывает.
        return [.. result.OrderBy(c => IsVirtual(c.Description))];
    }

    public static IPAddress? DefaultGateway()
    {
        var gw = ActiveConnections().FirstOrDefault()?.Gateway;
        return gw is not null && IPAddress.TryParse(gw, out var address) ? address : null;
    }

    public static bool IsVirtual(string description) =>
        System.Text.RegularExpressions.Regex.IsMatch(description,
            "virtual|vpn|tap-|tap |wintun|wireguard|hyper-v|vmware|virtualbox|radmin|hamachi|zerotier|tailscale|openvpn|loopback|npcap",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Программы, которые пропускают трафик через себя и искажают замеры сети.</summary>
    private static readonly (string Process, string Title)[] Interceptors =
    [
        ("xray", "Xray (VPN/прокси)"),
        ("sing-box", "sing-box (VPN/прокси)"),
        ("HiddifyCli", "Hiddify"),
        ("Hiddify", "Hiddify"),
        ("v2ray", "V2Ray"),
        ("clash", "Clash"),
        ("mihomo", "Mihomo (Clash)"),
        ("nekoray", "NekoRay"),
        ("winws", "zapret (обход блокировок)"),
        ("goodbyedpi", "GoodbyeDPI"),
        ("AmneziaVPN", "AmneziaVPN"),
        ("openvpn", "OpenVPN"),
        ("wireguard", "WireGuard"),
        ("ExitLag", "ExitLag"),
        ("GearUP Booster", "GearUP"),
        ("NoPing", "NoPing"),
    ];

    /// <summary>
    /// Запущенные VPN, прокси и фильтры трафика. Некоторые из них (Xray, sing-box в режиме TUN)
    /// сами отвечают на ping вместо удалённого сервера — тогда пинг «до интернета» получается
    /// нулевым и ничего не говорит о реальной сети.
    /// </summary>
    public static List<string> TrafficInterceptors()
    {
        var found = new List<string>();
        System.Diagnostics.Process[] all;
        try { all = System.Diagnostics.Process.GetProcesses(); }
        catch { return found; }

        foreach (var p in all)
        {
            try
            {
                foreach (var (name, title) in Interceptors)
                    if (p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) && !found.Contains(title))
                        found.Add(title);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return found;
    }

    /// <summary>
    /// Ответ из интернета быстрее 1,5 мс или быстрее роутера — физически невозможен для домашней
    /// линии: значит, на ping ответил кто-то на этом же компьютере (VPN в режиме TUN, фильтр).
    /// </summary>
    public static bool LooksIntercepted(PingStats? router, PingStats internet) =>
        internet.Received > 0
        && (internet.AvgMs < 1.5 || (router is { Received: > 0 } && internet.AvgMs < router.AvgMs));

    // ───────────────────────────── Пинг ─────────────────────────────

    public static async Task<PingStats> PingAsync(string label, IPAddress address, int count, int intervalMs,
        CancellationToken ct = default)
    {
        using var ping = new Ping();
        var times = new List<double>();
        int sent = 0;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            sent++;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                var reply = await ping.SendPingAsync(address, 1000);
                if (reply.Status == IPStatus.Success)
                {
                    // RoundtripTime округлён до целых миллисекунд: для роутера по кабелю
                    // это всегда 0. Собственный замер включает доли миллисекунды накладных
                    // расходов, но одинаковых для всех замеров, — для разброса это важнее.
                    times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
            }
            catch (PingException) { }

            var wait = intervalMs - (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct);
        }

        if (times.Count == 0)
            return new PingStats(label, address.ToString(), sent, 0, 0, 0, 0, 0);

        // Разброс (jitter) — средняя разница между соседними замерами, как в RFC 3550.
        double jitter = 0;
        for (int i = 1; i < times.Count; i++) jitter += Math.Abs(times[i] - times[i - 1]);
        jitter = times.Count > 1 ? jitter / (times.Count - 1) : 0;

        return new PingStats(label, address.ToString(), sent, times.Count,
            times.Average(), times.Min(), times.Max(), jitter);
    }

    // ───────────────────────────── DNS ─────────────────────────────

    /// <summary>
    /// Время ответа DNS-сервера: несколько кругов запросов типа A по популярным доменам,
    /// результат — медиана. Запросы шлём сами по UDP, а не через системный резолвер:
    /// у системного есть свой кэш, и сравнивать серверы он не даёт.
    /// </summary>
    public static async Task<DnsBenchResult> BenchDnsAsync(DnsProvider provider, int rounds = 3,
        CancellationToken ct = default)
    {
        var times = new List<double>();
        int asked = 0, failuresInRow = 0;
        var server = IPAddress.Parse(provider.Primary);

        using var udp = new UdpClient(server.AddressFamily);
        udp.Connect(server, 53);

        for (int round = 0; round < rounds; round++)
        {
            foreach (var domain in Domains)
            {
                ct.ThrowIfCancellationRequested();
                // Сервер молчит три раза подряд — недоступен, ждать таймаут на каждом домене незачем.
                if (failuresInRow >= 3 && times.Count == 0) break;
                asked++;
                var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
                var query = BuildQuery(id, domain);

                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    await udp.SendAsync(query, ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(1000);

                    while (true)
                    {
                        var response = await udp.ReceiveAsync(timeout.Token);
                        if (response.Buffer.Length >= 12
                            && BinaryPrimitives.ReadUInt16BigEndian(response.Buffer) == id)
                        {
                            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                            failuresInRow = 0;
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failuresInRow++; }
                catch (SocketException) { failuresInRow++; }
            }
        }

        times.Sort();
        var median = times.Count == 0 ? 0 : times[times.Count / 2];
        return new DnsBenchResult(provider, median, times.Count, asked);
    }

    /// <summary>Минимальный DNS-запрос: заголовок с флагом рекурсии и один вопрос типа A.</summary>
    public static byte[] BuildQuery(ushort id, string domain)
    {
        var buffer = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // RD = 1
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // QDCOUNT
        buffer.AddRange(header.ToArray());

        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }
        buffer.Add(0);
        buffer.AddRange([0, 1, 0, 1]); // QTYPE = A, QCLASS = IN
        return [.. buffer];
    }
}
