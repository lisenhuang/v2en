using System.Text.Json;
using v2en.Utilities;

namespace v2en.Services;

/// <summary>
/// Shared translation prompt + output parsing/validation, used by every translation provider
/// (OpenRouter and ChatGPT/Codex) so they behave identically: same instruction, same "did the
/// model actually translate to English?" guard, and the same tolerant JSON extraction.
/// </summary>
public static class TranslationParsing
{
    /// <summary>The system instruction sent to every provider. Kept here so all providers agree.</summary>
    public const string SystemPrompt =
        "You are a professional Chinese-to-English translator for the tech forum V2EX. " +
        "You receive a JSON object {\"title\":\"...\",\"content\":\"...\"} where content is HTML. " +
        "Translate ALL Chinese text — BOTH the title and the content — into natural, fluent English. " +
        "Your response MUST be entirely in English: do NOT return any Chinese characters from the source, " +
        "and never echo the input untranslated. If a field is already English, return it unchanged. " +
        "Respond with ONLY a JSON object of the same shape: {\"title\":\"...\",\"content\":\"...\"}, with " +
        "BOTH keys ALWAYS present and fully translated to English. " +
        "In content, preserve every HTML tag, attribute, and URL (href/src) exactly as given; translate only human-readable text. " +
        "Do NOT translate code inside <code>/<pre>, URLs, or usernames. " +
        "Do NOT wrap the JSON in markdown code fences and do NOT add any commentary.";

    /// <summary>
    /// True when a parsed translation looks like the model did NOT translate to English: the title is
    /// missing, or the title or the visible body text is still predominantly Chinese/CJK. Conservative
    /// (≥ half the letters must be CJK) so an English sentence quoting a Chinese term still passes, and
    /// an empty body is fine (V2EX has title-only posts).
    /// </summary>
    public static bool LooksUntranslated(string title, string html)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;   // a translated post must have a title
        if (IsMostlyCjk(title)) return true;                 // title must be English
        return IsMostlyCjk(HtmlText.Plain(html, 4000));      // body (if any) must be English
    }

    public static bool IsMostlyCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int cjk = 0, letters = 0;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) letters++;
            if (ch is (>= '一' and <= '鿿')    // CJK ideographs
                  or (>= '぀' and <= 'ヿ')     // Hiragana + Katakana
                  or (>= '가' and <= '힯'))    // Hangul
                cjk++;
        }
        return letters > 0 && cjk * 2 >= letters;
    }

    public static bool TryParseTranslation(string content, out string title, out string html)
    {
        title = string.Empty;
        html = string.Empty;
        var json = ExtractJson(content);
        // Many models emit raw newlines/tabs inside the "content" HTML string instead of the
        // required \n / \t escapes, which strict JSON parsing rejects. Try as-is first, then
        // retry with control characters inside strings escaped.
        foreach (var candidate in new[] { json, EscapeControlCharsInStrings(json) })
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                html = root.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(html) || !string.IsNullOrEmpty(title))
                    return true;
            }
            catch
            {
                // try the next candidate
            }
        }
        return false;
    }

    /// <summary>
    /// Escapes raw control characters (newlines, tabs, …) that appear INSIDE JSON string values,
    /// which some models emit unescaped. Structural whitespace outside strings is left untouched,
    /// so both compact and pretty-printed JSON round-trip correctly.
    /// </summary>
    public static string EscapeControlCharsInStrings(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 32);
        bool inString = false, escaped = false;
        foreach (var ch in s)
        {
            if (inString)
            {
                if (escaped) { sb.Append(ch); escaped = false; continue; }
                if (ch == '\\') { sb.Append(ch); escaped = true; continue; }
                if (ch == '"') { sb.Append(ch); inString = false; continue; }
                if (ch < 0x20)
                {
                    sb.Append(ch switch
                    {
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        '\b' => "\\b",
                        '\f' => "\\f",
                        _ => "\\u" + ((int)ch).ToString("x4"),
                    });
                    continue;
                }
                sb.Append(ch);
            }
            else
            {
                sb.Append(ch);
                if (ch == '"') inString = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Defensive: strip ``` fences and isolate the outermost {...} object.</summary>
    public static string ExtractJson(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s[..lastFence];
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s.Trim();
    }
}
