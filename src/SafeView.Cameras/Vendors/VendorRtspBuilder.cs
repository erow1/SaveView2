using System.Globalization;
using SafeView.Domain.Cameras;

namespace SafeView.Cameras.Vendors;

/// <summary>
/// Buduje URL RTSP dla kamery na podstawie producenta + parametrów (host, port, login, kanał, main/sub).
/// Szablony są oparte o publicznie udokumentowane konwencje vendorów. W razie nietypowego firmware
/// użytkownik może wybrać <see cref="CameraVendor.Custom"/> i wpisać URL ręcznie.
/// </summary>
public static class VendorRtspBuilder
{
    /// <summary>Zwraca SourceUrl dla podanej kamery. Dla Custom — zwraca <see cref="Camera.SourceUrl"/> bez zmian.</summary>
    public static string Build(Camera c)
    {
        ArgumentNullException.ThrowIfNull(c);

        if (c.Vendor is CameraVendor.Custom or CameraVendor.FileSource)
            return c.SourceUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(c.Host))
            return string.Empty;

        var port = c.Port > 0 ? c.Port : DefaultPort(c.Vendor);
        var ch = c.Channel > 0 ? c.Channel : 1;
        var path = BuildPath(c.Vendor, c.StreamProfile, ch);
        var creds = BuildCreds(c.Username, c.Password);
        var hostPort = $"{c.Host}:{port.ToString(CultureInfo.InvariantCulture)}";

        return $"rtsp://{creds}{hostPort}/{path}";
    }

    /// <summary>Sugerowany port RTSP per vendor (do auto-uzupełnienia w UI).</summary>
    public static int DefaultPort(CameraVendor v) => v switch
    {
        CameraVendor.ReCamera => 8554,
        _ => 554
    };

    /// <summary>Czytelny opis szablonu — pokazujemy w UI obok pola.</summary>
    public static string DescribeTemplate(CameraVendor v, CameraStreamProfile p) => v switch
    {
        CameraVendor.Dahua     => $"/cam/realmonitor?channel={{ch}}&subtype={(p == CameraStreamProfile.Main ? 0 : 1)}",
        CameraVendor.Hikvision => $"/Streaming/Channels/{{ch}}{(p == CameraStreamProfile.Main ? "01" : "02")}",
        CameraVendor.Axis      => $"/axis-media/media.amp?streamprofile={(p == CameraStreamProfile.Main ? "Quality" : "Mobile")}",
        CameraVendor.Bosch     => $"/rtsp_tunnel?inst={(p == CameraStreamProfile.Main ? 1 : 2)}",
        CameraVendor.Samsung   => $"/profile{(p == CameraStreamProfile.Main ? 1 : 3)}/media.smp",
        CameraVendor.ReCamera  => $"/live/{(p == CameraStreamProfile.Main ? 0 : 1)}",
        CameraVendor.FileSource => "(local file / image)",
        _                       => "(custom)"
    };

    private static string BuildPath(CameraVendor v, CameraStreamProfile p, int channel) => v switch
    {
        // Dahua: subtype 0=main, 1=sub
        CameraVendor.Dahua =>
            $"cam/realmonitor?channel={channel.ToString(CultureInfo.InvariantCulture)}&subtype={(p == CameraStreamProfile.Main ? 0 : 1)}",

        // Hikvision: /Streaming/Channels/{channel}{stream}, np. 101 = ch1 main, 102 = ch1 sub
        CameraVendor.Hikvision =>
            $"Streaming/Channels/{channel.ToString(CultureInfo.InvariantCulture)}{(p == CameraStreamProfile.Main ? "01" : "02")}",

        // Axis: profile po nazwie. "Quality" zwykle istnieje OOTB; sub-stream konfigurowalny per kamera.
        CameraVendor.Axis =>
            $"axis-media/media.amp?streamprofile={(p == CameraStreamProfile.Main ? "Quality" : "Mobile")}",

        // Bosch: /rtsp_tunnel?inst={1|2}; line/inst zależy od modelu, najczęściej 1=main, 2=sub
        CameraVendor.Bosch =>
            $"rtsp_tunnel?inst={(p == CameraStreamProfile.Main ? 1 : 2)}",

        // Samsung / Hanwha Wisenet: /profile{N}/media.smp; profile1 zwykle main, profile3 sub
        CameraVendor.Samsung =>
            $"profile{(p == CameraStreamProfile.Main ? 1 : 3)}/media.smp",

        // reCamera (Seeed): /live/{0|1}; w domyślnym firmware bez auth
        CameraVendor.ReCamera =>
            $"live/{(p == CameraStreamProfile.Main ? 0 : 1)}",

        _ => string.Empty
    };

    /// <summary>
    /// Zwraca URL do natychmiastowego snapshot-a JPEG po HTTP (dla vendorów, którzy
    /// udostępniają taki endpoint — DAHUA, Hikvision, Axis). Dzięki temu omijamy
    /// RTSP + oczekiwanie na keyframe → snapshot w 200-500ms zamiast 2-4s.
    /// Zwraca null gdy vendor nie ma takiego endpointu albo brakuje hosta.
    /// </summary>
    public static string? BuildHttpSnapshotUrl(Camera c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (string.IsNullOrWhiteSpace(c.Host)) return null;

        // HTTP standardowo na 80, ale Dahua/Hikvision często wystawiają na innym.
        // Używamy domyślnego 80 — użytkownik może wpisać Custom SourceUrl jeśli kamera słucha na innym.
        var ch = c.Channel > 0 ? c.Channel : 1;
        var creds = BuildCreds(c.Username, c.Password);
        var host = c.Host;

        return c.Vendor switch
        {
            // DAHUA: /cgi-bin/snapshot.cgi?channel=N (1-indexed), auth: Digest lub Basic
            CameraVendor.Dahua =>
                $"http://{creds}{host}/cgi-bin/snapshot.cgi?channel={ch.ToString(CultureInfo.InvariantCulture)}",

            // Hikvision: /ISAPI/Streaming/channels/{ch}01/picture, auth: Digest
            CameraVendor.Hikvision =>
                $"http://{creds}{host}/ISAPI/Streaming/channels/{ch.ToString(CultureInfo.InvariantCulture)}01/picture",

            // Axis: /axis-cgi/jpg/image.cgi — auth: Digest, resolution domyślna
            CameraVendor.Axis =>
                $"http://{creds}{host}/axis-cgi/jpg/image.cgi",

            _ => null
        };
    }

    private static string BuildCreds(string? user, string? pass)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass)) return string.Empty;
        // proste url-encode (zachowawcze — tylko najczęstsze problematyczne znaki)
        static string Esc(string? s) =>
            (s ?? string.Empty)
                .Replace("@", "%40", StringComparison.Ordinal)
                .Replace(":", "%3A", StringComparison.Ordinal)
                .Replace("/", "%2F", StringComparison.Ordinal);
        return $"{Esc(user)}:{Esc(pass)}@";
    }
}
