using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Velocity.App;

/// <summary>
/// Разметка текста шагов руководства без отдельного формата:
/// путь по меню «BIOS → Advanced → AMD CBS» превращается в цепочку плашек,
/// значения (Disabled, Enabled, Auto, 0.050 V) и названия в «ёлочках» выделяются.
/// Так шаг читается с одного взгляда, а руководства остаются обычными строками в JSON.
/// </summary>
public static partial class GuideText
{
    public static readonly DependencyProperty RichProperty = DependencyProperty.RegisterAttached(
        "Rich", typeof(string), typeof(GuideText), new PropertyMetadata(null, OnRichChanged));

    public static void SetRich(DependencyObject element, string? value) => element.SetValue(RichProperty, value);
    public static string? GetRich(DependencyObject element) => (string?)element.GetValue(RichProperty);

    private static void OnRichChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not string text || text.Length == 0) return;
        foreach (var inline in Build(text, block)) block.Inlines.Add(inline);
    }

    private static IEnumerable<Inline> Build(string text, FrameworkElement owner)
    {
        var parts = Arrow().Split(text);
        if (parts.Length == 1)
        {
            foreach (var run in Emphasize(text, owner)) yield return run;
            yield break;
        }

        // Начало пути — последняя фраза первой части; конец — первая фраза последней.
        var head = parts[0];
        int cut = LastBoundary(head);
        foreach (var run in Emphasize(head[..cut], owner)) yield return run;

        var chain = new List<string> { head[cut..].Trim() };
        for (int i = 1; i < parts.Length - 1; i++) chain.Add(parts[i].Trim());
        var tail = parts[^1];
        int end = FirstBoundary(tail);
        chain.Add(tail[..end].Trim());

        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0) yield return Separator(owner);
            // Плашка — только короткому пункту меню; фраза вроде «открой страницу своей платы» остаётся текстом.
            if (IsMenuItem(chain[i])) yield return Chip(chain[i], owner, last: i == chain.Count - 1);
            else foreach (var run in Emphasize(chain[i], owner)) yield return run;
        }
        foreach (var run in Emphasize(tail[end..], owner)) yield return run;
    }

    private static bool IsMenuItem(string text) =>
        text.Length is > 0 and <= 40 && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5;

    /// <summary>Конец предыдущей фразы перед началом пути: «… плате. BIOS → …», «: », «— ».</summary>
    private static int LastBoundary(string s)
    {
        int best = 0;
        foreach (var mark in new[] { ". ", ": ", "; ", " — ", "! " })
        {
            int i = s.LastIndexOf(mark, StringComparison.Ordinal);
            if (i >= 0 && i + mark.Length > best) best = i + mark.Length;
        }
        return best;
    }

    /// <summary>Конец пути: точка, точка с запятой, скобка, запятая, тире.</summary>
    private static int FirstBoundary(string s)
    {
        int best = s.Length;
        foreach (var mark in new[] { ". ", "; ", " (", ", ", " — ", ": " })
        {
            int i = s.IndexOf(mark, StringComparison.Ordinal);
            if (i >= 0 && i < best) best = i;
        }
        if (best == s.Length && s.EndsWith('.')) best = s.Length - 1;
        return best;
    }

    private static Inline Chip(string text, FrameworkElement owner, bool last)
    {
        var border = new Border
        {
            Background = (Brush)owner.FindResource(last ? "AccentTint" : "BgInset"),
            BorderBrush = (Brush)owner.FindResource(last ? "AccentLine" : "BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 1, 0, 1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)owner.FindResource(last ? "AccentInk" : "TextPrimary")
            }
        };
        return new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static Inline Separator(FrameworkElement owner) => new Run(" › ")
    {
        Foreground = (Brush)owner.FindResource("TextTertiary"),
        FontWeight = FontWeights.SemiBold
    };

    /// <summary>Выделяет «названия» и значения настроек.</summary>
    private static IEnumerable<Inline> Emphasize(string text, FrameworkElement owner)
    {
        int pos = 0;
        foreach (Match m in Highlight().Matches(text))
        {
            if (m.Index > pos) yield return new Run(text[pos..m.Index]);
            yield return new Run(m.Value)
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)owner.FindResource(m.Value.StartsWith('«') ? "TextPrimary" : "AccentInk")
            };
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) yield return new Run(text[pos..]);
    }

    [GeneratedRegex(@"\s→\s")]
    private static partial Regex Arrow();

    [GeneratedRegex(@"«[^»]{1,60}»|\b(?:Disabled|Enabled|Auto|Negative|Off|On|Typical Current Idle|Profile 1|XMP 1|EXPO 1|Gen4|Gen3)\b|\b\d+(?:[.,]\d+)?\s?(?:V|мВ|mV|МГц|MHz|Гц|Hz|ГБ|GB|мс|ms|°C)(?![\w])")]
    private static partial Regex Highlight();
}
