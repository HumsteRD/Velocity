using System.Windows;

namespace Velocity.App;

/// <summary>
/// Иконка пункта меню — символ шрифта Segoe Fluent Icons (на Windows 10 — Segoe MDL2 Assets,
/// коды символов у них общие). Хранится в присоединённом свойстве, чтобы разметка меню
/// оставалась плоской: <c>app:Nav.Glyph="&amp;#xE80F;"</c>.
/// </summary>
public static class Nav
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Nav), new PropertyMetadata(null));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);
}
