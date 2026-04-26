namespace SafeView.Notifications;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Minimalna waga incydentu, którą publikujemy (Low=0, Medium=1, High=2, Critical=3).</summary>
    public int MinSeverity { get; set; } = 1;

    public SmtpOptions Smtp { get; set; } = new();
    public List<WebhookOptions> Webhooks { get; set; } = [];
    public DigestOptions Digest { get; set; } = new();
}

public sealed class DigestOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Godzina UTC wysyłki digestu (0–23).</summary>
    public int HourUtc { get; set; } = 6;
    /// <summary>Minuta UTC wysyłki digestu (0–59).</summary>
    public int MinuteUtc { get; set; }
}

public sealed class SmtpOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25;
    public bool UseStartTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "safeview@localhost";
    public List<string> To { get; set; } = [];
    public string SubjectTemplate { get; set; } = "[SafeView] {severity} — {category} @ {camera}";
}

public sealed class WebhookOptions
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = string.Empty;

    /// <summary>Format: "generic" (JSON), "splunk" (HEC), "slack" (text).</summary>
    public string Format { get; set; } = "generic";

    /// <summary>Bearer token / Splunk HEC token / etc.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Custom HTTP headers (e.g. "X-Source": "SafeView").</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
}
