namespace v2en.Data;

/// <summary>
/// One recorded page view.
///
/// PRIVACY CONTRACT — this table never stores a raw IP address. The only per-visitor identifier is
/// <see cref="VisitorHash"/>, a one-way HMAC-SHA256 of the IP keyed by a server-side secret that is
/// re-mixed with the UTC date, so the value cannot be reversed to an IP and cannot be used to follow
/// the same person across days. Everything else here is coarse request metadata (path, referrer host,
/// user-agent family) plus whatever location Cloudflare told us at the edge.
///
/// Rows are append-only and pruned by the retention window in <c>RuntimeSettings.AnalyticsRetentionDays</c>.
/// </summary>
public class AnalyticsEvent
{
    public long Id { get; set; }

    /// <summary>When the request finished (UTC). The sort/filter key for every report.</summary>
    public DateTimeOffset Utc { get; set; }

    /// <summary>Request path WITHOUT the query string, e.g. "/t/1222343". Clipped to 512 chars.</summary>
    public string Path { get; set; } = "";

    /// <summary>HTTP method — only page-view methods are recorded, so in practice always "GET".</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Response status code, so 404s and errors show up in the dashboard too.</summary>
    public int StatusCode { get; set; }

    /// <summary>How long the request took to serve, in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// One-way, date-salted visitor fingerprint (hex). NOT an IP and NOT reversible — see the class
    /// remarks. Empty when the request had no usable client address at all.
    /// </summary>
    public string VisitorHash { get; set; } = "";

    // ── Location, as reported by Cloudflare's edge (all optional — a direct/local request has none) ──

    /// <summary>ISO-3166-1 alpha-2 from <c>CF-IPCountry</c>. Null when unknown ("XX") or absent.</summary>
    public string? Country { get; set; }

    /// <summary>Continent code from <c>CF-IPContinent</c> (e.g. "OC"), when the edge sends it.</summary>
    public string? Continent { get; set; }

    /// <summary>Region/state name from <c>CF-Region</c>.</summary>
    public string? Region { get; set; }

    /// <summary>City name from <c>CF-IPCity</c>.</summary>
    public string? City { get; set; }

    /// <summary>Latitude from <c>CF-IPLatitude</c> — city-level, never a precise position.</summary>
    public double? Latitude { get; set; }

    /// <summary>Longitude from <c>CF-IPLongitude</c>.</summary>
    public double? Longitude { get; set; }

    /// <summary>IANA timezone from <c>CF-Timezone</c>, e.g. "Pacific/Auckland".</summary>
    public string? Timezone { get; set; }

    /// <summary>Cloudflare colo (edge datacenter) code parsed from the <c>CF-Ray</c> header, e.g. "AKL".</summary>
    public string? EdgeColo { get; set; }

    // ── Request provenance ────────────────────────────────────────────────────────

    /// <summary>Referrer host only (e.g. "news.ycombinator.com"). Null for direct traffic.</summary>
    public string? ReferrerHost { get; set; }

    /// <summary>Full referrer URL, clipped. Kept so the dashboard can show the exact linking page.</summary>
    public string? ReferrerUrl { get; set; }

    /// <summary>Raw user-agent, clipped to 320 chars. Null when the client sent none.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Derived browser family ("Chrome", "Safari", …) — best-effort, never authoritative.</summary>
    public string? Browser { get; set; }

    /// <summary>Derived OS family ("Windows", "iOS", …).</summary>
    public string? Os { get; set; }

    /// <summary>"desktop" | "mobile" | "tablet" | "bot" | "unknown".</summary>
    public string? DeviceType { get; set; }

    /// <summary>True when the user-agent looks like a crawler/monitor. Filtered out of reports by default.</summary>
    public bool IsBot { get; set; }
}
