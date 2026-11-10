using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Velocity.App;

/// <summary>
/// Схемы к руководствам. Рисуются векторно в цветах текущей темы, поэтому одинаково
/// читаются в светлом и тёмном оформлении и не устаревают, как скриншоты чужих BIOS.
/// </summary>
public static class GuideFigures
{
    /// <summary>Какая схема к какому руководству.</summary>
    private static readonly Dictionary<string, string> ByGuide = new()
    {
        ["manual.memory_channels"] = "ram_slots",
        ["manual.xmp_expo"] = "bios_keys",
        ["manual.resizable_bar"] = "bios_keys",
        ["bios.update"] = "bios_keys",
        ["bios.cstates"] = "bios_keys",
        ["bios.ram_timings"] = "ram_slots",
        ["manual.monitor_cable"] = "monitor_cable",
        ["hw.monitor"] = "monitor_cable",
        ["net.bufferbloat"] = "bufferbloat",
        ["bios.x3d_dual_ccd"] = "x3d_ccd",
        ["bios.pcie"] = "pcie_slots",
    };

    public static UIElement? For(string guideId, FrameworkElement res) =>
        ByGuide.TryGetValue(guideId, out var id) ? Create(id, res) : null;

    public static UIElement? Create(string id, FrameworkElement res) => id switch
    {
        "ram_slots" => RamSlots(res),
        "bios_keys" => BiosKeys(res),
        "monitor_cable" => MonitorCable(res),
        "bufferbloat" => Bufferbloat(res),
        "x3d_ccd" => X3d(res),
        "pcie_slots" => PcieSlots(res),
        _ => null
    };

    // ───────────────────────────── Слоты памяти ─────────────────────────────

    private static UIElement RamSlots(FrameworkElement r)
    {
        var canvas = new Canvas { Width = 560, Height = 170 };
        Box(canvas, r, 20, 40, 120, 90, Loc.T("Процессор"), "BgElevated", "BorderStrong");
        string[] labels = ["A1", "A2", "B1", "B2"];
        for (int i = 0; i < 4; i++)
        {
            bool use = i is 1 or 3;
            double x = 180 + i * 60;
            var slot = new Rectangle
            {
                Width = 26, Height = 130, RadiusX = 3, RadiusY = 3,
                Fill = B(r, use ? "AccentTint" : "BgInset"),
                Stroke = B(r, use ? "Accent" : "BorderStrong"),
                StrokeThickness = use ? 2 : 1
            };
            Place(canvas, slot, x, 20);
            Label(canvas, r, labels[i], x - 2, 155, use ? "AccentInk" : "TextTertiary", 12, true);
            if (use) Label(canvas, r, (i == 1 ? "2" : "4"), x + 8, 75, "AccentInk", 15, true);
        }
        Label(canvas, r, Loc.T("Две планки — во 2-й и 4-й слоты, считая от процессора."), 430, 52, "TextPrimary", 12.5, true, 125);
        Label(canvas, r, Loc.T("Обычно это A2 и B2, слоты одного цвета."), 430, 100, "TextSecondary", 12, false, 125);
        return Frame(r, canvas, Loc.T("Схема: двухканальный режим"));
    }

    // ───────────────────────────── Клавиши BIOS ─────────────────────────────

    private static UIElement BiosKeys(FrameworkElement r) => Frame(r, Table(r,
        [Loc.T("Плата"), Loc.T("Вход в BIOS"), Loc.T("Профиль памяти"), Loc.T("Resizable BAR"), Loc.T("Расширенный режим")],
        [
            ["ASUS", "Del / F2", "Ai Tweaker → Ai Overclock Tuner → XMP I", "Advanced → PCI Subsystem Settings", "F7"],
            ["MSI", "Del", "OC → Extreme Memory Profile (XMP)", "Settings → Advanced → PCIe/PCI Sub-system", "F7"],
            ["Gigabyte", "Del", "Tweaker → Extreme Memory Profile (X.M.P.)", "Settings → IO Ports", "F2"],
            ["ASRock", "F2 / Del", "OC Tweaker → DRAM Configuration → XMP", "Advanced → Chipset Configuration", "F6"],
        ]), Loc.T("Где что лежит у популярных производителей. Сохранение и выход — F10."));

    // ───────────────────────────── Кабели мониторов ─────────────────────────────

    private static UIElement MonitorCable(FrameworkElement r) => Frame(r, Table(r,
        [Loc.T("Разъём и версия"), "1920×1080", "2560×1440", "3840×2160"],
        [
            ["HDMI 1.4", "120 Hz", "60 Hz", "30 Hz"],
            ["HDMI 2.0", "240 Hz", "144 Hz", "60 Hz"],
            ["HDMI 2.1", "360 Hz", "240 Hz", "144 Hz"],
            ["DisplayPort 1.2", "240 Hz", "165 Hz", "60 Hz"],
            ["DisplayPort 1.4", "360 Hz", "240 Hz", "120 Hz"],
        ], highlightRow: 4), Loc.T("Максимальная частота без сжатия DSC. Со сжатием — выше. Если в списке режимов нет частоты монитора — дело в кабеле или разъёме."));

    // ───────────────────────────── Bufferbloat ─────────────────────────────

    private static UIElement Bufferbloat(FrameworkElement r)
    {
        var canvas = new Canvas { Width = 600, Height = 190 };
        Row(10, Loc.T("Без QoS"), 9, "Critical", Loc.T("пинг 20 → 200 мс"));
        Row(105, Loc.T("С SQM / QoS"), 2, "Success", Loc.T("пинг 20 → 25 мс"));
        return Frame(r, canvas, Loc.T("Очередь пакетов в роутере, когда дома кто-то качает"));

        void Row(double y, string title, int queued, string tone, string result)
        {
            Label(canvas, r, title, 0, y, "TextSecondary", 12, true);
            Box(canvas, r, 0, y + 22, 70, 46, Loc.T("ПК"), "BgElevated", "BorderStrong");
            Arrow(canvas, r, 74, y + 45, 110);
            var router = new Rectangle { Width = 250, Height = 46, RadiusX = 6, RadiusY = 6, Fill = B(r, "BgInset"), Stroke = B(r, "BorderStrong") };
            Place(canvas, router, 114, y + 22);
            for (int i = 0; i < queued; i++)
            {
                var packet = new Rectangle { Width = 20, Height = 22, RadiusX = 3, RadiusY = 3, Fill = B(r, i == queued - 1 ? "Accent" : "BorderStrong") };
                Place(canvas, packet, 124 + i * 25, y + 34);
            }
            Label(canvas, r, Loc.T("очередь"), 300, y + 4, "TextTertiary", 11, false);
            Arrow(canvas, r, 368, y + 45, 404);
            Box(canvas, r, 408, y + 22, 80, 46, Loc.T("Интернет"), "BgElevated", "BorderStrong");
            Label(canvas, r, result, 500, y + 36, tone, 12.5, true);
        }
    }

    // ───────────────────────────── X3D ─────────────────────────────

    private static UIElement X3d(FrameworkElement r)
    {
        var canvas = new Canvas { Width = 560, Height = 170 };
        var ccd0 = new Rectangle { Width = 200, Height = 110, RadiusX = 8, RadiusY = 8, Fill = B(r, "AccentTint"), Stroke = B(r, "Accent"), StrokeThickness = 2 };
        Place(canvas, ccd0, 20, 40);
        Label(canvas, r, "CCD 0", 36, 52, "AccentInk", 13, true);
        Label(canvas, r, Loc.T("+ 3D V-Cache"), 36, 74, "AccentInk", 12, false);
        Label(canvas, r, Loc.T("игра"), 36, 112, "TextPrimary", 13, true);

        var ccd1 = new Rectangle { Width = 200, Height = 110, RadiusX = 8, RadiusY = 8, Fill = B(r, "BgInset"), Stroke = B(r, "BorderStrong") };
        Place(canvas, ccd1, 250, 40);
        Label(canvas, r, "CCD 1", 266, 52, "TextSecondary", 13, true);
        Label(canvas, r, Loc.T("ядра «припаркованы»"), 266, 74, "TextTertiary", 12, false);
        Label(canvas, r, Loc.T("фон, браузер"), 266, 112, "TextSecondary", 13, true);

        Label(canvas, r, Loc.T("Game Bar сообщает драйверу чипсета, что запущена игра, — драйвер держит её на чиплете с кэшем."), 470, 46, "TextSecondary", 11.5, false, 90);
        return Frame(r, canvas, Loc.T("Как Ryzen X3D с двумя чиплетами распределяет игру"));
    }

    // ───────────────────────────── PCIe ─────────────────────────────

    private static UIElement PcieSlots(FrameworkElement r)
    {
        var canvas = new Canvas { Width = 560, Height = 170 };
        Box(canvas, r, 20, 20, 110, 70, Loc.T("Процессор"), "BgElevated", "BorderStrong");
        var top = new Rectangle { Width = 300, Height = 18, RadiusX = 3, RadiusY = 3, Fill = B(r, "AccentTint"), Stroke = B(r, "Accent"), StrokeThickness = 2 };
        Place(canvas, top, 160, 46);
        Label(canvas, r, Loc.T("PCIe x16 от процессора — сюда видеокарту"), 160, 24, "AccentInk", 12.5, true);
        var bottom = new Rectangle { Width = 300, Height = 18, RadiusX = 3, RadiusY = 3, Fill = B(r, "BgInset"), Stroke = B(r, "BorderStrong") };
        Place(canvas, bottom, 160, 120);
        Label(canvas, r, Loc.T("Нижний «x16» — часто x4 от чипсета"), 160, 98, "TextTertiary", 12, false);
        Arrow(canvas, r, 132, 55, 156);
        return Frame(r, canvas, Loc.T("Слоты PCIe: проверь, что карта стоит в верхнем"));
    }

    // ───────────────────────────── Общее ─────────────────────────────

    private static Brush B(FrameworkElement r, string key) => (Brush)r.FindResource(key);

    private static void Place(Canvas canvas, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    private static void Label(Canvas canvas, FrameworkElement r, string text, double x, double y, string brush,
        double size, bool bold, double width = double.NaN)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = B(r, brush),
            FontFamily = (FontFamily)r.FindResource("FontUi"),
            TextWrapping = double.IsNaN(width) ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Width = width
        };
        Place(canvas, block, x, y);
    }

    private static void Box(Canvas canvas, FrameworkElement r, double x, double y, double w, double h, string text, string fill, string stroke)
    {
        var box = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(6),
            Background = B(r, fill), BorderBrush = B(r, stroke), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(r, "TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = (FontFamily)r.FindResource("FontUi")
            }
        };
        Place(canvas, box, x, y);
    }

    private static void Arrow(Canvas canvas, FrameworkElement r, double x1, double y, double x2)
    {
        canvas.Children.Add(new Line { X1 = x1, X2 = x2 - 6, Y1 = y, Y2 = y, Stroke = B(r, "TextTertiary"), StrokeThickness = 1.5 });
        canvas.Children.Add(new Polygon
        {
            Points = [new Point(x2, y), new Point(x2 - 7, y - 4), new Point(x2 - 7, y + 4)],
            Fill = B(r, "TextTertiary")
        });
    }

    private static UIElement Table(FrameworkElement r, string[] header, string[][] rows, int highlightRow = -1)
    {
        var grid = new Grid();
        for (int i = 0; i < header.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row <= rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = row == 0 ? header : rows[row - 1];
            for (int c = 0; c < cells.Length; c++)
            {
                bool head = row == 0, hi = row - 1 == highlightRow;
                var cell = new Border
                {
                    BorderBrush = B(r, "BorderSubtle"),
                    BorderThickness = new Thickness(0, 0, 0, row == rows.Length ? 0 : 1),
                    Background = hi ? B(r, "AccentTint") : Brushes.Transparent,
                    Padding = new Thickness(10, 7, 14, 7),
                    Child = new TextBlock
                    {
                        Text = cells[c],
                        FontSize = head ? 11.5 : 12.5,
                        FontWeight = head || c == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = B(r, head ? "TextTertiary" : c == 0 ? "TextPrimary" : "TextSecondary"),
                        FontFamily = (FontFamily)r.FindResource("FontUi"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    private static UIElement Frame(FrameworkElement r, UIElement content, string caption)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Style = (Style)r.FindResource("TextLabel"),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });
        // Схемы масштабируются под ширину, таблицы — переносят текст: мелкий шрифт в таблице не читается.
        panel.Children.Add(content is Canvas
            ? new Viewbox { Child = content, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left }
            : content);
        return new Border
        {
            Background = B(r, "BgInset"),
            BorderBrush = B(r, "BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)r.FindResource("RadiusMd"),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 16, 0, 0),
            Child = panel
        };
    }
}
