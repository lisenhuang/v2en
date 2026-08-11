using Microsoft.AspNetCore.Mvc.RazorPages;
using v2en.Services;

namespace v2en.Pages.Admin;

/// <summary>
/// Shell for the analytics dashboard. Everything on the page is drawn client-side from
/// <c>GET /api/admin/analytics</c> so changing the date range never reloads the page; this model only
/// supplies the state the shell needs before that first fetch — whether collection is even on, and
/// how long data is kept — so the empty state can explain itself instead of just showing zeros.
/// </summary>
public class AnalyticsModel : PageModel
{
    private readonly RuntimeSettingsService _settings;
    private readonly AnalyticsRecorder _recorder;

    public AnalyticsModel(RuntimeSettingsService settings, AnalyticsRecorder recorder)
    {
        _settings = settings;
        _recorder = recorder;
    }

    public bool CollectionEnabled { get; private set; }
    public int RetentionDays { get; private set; }
    public bool RespectDoNotTrack { get; private set; }
    public bool IncludeAdmin { get; private set; }

    /// <summary>Page views accepted into the write queue since startup — a quick "is it working" signal.</summary>
    public long Recorded => _recorder.Recorded;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);
        CollectionEnabled = cfg.EnableAnalytics;
        RetentionDays = cfg.AnalyticsRetentionDays;
        RespectDoNotTrack = cfg.AnalyticsRespectDoNotTrack;
        IncludeAdmin = cfg.AnalyticsIncludeAdmin;
    }
}
