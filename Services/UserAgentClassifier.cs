namespace v2en.Services;

/// <summary>Coarse client family derived from a user-agent string.</summary>
public readonly record struct UserAgentInfo(string Browser, string Os, string DeviceType, bool IsBot)
{
    public static readonly UserAgentInfo Unknown = new("Unknown", "Unknown", "unknown", false);
}

/// <summary>
/// Best-effort user-agent classification, deliberately kept to a small substring table rather than a
/// UA-parsing dependency. It exists to answer "roughly what kind of client was this" in the dashboard
/// and to keep crawlers out of the visitor numbers — never to identify anyone. Order matters a lot in
/// UA sniffing (every browser lies about being the others), so the checks below run most-specific first.
/// </summary>
public static class UserAgentClassifier
{
    public const string DeviceDesktop = "desktop";
    public const string DeviceMobile = "mobile";
    public const string DeviceTablet = "tablet";
    public const string DeviceBot = "bot";

    /// <summary>Substrings that mark a non-human client. Matched case-insensitively.</summary>
    private static readonly string[] BotMarkers =
    {
        "bot", "crawler", "spider", "slurp", "archiver", "scraper", "curl/", "wget/", "python-requests",
        "python-urllib", "httpclient", "go-http-client", "java/", "okhttp", "libwww-perl", "axios/",
        "node-fetch", "headlesschrome", "phantomjs", "monitoring", "uptime", "pingdom", "statuscake",
        "feedfetcher", "feedly", "rss", "preview", "lighthouse", "pagespeed", "ahrefs", "semrush",
        "dataprovider", "petalsearch", "yandex", "duckduck", "facebookexternalhit", "whatsapp",
        "telegrambot", "discordbot", "slackbot", "embedly", "quora link preview", "bitlybot",
    };

    public static UserAgentInfo Classify(string? userAgent)
    {
        var ua = userAgent?.Trim();
        if (string.IsNullOrEmpty(ua)) return UserAgentInfo.Unknown;

        var lower = ua.ToLowerInvariant();

        if (IsBot(lower))
            return new UserAgentInfo(BotName(lower), "—", DeviceBot, true);

        var os = DetectOs(lower);
        var browser = DetectBrowser(lower);
        var device = DetectDevice(lower, os);
        return new UserAgentInfo(browser, os, device, false);
    }

    private static bool IsBot(string lower)
    {
        foreach (var marker in BotMarkers)
            if (lower.Contains(marker, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Name the well-known crawlers so the bot rows in the dashboard are readable.</summary>
    private static string BotName(string lower)
    {
        if (lower.Contains("googlebot", StringComparison.Ordinal)) return "Googlebot";
        if (lower.Contains("bingbot", StringComparison.Ordinal)) return "Bingbot";
        if (lower.Contains("yandex", StringComparison.Ordinal)) return "YandexBot";
        if (lower.Contains("duckduck", StringComparison.Ordinal)) return "DuckDuckBot";
        if (lower.Contains("baiduspider", StringComparison.Ordinal)) return "Baiduspider";
        if (lower.Contains("applebot", StringComparison.Ordinal)) return "Applebot";
        if (lower.Contains("gptbot", StringComparison.Ordinal)) return "GPTBot";
        if (lower.Contains("claudebot", StringComparison.Ordinal)) return "ClaudeBot";
        if (lower.Contains("ahrefs", StringComparison.Ordinal)) return "AhrefsBot";
        if (lower.Contains("semrush", StringComparison.Ordinal)) return "SemrushBot";
        if (lower.Contains("facebookexternalhit", StringComparison.Ordinal)) return "Facebook";
        if (lower.Contains("curl/", StringComparison.Ordinal)) return "curl";
        if (lower.Contains("wget/", StringComparison.Ordinal)) return "wget";
        if (lower.Contains("feedly", StringComparison.Ordinal)) return "Feedly";
        if (lower.Contains("rss", StringComparison.Ordinal)) return "RSS reader";
        return "Bot";
    }

    /// <summary>Chromium forks all carry "chrome", so the forks have to be tested before it.</summary>
    private static string DetectBrowser(string lower)
    {
        if (lower.Contains("edg/", StringComparison.Ordinal) || lower.Contains("edga/", StringComparison.Ordinal)
            || lower.Contains("edgios/", StringComparison.Ordinal)) return "Edge";
        if (lower.Contains("opr/", StringComparison.Ordinal) || lower.Contains("opera", StringComparison.Ordinal)) return "Opera";
        if (lower.Contains("samsungbrowser", StringComparison.Ordinal)) return "Samsung Internet";
        if (lower.Contains("vivaldi", StringComparison.Ordinal)) return "Vivaldi";
        if (lower.Contains("brave", StringComparison.Ordinal)) return "Brave";
        if (lower.Contains("yabrowser", StringComparison.Ordinal)) return "Yandex Browser";
        if (lower.Contains("firefox", StringComparison.Ordinal) || lower.Contains("fxios", StringComparison.Ordinal)) return "Firefox";
        if (lower.Contains("chrome", StringComparison.Ordinal) || lower.Contains("crios", StringComparison.Ordinal)) return "Chrome";
        // Safari is the fallback for WebKit UAs — every Chromium UA also claims "safari".
        if (lower.Contains("safari", StringComparison.Ordinal) || lower.Contains("applewebkit", StringComparison.Ordinal)) return "Safari";
        if (lower.Contains("msie", StringComparison.Ordinal) || lower.Contains("trident", StringComparison.Ordinal)) return "Internet Explorer";
        return "Other";
    }

    private static string DetectOs(string lower)
    {
        // iPadOS 13+ reports "macintosh", so the iPad check has to come before macOS.
        if (lower.Contains("ipad", StringComparison.Ordinal)) return "iPadOS";
        if (lower.Contains("iphone", StringComparison.Ordinal) || lower.Contains("ipod", StringComparison.Ordinal)) return "iOS";
        if (lower.Contains("android", StringComparison.Ordinal)) return "Android";
        if (lower.Contains("cros", StringComparison.Ordinal)) return "ChromeOS";
        if (lower.Contains("windows", StringComparison.Ordinal)) return "Windows";
        if (lower.Contains("mac os x", StringComparison.Ordinal) || lower.Contains("macintosh", StringComparison.Ordinal)) return "macOS";
        if (lower.Contains("linux", StringComparison.Ordinal) || lower.Contains("x11", StringComparison.Ordinal)) return "Linux";
        return "Unknown";
    }

    private static string DetectDevice(string lower, string os)
    {
        if (os == "iPadOS" || lower.Contains("tablet", StringComparison.Ordinal)) return DeviceTablet;
        // Android tablets omit "mobile" from an otherwise-mobile UA — that absence is the only signal.
        if (os == "Android") return lower.Contains("mobile", StringComparison.Ordinal) ? DeviceMobile : DeviceTablet;
        if (os is "iOS") return DeviceMobile;
        if (lower.Contains("mobile", StringComparison.Ordinal)) return DeviceMobile;
        if (os is "Windows" or "macOS" or "Linux" or "ChromeOS") return DeviceDesktop;
        return "unknown";
    }
}
