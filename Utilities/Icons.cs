using Microsoft.AspNetCore.Html;

namespace v2en.Utilities;

/// <summary>
/// Inline SVG icons (Feather/Lucide-style line icons, drawn on a 24×24 viewBox with
/// <c>stroke="currentColor"</c>) used across the UI in place of emoji. Rendering an icon as
/// real vector markup — instead of an emoji glyph — makes it look identical on every platform,
/// inherit the surrounding text color and theme, and scale crisply with font-size.
///
/// Call from Razor with <c>@Icons.Svg("settings")</c>. The icon name and CSS class are always
/// literals supplied by our own views (never user input), so the markup is safe to emit raw.
/// </summary>
public static class Icons
{
    // Inner markup for each icon (paths/shapes only) on a 24×24 viewBox.
    private static readonly Dictionary<string, string> Shapes = new()
    {
        ["settings"] =
            "<circle cx='12' cy='12' r='3'/>" +
            "<path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/>",
        ["lock"] =
            "<rect x='3' y='11' width='18' height='11' rx='2' ry='2'/>" +
            "<path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
        ["mic"] =
            "<path d='M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z'/>" +
            "<path d='M19 10v2a7 7 0 0 1-14 0v-2'/>" +
            "<line x1='12' y1='19' x2='12' y2='23'/>" +
            "<line x1='8' y1='23' x2='16' y2='23'/>",
        ["moon"] =
            "<path d='M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z'/>",
        ["sun"] =
            "<circle cx='12' cy='12' r='5'/>" +
            "<line x1='12' y1='1' x2='12' y2='3'/><line x1='12' y1='21' x2='12' y2='23'/>" +
            "<line x1='4.22' y1='4.22' x2='5.64' y2='5.64'/><line x1='18.36' y1='18.36' x2='19.78' y2='19.78'/>" +
            "<line x1='1' y1='12' x2='3' y2='12'/><line x1='21' y1='12' x2='23' y2='12'/>" +
            "<line x1='4.22' y1='19.78' x2='5.64' y2='18.36'/><line x1='18.36' y1='5.64' x2='19.78' y2='4.22'/>",
        ["clock"] =
            "<circle cx='12' cy='12' r='10'/>" +
            "<polyline points='12 6 12 12 16 14'/>",
        ["check"] =
            "<path d='M22 11.08V12a10 10 0 1 1-5.93-9.14'/>" +
            "<polyline points='22 4 12 14.01 9 11.01'/>",
        ["alert"] =
            "<path d='M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z'/>" +
            "<line x1='12' y1='9' x2='12' y2='13'/><line x1='12' y1='17' x2='12.01' y2='17'/>",
        ["search"] =
            "<circle cx='11' cy='11' r='8'/>" +
            "<line x1='21' y1='21' x2='16.65' y2='16.65'/>",
        ["monitor"] =
            "<rect x='2' y='3' width='20' height='14' rx='2' ry='2'/>" +
            "<line x1='8' y1='21' x2='16' y2='21'/><line x1='12' y1='17' x2='12' y2='21'/>",
    };

    /// <summary>Render a named icon as inline SVG with the given CSS class(es).</summary>
    public static IHtmlContent Svg(string name, string cssClass = "icon")
    {
        var inner = Shapes.TryGetValue(name, out var s) ? s : string.Empty;
        var svg =
            $"<svg class=\"{cssClass}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" " +
            "stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" " +
            "focusable=\"false\">" + inner.Replace('\'', '"') + "</svg>";
        return new HtmlString(svg);
    }
}
