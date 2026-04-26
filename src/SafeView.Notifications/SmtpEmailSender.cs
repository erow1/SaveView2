using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Domain.Incidents;

namespace SafeView.Notifications;

public sealed class SmtpEmailSender
{
    private readonly IOptionsMonitor<NotificationOptions> _opts;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptionsMonitor<NotificationOptions> opts, ILogger<SmtpEmailSender> log)
    {
        _opts = opts;
        _log = log;
    }

    /// <summary>Wysyła dowolną wiadomość (np. raport dzienny). Załączniki opcjonalne.</summary>
    public async Task SendRawAsync(string subject, string body,
        IReadOnlyList<(string FileName, string ContentType, byte[] Content)>? attachments = null,
        CancellationToken ct = default)
    {
        var smtp = _opts.CurrentValue.Smtp;
        if (!smtp.Enabled || smtp.To.Count == 0) return;

        try
        {
            using var client = BuildClient(smtp);
            using var msg = new MailMessage
            {
                From = new MailAddress(smtp.From),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            foreach (var to in smtp.To) msg.To.Add(to);

            if (attachments is { Count: > 0 })
            {
                foreach (var a in attachments)
                {
                    var stream = new MemoryStream(a.Content);
                    msg.Attachments.Add(new Attachment(stream, a.FileName, a.ContentType));
                }
            }

            await client.SendMailAsync(msg, ct).ConfigureAwait(false);
            _log.LogDebug("SMTP raw mail sent: {Subject}", subject);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP raw mail failed: {Subject}", subject);
        }
    }

    private static SmtpClient BuildClient(SmtpOptions smtp) => new(smtp.Host, smtp.Port)
    {
        EnableSsl = smtp.UseStartTls,
        DeliveryMethod = SmtpDeliveryMethod.Network,
        UseDefaultCredentials = false,
        Credentials = string.IsNullOrEmpty(smtp.Username)
            ? CredentialCache.DefaultNetworkCredentials
            : new NetworkCredential(smtp.Username, smtp.Password ?? string.Empty)
    };

    public async Task SendAsync(Incident i, CancellationToken ct)
    {
        var smtp = _opts.CurrentValue.Smtp;
        if (!smtp.Enabled || smtp.To.Count == 0) return;

        try
        {
            var subject = (smtp.SubjectTemplate ?? "[SafeView] {severity} — {category} @ {camera}")
                .Replace("{severity}", i.Severity.ToString(), StringComparison.Ordinal)
                .Replace("{category}", i.Category, StringComparison.Ordinal)
                .Replace("{camera}", i.CameraName, StringComparison.Ordinal);

            var body = $"""
                Incydent SafeView
                ─────────────────
                Kiedy:    {i.OccurredAt:yyyy-MM-dd HH:mm:ss} UTC
                Kamera:   {i.CameraName} ({i.CameraId})
                Model:    {i.ModelName ?? "—"}
                Kategoria:{i.Category}
                Waga:     {i.Severity}
                Status:   {i.Status}

                Opis:
                {i.Summary}

                {(string.IsNullOrEmpty(i.Details) ? "" : i.Details)}

                ID: {i.Id}
                """;

            using var client = BuildClient(smtp);

            using var msg = new MailMessage
            {
                From = new MailAddress(smtp.From),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            foreach (var to in smtp.To) msg.To.Add(to);

            await client.SendMailAsync(msg, ct).ConfigureAwait(false);
            _log.LogDebug("SMTP notification sent for incident {Id}", i.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP notification failed for incident {Id}", i.Id);
        }
    }
}
