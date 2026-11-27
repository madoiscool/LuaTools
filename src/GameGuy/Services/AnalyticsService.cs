using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace GameGuy.Services;

/// <summary>
/// Anonymous app-launch analytics via a self-hosted Umami instance. Posts a single "app_launch" event
/// per launch to Umami's /api/send (the same endpoint the browser script calls). No personal data.
/// Just an event name + app version. Best-effort fire-and-forget: never throws, never blocks startup.
/// </summary>
public class AnalyticsService
{
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(10));

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion is { } v && v.IndexOf('+') is var i and >= 0 ? v[..i]
        : Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    /// <summary>Report one app launch. Silent no-op on any failure.</summary>
    public async Task TrackAppLaunchAsync(CancellationToken ct = default)
    {
        try
        {
            // Sent as a PAGEVIEW (no event "name") to /launch/<version>. Umami surfaces this directly in
            // Activity ("Viewed page /launch/0.0.9") and in the Views list as a per-version breakdown.
            // A named event would instead be hidden as a custom event and the URL wouldn't show.
            var body = new
            {
                type = "event",
                payload = new
                {
                    website = AppConfig.UmamiWebsiteId,
                    hostname = AppConfig.UmamiHostname,
                    url = $"/launch/{Version}",
                    title = "App Launch",
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.UmamiHost}/api/send")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            // Umami's bot filter SILENTLY DROPS events from non-standard User-Agents (it still returns
            // 200 but with a {"beep":"boop"} body and records nothing). A custom "GameGuyDesktop/x"
            // suffix tripped that filter, so we send a plain, standard Windows Chrome UA. The launch is
            // still identifiable in the dashboard via the "app_launch" event name + desktop.lua.tools host.
            req.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            await _http.SendAsync(req, ct);
        }
        catch
        {
            // Telemetry must never affect the app: swallow everything.
        }
    }
}
