using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection.Handlers;

/// <summary>
/// <see cref="ActionType.Webhook"/> handler — wysyła HTTP POST z JSON payloadem do URL
/// skonfigurowanego w <see cref="DetectionAction.Config"/>.
///
/// Config keys:
///  • <c>"url"</c> — URL endpoint (wymagane, np. https://hooks.zapier.com/...)
///  • <c>"secret"</c> — opcjonalny Bearer token (wysyłany w nagłówku Authorization)
///  • <c>"template"</c> — opcjonalny szablon payloadu — JSON z placeholderami {camera}/{zone}/itp.
///                        Domyślnie wysyłamy structured payload (patrz <see cref="DefaultPayload"/>).
///
/// Timeout: 10s. Retry: nie robimy (rate limit + disable/enable Action załatwia).
/// </summary>
public sealed class WebhookActionHandler : IActionHandler
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebhookActionHandler> _log;

    public ActionType Type => ActionType.Webhook;

    public WebhookActionHandler(IHttpClientFactory httpFactory, ILogger<WebhookActionHandler> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task HandleAsync(DetectionAction action, ActionContext context, CancellationToken ct = default)
    {
        if (!action.Config.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("WebhookActionHandler: config['url'] not set");

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);

        // Payload — albo custom template albo nasz domyślny structured
        if (action.Config.TryGetValue("template", out var template) && !string.IsNullOrWhiteSpace(template))
        {
            var rendered = RenderTemplate(template, context);
            req.Content = new StringContent(rendered, System.Text.Encoding.UTF8, "application/json");
        }
        else
        {
            req.Content = JsonContent.Create(DefaultPayload(action, context));
        }

        // Bearer secret (opcjonalny)
        if (action.Config.TryGetValue("secret", out var secret) && !string.IsNullOrWhiteSpace(secret))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Webhook {resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }

        _log.LogDebug("Webhook → {Url} OK ({Status})", url, (int)resp.StatusCode);
    }

    private static object DefaultPayload(DetectionAction action, ActionContext ctx) => new
    {
        action = new { id = action.Id, name = action.Name, type = action.Type.ToString() },
        trigger = new { id = ctx.TriggerId, name = ctx.TriggerName },
        camera = new { id = ctx.CameraId, name = ctx.CameraName },
        zone = new { id = ctx.ZoneId, name = ctx.ZoneName },
        detections = ctx.Detections.Select(d => new
        {
            label = d.Label,
            confidence = d.Confidence,
            bbox = new { x = d.Bbox.X, y = d.Bbox.Y, w = d.Bbox.Width, h = d.Bbox.Height }
        }),
        frameSnapshot = ctx.FrameSnapshotPath,
        occurredAt = ctx.OccurredAt.ToString("O")
    };

    private static string RenderTemplate(string template, ActionContext ctx)
    {
        var labels = string.Join(", ", ctx.Detections.Select(d => d.Label).Distinct());
        return template
            .Replace("{camera}", ctx.CameraName, StringComparison.Ordinal)
            .Replace("{zone}", ctx.ZoneName, StringComparison.Ordinal)
            .Replace("{trigger}", ctx.TriggerName, StringComparison.Ordinal)
            .Replace("{labels}", labels, StringComparison.Ordinal)
            .Replace("{detections}", ctx.Detections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{occurredAt}", ctx.OccurredAt.ToString("O"), StringComparison.Ordinal);
    }
}
