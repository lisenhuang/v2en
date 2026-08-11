using System.Net;
using Microsoft.AspNetCore.Http;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// Covers the acceptance criteria around Cloudflare headers: a request through the edge must produce
/// usable location data, and a direct/local request must still work and read as "unknown location".
/// </summary>
public class CloudflareRequestReaderTests
{
    private static HttpContext Request(params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (name, value) in headers)
            ctx.Request.Headers[name] = value;
        return ctx;
    }

    [Fact]
    public void ReadsEveryCloudflareLocationHeader()
    {
        var ctx = Request(
            ("CF-IPCountry", "NZ"),
            ("CF-IPContinent", "OC"),
            ("CF-IPCity", "Auckland"),
            ("CF-Region", "Auckland"),
            ("CF-IPLatitude", "-36.84850"),
            ("CF-IPLongitude", "174.76330"),
            ("CF-Timezone", "Pacific/Auckland"),
            ("CF-Ray", "7d1234abcdef1234-AKL"));

        var location = CloudflareRequestReader.ReadLocation(ctx.Request);

        Assert.Equal("NZ", location.Country);
        Assert.Equal("OC", location.Continent);
        Assert.Equal("Auckland", location.City);
        Assert.Equal("Auckland", location.Region);
        Assert.Equal(-36.8485, location.Latitude!.Value, 4);
        Assert.Equal(174.7633, location.Longitude!.Value, 4);
        Assert.Equal("Pacific/Auckland", location.Timezone);
        Assert.Equal("AKL", location.EdgeColo);
        Assert.False(location.IsEmpty);
    }

    [Fact]
    public void ADirectRequestWithNoEdgeHeadersHasNoLocation()
    {
        var location = CloudflareRequestReader.ReadLocation(Request().Request);

        Assert.True(location.IsEmpty);
        Assert.Null(location.Country);
        Assert.Null(location.Latitude);
        Assert.Null(location.City);
    }

    [Fact]
    public void CountryOnlyIsEnough_TheFreeCloudflarePlanSendsNothingElse()
    {
        var location = CloudflareRequestReader.ReadLocation(Request(("CF-IPCountry", "de")).Request);

        Assert.Equal("DE", location.Country);          // normalized to upper case
        Assert.Null(location.City);
        Assert.Null(location.Latitude);
    }

    [Fact]
    public void UnknownCountryCodeBecomesNoCountry()
    {
        // Cloudflare sends XX when it cannot geolocate the visitor at all.
        var location = CloudflareRequestReader.ReadLocation(Request(("CF-IPCountry", "XX")).Request);
        Assert.Null(location.Country);
    }

    [Fact]
    public void TorTrafficKeepsItsPseudoCode()
    {
        var location = CloudflareRequestReader.ReadLocation(Request(("CF-IPCountry", "T1")).Request);
        Assert.Equal("T1", location.Country);
    }

    [Theory]
    [InlineData("banana")]        // not a number
    [InlineData("999")]           // out of range
    [InlineData("-91.5")]         // past the pole
    [InlineData("")]
    public void MalformedCoordinatesAreDiscarded(string latitude)
    {
        var location = CloudflareRequestReader.ReadLocation(
            Request(("CF-IPLatitude", latitude), ("CF-IPLongitude", "174.7")).Request);

        Assert.Null(location.Latitude);
        Assert.Null(location.Longitude);   // a lone coordinate is meaningless, so both are dropped
    }

    [Fact]
    public void ALoneLongitudeIsDropped()
    {
        var location = CloudflareRequestReader.ReadLocation(Request(("CF-IPLongitude", "174.7")).Request);
        Assert.Null(location.Longitude);
    }

    [Theory]
    [InlineData("USA")]           // three letters
    [InlineData("N")]             // one letter
    [InlineData("12")]            // digits
    [InlineData(" ")]
    public void MalformedCountryCodesAreDiscarded(string code)
    {
        Assert.Null(CloudflareRequestReader.ReadLocation(Request(("CF-IPCountry", code)).Request).Country);
    }

    [Fact]
    public void CityTextIsStrippedOfControlCharactersAndClipped()
    {
        var ctx = Request(("CF-IPCity", "Auck\0land\r\n" + new string('x', 200)));

        var city = CloudflareRequestReader.ReadLocation(ctx.Request).City!;

        Assert.DoesNotContain('\0', city);
        Assert.DoesNotContain('\n', city);
        Assert.True(city.Length <= 96);
        Assert.StartsWith("Auckland", city);
    }

    [Fact]
    public void MalformedRayHeaderYieldsNoColo()
    {
        Assert.Null(CloudflareRequestReader.ReadLocation(Request(("CF-Ray", "7d1234abcdef1234")).Request).EdgeColo);
        Assert.Null(CloudflareRequestReader.ReadLocation(Request(("CF-Ray", "abc-")).Request).EdgeColo);
    }

    // ── Client IP resolution ───────────────────────────────────────────────────

    [Fact]
    public void CloudflaresHeaderWinsOverEveryOtherSource()
    {
        var ctx = Request(
            ("CF-Connecting-IP", "203.0.113.7"),
            ("True-Client-IP", "198.51.100.9"),
            ("X-Forwarded-For", "192.0.2.1"));
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");

        Assert.Equal(IPAddress.Parse("203.0.113.7"), CloudflareRequestReader.ClientIp(ctx));
    }

    [Fact]
    public void FallsBackThroughTrueClientIpThenForwardedForThenTheSocket()
    {
        var trueClient = Request(("True-Client-IP", "198.51.100.9"));
        Assert.Equal(IPAddress.Parse("198.51.100.9"), CloudflareRequestReader.ClientIp(trueClient));

        var forwarded = Request(("X-Forwarded-For", "192.0.2.1, 70.41.3.18"));
        Assert.Equal(IPAddress.Parse("192.0.2.1"), CloudflareRequestReader.ClientIp(forwarded));

        var direct = Request();
        direct.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        Assert.Equal(IPAddress.Parse("10.1.2.3"), CloudflareRequestReader.ClientIp(direct));
    }

    [Fact]
    public void NoAddressAnywhereReturnsNull()
    {
        Assert.Null(CloudflareRequestReader.ClientIp(Request()));
    }

    [Fact]
    public void GarbageInTheIpHeaderFallsThroughInsteadOfThrowing()
    {
        var ctx = Request(("CF-Connecting-IP", "not-an-ip"));
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");

        Assert.Equal(IPAddress.Parse("10.0.0.5"), CloudflareRequestReader.ClientIp(ctx));
    }

    [Fact]
    public void IPv6IsAcceptedAndIPv4MappedFormsAreCanonicalized()
    {
        var v6 = Request(("CF-Connecting-IP", "2001:db8::1"));
        Assert.Equal(IPAddress.Parse("2001:db8::1"), CloudflareRequestReader.ClientIp(v6));

        // The same visitor must hash identically whether the address arrives mapped or plain.
        var mapped = Request(("CF-Connecting-IP", "::ffff:203.0.113.7"));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), CloudflareRequestReader.ClientIp(mapped));
    }

    [Fact]
    public void AnIpv4WithAPortIsStillUnderstood()
    {
        Assert.Equal(
            IPAddress.Parse("203.0.113.7"),
            CloudflareRequestReader.ClientIp(Request(("CF-Connecting-IP", "203.0.113.7:44321"))));
    }
}
