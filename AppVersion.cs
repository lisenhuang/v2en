namespace v2en;

/// <summary>
/// Human-readable release stamp shown at the bottom of the home page.
/// Format: yy.MM.dd.HH:mm in UTC (timezone 0), e.g. 26.06.26.09:47.
///
/// IMPORTANT: bump <see cref="Stamp"/> to the CURRENT real UTC time on EVERY code change
/// (see the "Version stamp" rule in CLAUDE.md). Get the real time from the internet, not a guess.
/// </summary>
public static class AppVersion
{
    public const string Stamp = "26.07.02.01:47";
}
