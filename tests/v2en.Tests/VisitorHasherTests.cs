using System.Net;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// Guards the privacy contract: the stored visitor id must be stable within a day, different the
/// next day, and must never contain or reveal the IP it was derived from.
/// </summary>
public class VisitorHasherTests
{
    private const string Salt = "dGVzdC1zYWx0LXZhbHVlLXRoYXQtaXMtbG9uZy1lbm91Z2g=";
    private static readonly DateTimeOffset Monday = new(2026, 8, 10, 9, 30, 0, TimeSpan.Zero);
    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void SameVisitorSameDayGetsTheSameId()
    {
        var morning = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday);
        var evening = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday.AddHours(12));

        Assert.Equal(morning, evening);
        Assert.NotEmpty(morning);
    }

    [Fact]
    public void TheSameVisitorIsUnrecognisableTheNextDay()
    {
        // This is what stops the table from being a long-term record of an individual.
        var today = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday);
        var tomorrow = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday.AddDays(1));

        Assert.NotEqual(today, tomorrow);
    }

    [Fact]
    public void DifferentVisitorsGetDifferentIds()
    {
        var a = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday);
        var b = VisitorHasher.Compute(IPAddress.Parse("198.51.100.4"), "Chrome", Salt, Monday);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoDevicesBehindOneAddressAreCountedSeparately()
    {
        var laptop = VisitorHasher.Compute(Ip, "Mozilla/5.0 (Macintosh) Chrome", Salt, Monday);
        var phone = VisitorHasher.Compute(Ip, "Mozilla/5.0 (iPhone) Safari", Salt, Monday);

        Assert.NotEqual(laptop, phone);
    }

    [Fact]
    public void ChangingTheSaltInvalidatesEveryPreviousId()
    {
        var before = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday);
        var after = VisitorHasher.Compute(Ip, "Chrome", VisitorHasher.NewSalt(), Monday);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void TheIdNeverContainsTheAddressItCameFrom()
    {
        var hash = VisitorHasher.Compute(Ip, "Chrome", Salt, Monday);

        Assert.DoesNotContain("203", hash);
        Assert.DoesNotContain("113", hash);
        Assert.Equal(32, hash.Length);                             // 16 bytes, hex
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void NoAddressMeansNoVisitorIdentity()
    {
        Assert.Equal("", VisitorHasher.Compute(null, "Chrome", Salt, Monday));
    }

    [Fact]
    public void NoSaltMeansNoIdentity_SoNothingIsEverHashedUnsalted()
    {
        // The recorder starts with an empty snapshot until the salt is loaded; that must not
        // produce a weak, unsalted identifier.
        Assert.Equal("", VisitorHasher.Compute(Ip, "Chrome", "", Monday));
    }

    [Fact]
    public void ANonBase64SaltStillWorks()
    {
        // A salt typed in by hand must not silently disable hashing.
        var hash = VisitorHasher.Compute(Ip, "Chrome", "a plain string salt", Monday);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void GeneratedSaltsAreRandomAndLongEnough()
    {
        var a = VisitorHasher.NewSalt();
        var b = VisitorHasher.NewSalt();

        Assert.NotEqual(a, b);
        Assert.Equal(32, Convert.FromBase64String(a).Length);
    }

    [Fact]
    public void AVeryLongUserAgentDoesNotBreakHashing()
    {
        var hash = VisitorHasher.Compute(Ip, new string('u', 4000), Salt, Monday);
        Assert.Equal(32, hash.Length);
    }
}
