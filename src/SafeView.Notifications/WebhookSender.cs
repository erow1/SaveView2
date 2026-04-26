using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using SafeView.Domain.Incidents;

namespace SafeView.Notifications;

public sealed class WebhookSender
{
    private readonly HttpClient _http;
    private readonly ILogger<WebhookSender> _log;

    public WebhookSender(HttpClient http, ILogger<WebhookSender> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _log = log;
    }

    public async Task SendAsync(WebhookOptions hook, Incident i, CancellationToken ct)
    {
        if (!hook.Enabled || string.IsNullOrWhiteSpace(hook.Url)) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, hook.Url);
            if (!string.IsNullOrEmpty(hook.AuthToken))
            {
                if (hook.Format.Equals("splunk", StringComparison.OrdinalIgnoreCase))
                    req.Headers.TryAddWithoutValidation("Authorization", "Splunk " + hook.AuthToken);
                else
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hook.AuthToken);
            }
            foreach (var (k, v) in hook.Headers)
                req.Headers.TryAddWithoutValidation(k, v);

            req.Content = hook.Format.ToLowerInvariant() switch
            {
                "splunk" => JsonContent.Create(new
                {
                    @event = new
                    {
                        product = "SafeView",
                        i.Id,
                        i.CameraId,
                        i.CameraName,
                        i.Category,
                        severity = i.Severity.ToString(),
                        status = i.Status.ToString(),
                        i.Summary,
                        i.OccurredAt
                    },
                    sourcetype = "safeview:incident"
                }),
                "slack" => new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        text = $":rotating_light: *SafeView* `{i.Severity}` — *{i.Category}* @ *{i.CameraName}*\n{i.Summary}"
                    }),
                    Encoding.UTF8, "application/json"),
                _ => JsonContent.Create(new
                {
                    type = "safeview.incident",
                    id = i.Id,
                    occurredAt = i.OccurredAt,
                    camera = new { id = i.CameraId, name = i.CameraName },
                    model = new { id = i.ModelId, name = i.ModelName },
                    category = i.Category,
                    severity = i.Severity.ToString(),
                    status = i.Status.ToString(),
                    summary = i.Summary,
                    details = i.Details
                })
            };

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("Webhook {Name} returned {Status} for incident {Id}",
                    hook.Name, (int)resp.StatusCode, i.Id);
            else
                _log.LogDebug("Webhook {Name} OK for incident {Id}", hook.Name, i.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Webhook {Name} failed for incident {Id}", hook.Name, i.Id);
        }
    }
}
