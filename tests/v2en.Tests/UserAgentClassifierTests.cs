using v2en.Services;
using Xunit;

namespace v2en.Tests;

public class UserAgentClassifierTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Chrome", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
        "Safari", "macOS", "desktop")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 Edg/128.0.0.0",
        "Edge", "Windows", "desktop")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0",
        "Firefox", "Linux", "desktop")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        "Safari", "iOS", "mobile")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/604.1",
        "Safari", "iPadOS", "tablet")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36",
        "Chrome", "Android", "mobile")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-X200) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
        "Chrome", "Android", "tablet")]
    public void RealBrowsersAreClassified(string userAgent, string browser, string os, string device)
    {
        var info = UserAgentClassifier.Classify(userAgent);

        Assert.Equal(browser, info.Browser);
        Assert.Equal(os, info.Os);
        Assert.Equal(device, info.DeviceType);
        Assert.False(info.IsBot);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Googlebot")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", "Bingbot")]
    [InlineData("curl/8.5.0", "curl")]
    [InlineData("python-requests/2.32.3", "Bot")]
    [InlineData("Mozilla/5.0 (compatible; ClaudeBot/1.0)", "ClaudeBot")]
    [InlineData("facebookexternalhit/1.1", "Facebook")]
    public void CrawlersAreFlaggedAndNamed(string userAgent, string expected)
    {
        var info = UserAgentClassifier.Classify(userAgent);

        Assert.True(info.IsBot);
        Assert.Equal("bot", info.DeviceType);
        Assert.Equal(expected, info.Browser);
    }

    [Fact]
    public void AMissingUserAgentIsUnknownRatherThanAnError()
    {
        foreach (var value in new string?[] { null, "", "   " })
        {
            var info = UserAgentClassifier.Classify(value);
            Assert.Equal("Unknown", info.Browser);
            Assert.Equal("unknown", info.DeviceType);
            Assert.False(info.IsBot);
        }
    }

    [Fact]
    public void ChromiumForksAreNotMisreadAsChrome()
    {
        // Every Chromium UA also claims Chrome AND Safari; the fork marker has to win.
        var opera = UserAgentClassifier.Classify(
            "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/128.0.0.0 Safari/537.36 OPR/114.0.0.0");
        Assert.Equal("Opera", opera.Browser);

        var samsung = UserAgentClassifier.Classify(
            "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36");
        Assert.Equal("Samsung Internet", samsung.Browser);
    }
}
