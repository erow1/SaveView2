using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection.Handlers;

/// <summary>
/// <see cref="ActionType.Email"/> handler — SMTP email z ewentualnym załącznikiem klatki.
///
/// Config keys (wszystkie per-akcja w <see cref="DetectionAction.Config"/>):
///  • <c>"to"</c>              — adresy odbiorców (comma-separated) — WYMAGANE
///  • <c>"from"</c>            — adres nadawcy — WYMAGANE
///  • <c>"host"</c>            — SMTP host — WYMAGANE
///  • <c>"port"</c>            — SMTP port (default 587)
///  • <c>"username"</c>        — SMTP user (opcjonalny)
///  • <c>"password"</c>        — SMTP pass (opcjonalny)
///  • <c>"useStartTls"</c>     — "true"/"false" (default true)
///  • <c>"subject"</c>         — szablon tematu (placeholders: {camera}, {zone}, {trigger}, {labels})
///  • <c>"template"</c>        — szablon body (plain text; placeholders jw)
///  • <c>"attachFrame"</c>     — "true"/"false" (default true) — dołącz klatkę JPG jako załącznik
///
/// Użytkownik konfiguruje SMTP per-akcja (jedna kamera może używać swojego SMTP, inna innego).
/// Alternatywa: wspólna konfiguracja w appsettings + override per-akcja — do rozważenia w kolejnej iteracji.
/// </summary>
public sealed class EmailActionHandler : IActionHandler
{
    private readonly ILogger<EmailActionHandler> _log;

    public ActionType Type => ActionType.Email;

    public EmailActionHandler(ILogger<EmailActionHandler> log)
    {
        _log = log;
    }

    public async Task HandleAsync(DetectionAction action, ActionContext context, CancellationToken ct = default)
    {
        // Walidacja required keys
        if (!action.Config.TryGetValue("to", out var to) || string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException("EmailActionHandler: config['to'] not set");
        if (!action.Config.TryGetValue("from", out var from) || string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("EmailActionHandler: config['from'] not set");
        if (!action.Config.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("EmailActionHandler: config['host'] not set");

        var port = action.Config.TryGetValue("port", out var p) && int.TryParse(p, out var pi) ? pi : 587;
        var useTls = !action.Config.TryGetValue("useStartTls", out var tls) || tls != "false";
        var username = action.Config.GetValueOrDefault("username");
        var password = action.Config.GetValueOrDefault("password");

        var subjectTemplate = action.Config.GetValueOrDefault("subject")
            ?? "[SafeView] {trigger} — {camera}/{zone}";
        var bodyTemplate = action.Config.GetValueOrDefault("template")
            ?? "Detection on camera '{camera}' in zone '{zone}'.\nTrigger: {trigger}\nLabels: {labels}\nDetections: {detections}\nTime: {occurredAt}";
        var attachFrame = !action.Config.TryGetValue("attachFrame", out var af) || af != "false";

        var subject = RenderTemplate(subjectTemplate, context);
        var body = RenderTemplate(bodyTemplate, context);

        using var msg = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        foreach (var addr in to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(addr);

        Attachment? attachment = null;
        if (attachFrame && !string.IsNullOrEmpty(context.FrameSnapshotPath) && File.Exists(context.FrameSnapshotPath))
        {
            attachment = new Attachment(context.FrameSnapshotPath, "image/jpeg");
            msg.Attachments.Add(attachment);
        }

        try
        {
            using var smtp = new SmtpClient(host, port)
            {
                EnableSsl = useTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15_000
            };
            if (!string.IsNullOrEmpty(username))
                smtp.Credentials = new NetworkCredential(username, password ?? "");

            await smtp.SendMailAsync(msg, ct).ConfigureAwait(false);
            _log.LogDebug("Email → {To} OK (host {Host}:{Port}, frame={Frame})",
                to, host, port, attachFrame && attachment is not null);
        }
        finally
        {
            attachment?.Dispose();
        }
    }

    private static string RenderTemplate(string template, ActionContext ctx)
    {
        var labels = string.Join(", ", ctx.Detections.Select(d => d.Label).Distinct());
        return template
            .Replace("{camera}", ctx.CameraName, StringComparison.Ordinal)
            .Replace("{zone}", ctx.ZoneName, StringComparison.Ordinal)
            .Replace("{trigger}", ctx.TriggerName, StringComparison.Ordinal)
            .Replace("{labels}", labels, StringComparison.Ordinal)
            .Replace("{detections}", ctx.Detections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{occurredAt}", ctx.OccurredAt.ToString("u"), StringComparison.Ordinal);
    }
}
