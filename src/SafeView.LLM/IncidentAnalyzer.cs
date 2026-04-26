using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.LLM;
using SafeView.Domain.Incidents;

namespace SafeView.LLM;

public sealed class IncidentAnalyzer : IIncidentAnalyzer
{
    private readonly IChatClientFactory _factory;
    private readonly ILogger<IncidentAnalyzer> _log;

    private const string SystemPrompt = """
        Jesteś asystentem BHP w fabryce. Otrzymujesz JSON z opisem zdarzenia wykrytego
        przez kamerę i model wizyjny. Twoim zadaniem jest:
        1) ocenić prawdopodobieństwo, że to false-positive (0..1),
        2) zarekomendować działanie ('monitor', 'investigate', 'escalate', 'dismiss'),
        3) krótko podsumować i uzasadnić.

        Odpowiedz WYŁĄCZNIE poprawnym JSON-em zgodnym ze schematem.
        Nie dodawaj ` ` ani komentarza.
        """;

    // JSON Schema (draft-07 / OpenAI subset) wymuszany przez vLLM guided_json / OpenAI strict.
    private const string ResponseSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "summary": { "type": "string", "maxLength": 400 },
            "false_positive_probability": { "type": "number", "minimum": 0, "maximum": 1 },
            "recommended_action": {
              "type": "string",
              "enum": ["monitor", "investigate", "escalate", "dismiss"]
            },
            "rationale": { "type": "string", "maxLength": 800 }
          },
          "required": ["summary", "false_positive_probability", "recommended_action", "rationale"]
        }
        """;

    private static readonly JsonSerializerOptions SerializeOpts = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions DeserializeOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public IncidentAnalyzer(IChatClientFactory factory, ILogger<IncidentAnalyzer> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<IncidentLlmAnalysis?> AnalyzeAsync(Incident incident, CancellationToken ct = default)
    {
        IChatClient chat;
        try { chat = await _factory.GetForAsync(null, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("IncidentAnalyzer: brak skonfigurowanego providera LLM ({Msg})", ex.Message);
            return null;
        }

        var userPayload = JsonSerializer.Serialize(new
        {
            id = incident.Id,
            occurredAt = incident.OccurredAt,
            camera = new { incident.CameraId, name = incident.CameraName },
            model = new { incident.ModelId, name = incident.ModelName },
            category = incident.Category,
            severity = incident.Severity.ToString(),
            summary = incident.Summary,
            details = incident.Details,
            detections = incident.Detections.Select(d => new
            {
                d.Label,
                confidence = d.Confidence,
                box = new { d.X, d.Y, d.Width, d.Height }
            })
        }, SerializeOpts);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userPayload)
        };

        var resp = await chat.ChatAsync(messages,
            new ChatOptions(Temperature: 0.1, MaxTokens: 512, JsonSchema: ResponseSchema), ct)
            .ConfigureAwait(false);

        if (!resp.Success)
        {
            _log.LogWarning("LLM analysis failed for incident {Id}: {Err}", incident.Id, resp.ErrorMessage);
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AnalysisDto>(resp.Content, DeserializeOpts);
            if (parsed is null) return null;

            return new IncidentLlmAnalysis
            {
                AnalyzedAt = DateTime.UtcNow,
                Model = resp.Model ?? "?",
                Summary = parsed.Summary ?? string.Empty,
                FalsePositiveProbability = Math.Clamp(parsed.FalsePositiveProbability, 0, 1),
                RecommendedAction = string.IsNullOrWhiteSpace(parsed.RecommendedAction) ? "monitor" : parsed.RecommendedAction,
                Rationale = parsed.Rationale ?? string.Empty
            };
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "LLM response was not valid JSON for incident {Id}. Raw: {Raw}", incident.Id, resp.Content);
            return null;
        }
    }

    private sealed record AnalysisDto(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("false_positive_probability")] double FalsePositiveProbability,
        [property: JsonPropertyName("recommended_action")] string? RecommendedAction,
        [property: JsonPropertyName("rationale")] string? Rationale);
}
