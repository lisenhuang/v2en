using System.Globalization;
using System.Net;

namespace v2en.Services;

/// <summary>Where Cloudflare says the visitor is. Every field is optional — see <see cref="CloudflareRequestReader"/>.</summary>
public readonly record struct EdgeLocation(
    string? Country,
    string? Continent,
    string? Region,
    string? City,
    double? Latitude,
    double? Longitude,
    string? Timezone,
    string? EdgeColo)
{
    /// <summary>Nothing at all was resolvable — a direct/local request, or a plan that sends no geo headers.</summary>
    public bool IsEmpty =>
        Country is null && Continent is null && Region is null && City is null &&
        Latitude is null && Longitude is null && Timezone is null && EdgeColo is null;

    public static readonly EdgeLocation None = default;
}

/// <summary>
/// Reads the real client address and the visitor-location headers that Cloudflare adds at the edge.
///
/// The site is served through a Cloudflare tunnel, so on a normal request the origin sees
/// <c>CF-Connecting-IP</c> plus whichever geo headers the zone's plan/transform rule enables. None of
/// them are guaranteed: the free plan sends only <c>CF-IPCountry</c>, and a direct hit (localhost,
/// a health check, a container-to-container call) sends none at all. Every read here therefore
/// degrades to null rather than throwing or guessing, which is what keeps local development and
/// direct requests working — they simply show up as "unknown location".
///
/// Header values are attacker-controlled if anything can reach the origin without passing through
/// Cloudflare, so everything is validated and clipped before it is stored: country must be two
/// letters, coordinates must parse invariantly and sit inside real lat/long bounds, and free-text
/// fields have control characters stripped and are length-capped.
/// </summary>
public static class CloudflareRequestReader
{
    // Cloudflare's own header names (see developers.cloudflare.com → HTTP request headers).
    public const string ConnectingIpHeader = "CF-Connecting-IP";
    public const string TrueClientIpHeader = "True-Client-IP";      // Enterprise
    public const string CountryHeader = "CF-IPCountry";
    public const string ContinentHeader = "CF-IPContinent";
    public const string CityHeader = "CF-IPCity";
    public const string RegionHeader = "CF-Region";
    public const string LatitudeHeader = "CF-IPLatitude";
    public const string LongitudeHeader = "CF-IPLongitude";
    public const string TimezoneHeader = "CF-Timezone";
    public const string RayHeader = "CF-Ray";

    private const int MaxTextLength = 96;

    /// <summary>
    /// The visitor's IP, preferring Cloudflare's own header and falling back through the standard
    /// proxy header to the socket address. Returns null when nothing usable is present.
    ///
    /// Note the fallback order matters: <c>CF-Connecting-IP</c> is a single, edge-set value and cannot
    /// be spoofed by the client through Cloudflare, whereas <c>X-Forwarded-For</c> is a client-appendable
    /// list, so only its FIRST entry (the original client) is considered.
    /// </summary>
    public static IPAddress? ClientIp(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;

        if (TryParseIp(headers[ConnectingIpHeader], out var cf)) return cf;
        if (TryParseIp(headers[TrueClientIpHeader], out var tc)) return tc;

        // UseForwardedHeaders normally consumes X-Forwarded-For before we get here; this covers the
        // case where it did not (extra hops left in the header).
        var forwarded = headers["X-Forwarded-For"].ToString();
        if (forwarded.Length > 0)
        {
            var first = forwarded.Split(',')[0].Trim();
            if (TryParseIp(first, out var xff)) return xff;
        }

        return Normalize(ctx.Connection.RemoteIpAddress);
    }

    /// <summary>Reads every location header Cloudflare might have added. All fields degrade to null.</summary>
    public static EdgeLocation ReadLocation(HttpRequest request)
    {
        var h = request.Headers;

        var country = NormalizeCountry(h[CountryHeader]);
        var continent = NormalizeCountry(h[ContinentHeader]);   // same shape: two ASCII letters
        var region = CleanText(h[RegionHeader]);
        var city = CleanText(h[CityHeader]);
        var timezone = CleanText(h[TimezoneHeader], 64);
        var colo = ParseColo(h[RayHeader]);

        // Latitude and longitude are only meaningful together — a lone coordinate is dropped.
        double? lat = ParseCoordinate(h[LatitudeHeader], 90);
        double? lon = ParseCoordinate(h[LongitudeHeader], 180);
        if (lat is null || lon is null) { lat = null; lon = null; }

        return new EdgeLocation(country, continent, region, city, lat, lon, timezone, colo);
    }

    /// <summary>
    /// Parses and canonicalizes an IP so the same visitor always hashes to the same value
    /// (IPv4-mapped IPv6 is folded to IPv4, and the scope id on a link-local IPv6 is dropped).
    /// </summary>
    public static bool TryParseIp(string? value, out IPAddress? address)
    {
        address = null;
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return false;

        // Tolerate an "ip:port" IPv4 form; bracketed IPv6 is handled by IPEndPoint-style parsing below.
        if (!IPAddress.TryParse(s, out var parsed))
        {
            var colon = s.LastIndexOf(':');
            if (colon <= 0 || s.IndexOf(':') != colon || !IPAddress.TryParse(s[..colon], out parsed))
                return false;
        }

        address = Normalize(parsed);
        return address is not null;
    }

    private static IPAddress? Normalize(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        // ScopeId is IPv6-only — reading it on an IPv4 address throws SocketException(95), so the
        // family check MUST come first. Rebuilding from the bytes drops the "%eth0" scope, which
        // otherwise makes the same link-local visitor hash differently per interface.
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            address = new IPAddress(address.GetAddressBytes());

        return address;
    }

    /// <summary>
    /// A two-character code, upper-cased. ISO-3166 codes are two letters, but Cloudflare also sends
    /// its own pseudo-codes — notably "T1" for Tor — so a trailing digit is allowed while the first
    /// character must still be a letter (that rejects junk like "12"). Cloudflare's "XX" means "could
    /// not geolocate" and becomes null, so the dashboard says Unknown instead of inventing a country.
    /// </summary>
    private static string? NormalizeCountry(string? value)
    {
        var s = value?.Trim();
        if (s is not { Length: 2 }) return null;
        var a = char.ToUpperInvariant(s[0]);
        var b = char.ToUpperInvariant(s[1]);
        if (a is < 'A' or > 'Z') return null;
        if (b is (< 'A' or > 'Z') and (< '0' or > '9')) return null;
        var code = string.Concat(a, b);
        return code == Utilities.Countries.UnknownCode ? null : code;
    }

    /// <summary>Invariant parse (the edge always sends "." decimals) plus a real-world range check.</summary>
    private static double? ParseCoordinate(string? value, double limit)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
        if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > limit) return null;
        return d;
    }

    /// <summary>
    /// A CF-Ray looks like "7d1234abcdef1234-AKL"; the suffix is the IATA code of the edge datacenter
    /// that served the request. Useful as a coarse "where did this land" signal even with no geo headers.
    /// </summary>
    private static string? ParseColo(string? ray)
    {
        var s = ray?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        var dash = s.LastIndexOf('-');
        if (dash < 0 || dash == s.Length - 1) return null;
        var colo = s[(dash + 1)..];
        if (colo.Length is < 2 or > 8) return null;
        foreach (var ch in colo)
            if (!char.IsAsciiLetterOrDigit(ch)) return null;
        return colo.ToUpperInvariant();
    }

    /// <summary>Strips control characters and clips — header text is untrusted and may be long.</summary>
    private static string? CleanText(string? value, int maxLength = MaxTextLength)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;

        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        var n = 0;
        foreach (var ch in s)
            if (!char.IsControl(ch)) buffer[n++] = ch;

        if (n == 0) return null;
        if (n > maxLength) n = maxLength;
        return new string(buffer[..n]).TrimEnd();
    }
}
