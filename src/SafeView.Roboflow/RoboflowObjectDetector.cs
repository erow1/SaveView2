using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.ML;
using SafeView.Domain.ML;

namespace SafeView.Roboflow;

/// <summary>
/// Detektor uruchamiany przez API Roboflow (serverless lub Hosted Inference).
/// Payload: base64 obrazu POST jako form-urlencoded body (klasyczny format Roboflow detect).
/// </summary>
public sealed class RoboflowObjectDetector : IObjectDetector
{
    private static readonly Uri BaseUri = new("https://detect.roboflow.com/");

    private readonly HttpClient _http;
    private readonly ILogger<RoboflowObjectDetector> _log;

    public string Backend => "roboflow";

    public RoboflowObjectDetector(HttpClient http, ILogger<RoboflowObjectDetector> log)
    {
        _http = http;
        _log = log;
        if (_http.BaseAddress is null) _http.BaseAddress = BaseUri;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<DetectionResult> DetectAsync(MLModel model, string imagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Backend != DetectorBackend.Roboflow)
            return DetectionResult.Failed("Model nie jest Roboflow.");
        if (string.IsNullOrWhiteSpace(model.RoboflowModelId))
            return DetectionResult.Failed("RoboflowModelId nie ustawione.");
        if (string.IsNullOrWhiteSpace(model.RoboflowApiKey))
            return DetectionResult.Failed("RoboflowApiKey nie ustawione.");
        if (!File.Exists(imagePath))
            return DetectionResult.Failed($"Obraz nie istnieje: {imagePath}");

        var sw = Stopwatch.StartNew();

        var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        var base64 = Convert.ToBase64String(bytes);

        var url = $"{model.RoboflowModelId}?api_key={Uri.EscapeDataString(model.RoboflowApiKey)}" +
                  $"&confidence={(int)(model.ConfidenceThreshold * 100)}" +
                  $"&overlap={(int)(model.IouThreshold * 100)}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(base64, System.Text.Encoding.ASCII, "application/x-www-form-urlencoded")
        };

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Roboflow request failed");
            return DetectionResult.Failed($"HTTP: {ex.Message}");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return DetectionResult.Failed($"HTTP {(int)resp.StatusCode}: {Truncate(body, 200)}");
            }

            RoboflowResponse? parsed;
            try
            {
                parsed = await resp.Content.ReadFromJsonAsync<RoboflowResponse>(ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return DetectionResult.Failed($"JSON parse error: {ex.Message}");
            }

            if (parsed is null) return DetectionResult.Failed("Pusta odpowiedź Roboflow");

            var dets = new List<Detection>(parsed.Predictions?.Count ?? 0);
            foreach (var p in parsed.Predictions ?? [])
            {
                var classId = model.Labels.IndexOf(p.Class);
                if (classId < 0) classId = 0;
                // Roboflow zwraca center x/y, width, height w px obrazu źródłowego
                var x = p.X - p.Width / 2;
                var y = p.Y - p.Height / 2;
                dets.Add(new Detection(classId, p.Class, p.Confidence, new BoundingBox(x, y, p.Width, p.Height)));
            }

            sw.Stop();
            return new DetectionResult(true, dets, parsed.Image?.Width ?? 0, parsed.Image?.Height ?? 0, sw.Elapsed);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ── DTO ────────────────────────────────────────────────────────────────────
    private sealed class RoboflowResponse
    {
        [JsonPropertyName("predictions")] public List<RoboflowPrediction>? Predictions { get; set; }
        [JsonPropertyName("image")]        public RoboflowImage? Image { get; set; }
    }

    private sealed class RoboflowPrediction
    {
        [JsonPropertyName("x")]          public float X { get; set; }
        [JsonPropertyName("y")]          public float Y { get; set; }
        [JsonPropertyName("width")]      public float Width { get; set; }
        [JsonPropertyName("height")]     public float Height { get; set; }
        [JsonPropertyName("class")]      public string Class { get; set; } = string.Empty;
        [JsonPropertyName("confidence")] public float Confidence { get; set; }
    }

    private sealed class RoboflowImage
    {
        [JsonPropertyName("width")]  public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
    }
}
