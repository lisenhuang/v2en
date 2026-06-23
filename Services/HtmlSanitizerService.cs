using Ganss.Xss;

namespace v2en.Services;

/// <summary>
/// Wraps Ganss.Xss.HtmlSanitizer. Translated post bodies (user HTML from V2EX, passed
/// through an LLM) are sanitized once at store time before rendering and before feed CDATA.
/// </summary>
public class HtmlSanitizerService
{
    private readonly HtmlSanitizer _sanitizer;

    public HtmlSanitizerService()
    {
        _sanitizer = new HtmlSanitizer();
        // V2EX content uses classes (e.g. embedded_image) and link rel/target.
        _sanitizer.AllowedAttributes.Add("class");
        _sanitizer.AllowedAttributes.Add("target");
        _sanitizer.AllowedAttributes.Add("rel");
        _sanitizer.KeepChildNodes = true; // drop disallowed tags but keep their text
    }

    public string Sanitize(string? html) => _sanitizer.Sanitize(html ?? string.Empty);
}
