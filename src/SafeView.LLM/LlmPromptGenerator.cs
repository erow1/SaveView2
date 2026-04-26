using System.Text.Json;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.LLM;
using SafeView.Application.Abstractions.Vllm;
using SafeView.Domain.Vllm;

namespace SafeView.LLM;

/// <summary>
/// Generator szablonów VLLM — woła LLM z calibrated meta-promptem, który:
///  1. Przedstawia LLM-owi kontekst systemu SafeView (pipeline BHP, VLLM validation)
///  2. Podaje best-practice (CoT, anti-FP, severity rubric, calibration, image quality)
///  3. Wymusza strict output JSON z polami: name, category, description, system_prompt,
///     user_template, schema_json, recommended_min_confidence, default_min_severity,
///     require_good_image_quality
///  4. Pokazuje few-shot przykład dobrego szablonu
///
/// Meta-prompt jest w języku angielskim — modele są stabilniejsze po EN, a content
/// opisów/templatów generowany do konkretnych pól może być PL/EN wg instrukcji.
/// </summary>
public sealed class LlmPromptGenerator : IPromptGenerator
{
    private readonly IChatClientFactory _factory;
    private readonly ILogger<LlmPromptGenerator> _log;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public LlmPromptGenerator(IChatClientFactory factory, ILogger<LlmPromptGenerator> log)
    {
        _factory = factory;
        _log = log;
    }

    private const string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["name", "category", "description", "system_prompt", "user_template",
                       "schema_json", "recommended_min_confidence", "default_min_severity", "require_good_image_quality"],
          "properties": {
            "name": { "type": "string", "maxLength": 80 },
            "category": { "type": "string", "maxLength": 30 },
            "description": { "type": "string", "maxLength": 300 },
            "system_prompt": { "type": "string" },
            "user_template": { "type": "string" },
            "schema_json": { "type": "string" },
            "recommended_min_confidence": { "type": "number", "minimum": 0.3, "maximum": 0.95 },
            "default_min_severity": { "type": "string", "enum": ["none","low","medium","high","critical"] },
            "require_good_image_quality": { "type": "boolean" }
          }
        }
        """;

    // Common response schema that generated user_template's VLLM must produce — same as built-in templates use.
    private const string ExpectedResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["confirmed","confidence","severity","reason","image_quality","recommended_action"],"properties":{"observations":{"type":"array","items":{"type":"string"}},"alternative_explanations":{"type":"array","items":{"type":"string"}},"severity":{"type":"string","enum":["none","low","medium","high","critical"]},"confidence":{"type":"number","minimum":0,"maximum":1},"confirmed":{"type":"boolean"},"image_quality":{"type":"string","enum":["good","acceptable","poor"]},"reason":{"type":"string"},"recommended_action":{"type":"string","enum":["ignore","monitor","investigate","alert","emergency"]}}}
        """;

    private const string MetaPrompt = """
        You are a senior industrial safety (BHP) prompt engineer. You write validation prompts for a
        vision LLM (VLLM) that double-checks YOLO detections in Polish factories — reducing false positives.

        USER WILL DESCRIBE a scenario in plain language (Polish or English). You generate a complete
        production-ready template for this scenario.

        CRITICAL RULES for your output:

        1) `system_prompt` MUST be in ENGLISH (models are more stable with EN instructions).
           It MUST include, in this order:
              - Role ("You are a senior industrial safety analyst reviewing a still frame…")
              - REASONING PROTOCOL: observe → list alternative explanations → decide
              - SEVERITY RUBRIC: none/low/medium/high/critical with definitions
              - CONFIDENCE CALIBRATION: >=0.9 unambiguous, 0.7-0.9 likely, <0.7 do NOT confirm
              - IMAGE QUALITY self-assessment: good/acceptable/poor, poor => confirmed=false
              - Scenario-specific hints: what to look for, what common false positives to exclude
              - Instruction that prose (reason, observations) must be in POLISH
              - Output: strict JSON matching the given schema, no text outside JSON

        2) `user_template` MUST be in POLISH. It MUST use placeholders:
              {camera}, {zone}, {trigger}, {labels}, {detections}, {time_of_day}
           Keep it concise (2-5 sentences). Describe context + the specific check.

        3) `schema_json` MUST be exactly this fixed schema (copy verbatim):
           %EXPECTED_RESPONSE_SCHEMA%

        4) `recommended_min_confidence` calibration:
              - High-cost false positives (intruder, smoking): 0.75-0.85
              - Critical hazards where missing is worse (fire, fall): 0.50-0.65
              - Regular PPE compliance: 0.65-0.75

        5) `default_min_severity`:
              - "medium" for most PPE/compliance scenarios (filter noise)
              - "high" for hazards requiring immediate action
              - "low" for weak single-frame signals (loitering, crowding)

        6) `require_good_image_quality`:
              - true for judgment calls requiring detail (PPE, intruder identification)
              - false for critical hazards where you'd rather false-alarm (fire, fall, forklift collision)

        7) `name` — short noun phrase, Polish. Pattern "[Kategoria] — [co wykrywa]". Max 80 chars.
        8) `category` — one of: PPE, Pożar, Strefa, Wypadek, Ruch, Maszyny, Środowisko, Wysokość, Custom.
        9) `description` — 1-3 sentences in Polish explaining when operator should use this template.

        ANTI-FALSE-POSITIVE GUIDELINES (weave into system_prompt):
           - Enumerate common look-alikes the model might confuse for the target
           - Require image_quality check — poor means don't alert
           - Mention NOT to judge on protected attributes (ethnicity, gender, clothing color alone)

        FEW-SHOT EXAMPLE:
        User description: "chcę wykrywać czy pracownik ma kask"
        Your output (values shortened here for space — actual output has full prompts):
        {
          "name": "PPE — brak kasku ochronnego",
          "category": "PPE",
          "description": "Weryfikuje czy widoczne osoby mają założone kaski ochronne w strefie wymagającej PPE.",
          "system_prompt": "You are a senior industrial safety analyst reviewing a still frame… [full English CoT/severity/quality rubric + hints for helmet detection]",
          "user_template": "Kontekst: {labels} na '{camera}', strefa '{zone}' (trigger '{trigger}'). Sprawdź czy osoby mają założone kaski ochronne…",
          "schema_json": "[fixed schema]",
          "recommended_min_confidence": 0.7,
          "default_min_severity": "medium",
          "require_good_image_quality": true
        }

        Output ONLY the JSON object. No prose before or after.
        """;

    public async Task<GeneratedTemplate> GenerateAsync(string userDescription, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userDescription))
            return Error("Pusty opis — nie mam z czego generować.");

        var systemPrompt = MetaPrompt.Replace("%EXPECTED_RESPONSE_SCHEMA%", ExpectedResponseSchema, StringComparison.Ordinal);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, $"Opis scenariusza:\n{userDescription.Trim()}")
        };

        return await RunAsync(messages, ct).ConfigureAwait(false);
    }

    public async Task<GeneratedTemplate> RefineAsync(GeneratedTemplate current, string feedback, CancellationToken ct = default)
    {
        if (current is null || string.IsNullOrWhiteSpace(current.SystemPrompt))
            return Error("Brak wyjściowego szablonu do poprawy.");
        if (string.IsNullOrWhiteSpace(feedback))
            return Error("Pusty feedback — nie wiem co poprawić.");

        var systemPrompt = MetaPrompt.Replace("%EXPECTED_RESPONSE_SCHEMA%", ExpectedResponseSchema, StringComparison.Ordinal);
        // Do meta-promptu doklejamy instrukcję "ref mode" — wskazujemy że to iteracja
        var refineInstruction = """

            CONTEXT: This is a REFINEMENT of an existing template. User will provide:
              - The CURRENT template (in JSON)
              - Specific FEEDBACK on what to change
            You MUST return a complete updated JSON template — not a diff. Preserve anything not
            explicitly mentioned in the feedback. Do NOT break the schema (fields must stay present).
            Focus edits on the parts the user mentioned.
            """;

        var currentJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            name = current.Name,
            category = current.Category,
            description = current.Description,
            system_prompt = current.SystemPrompt,
            user_template = current.UserTemplate,
            schema_json = current.SchemaJson,
            recommended_min_confidence = current.RecommendedMinConfidence,
            default_min_severity = current.DefaultMinSeverity.ToString().ToLowerInvariant(),
            require_good_image_quality = current.RequireGoodImageQuality
        }, IndentedJson);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt + refineInstruction),
            new(ChatRole.User,
                $"CURRENT template:\n{currentJson}\n\nUSER FEEDBACK:\n{feedback.Trim()}\n\n" +
                "Return the updated template as JSON (same schema, complete).")
        };

        return await RunAsync(messages, ct).ConfigureAwait(false);
    }

    private async Task<GeneratedTemplate> RunAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        var options = new ChatOptions(
            Model: null, // uses default configured text model (nie trzeba tu vision-capable)
            Temperature: 0.3, // trochę kreatywności na promptach, ale stabilnie
            MaxTokens: 3000,
            JsonSchema: OutputSchema);

        IChatClient chat;
        try { chat = await _factory.GetForAsync(null, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("PromptGenerator: brak skonfigurowanego providera LLM ({Msg})", ex.Message);
            return Error("Brak skonfigurowanego dostawcy LLM. Dodaj na /admin/llm-providers.");
        }

        try
        {
            var resp = await chat.ChatAsync(messages, options, ct).ConfigureAwait(false);
            if (!resp.Success)
            {
                _log.LogWarning("PromptGenerator LLM error: {Err}", resp.ErrorMessage);
                return Error($"LLM error: {resp.ErrorMessage}");
            }
            return Parse(resp.Content);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PromptGenerator exception");
            return Error($"Exception: {ex.Message}");
        }
    }

    private GeneratedTemplate Parse(string raw)
    {
        var cleaned = ExtractJson(raw);
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            string Str(string key) => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? string.Empty) : string.Empty;

            double confidence = root.TryGetProperty("recommended_min_confidence", out var cEl) && cEl.TryGetDouble(out var d)
                ? Math.Clamp(d, 0.3, 0.95) : 0.7;
            var severity = Str("default_min_severity").ToLowerInvariant() switch
            {
                "none" => ResponseSeverity.None,
                "low" => ResponseSeverity.Low,
                "high" => ResponseSeverity.High,
                "critical" => ResponseSeverity.Critical,
                _ => ResponseSeverity.Medium
            };
            bool requireQuality = !root.TryGetProperty("require_good_image_quality", out var qEl)
                                   || qEl.ValueKind != JsonValueKind.False;

            return new GeneratedTemplate(
                Name: Str("name"),
                Category: string.IsNullOrWhiteSpace(Str("category")) ? "Custom" : Str("category"),
                Description: Str("description"),
                SystemPrompt: Str("system_prompt"),
                UserTemplate: Str("user_template"),
                SchemaJson: string.IsNullOrWhiteSpace(Str("schema_json")) ? ExpectedResponseSchema : Str("schema_json"),
                RecommendedMinConfidence: confidence,
                DefaultMinSeverity: severity,
                RequireGoodImageQuality: requireQuality);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "PromptGenerator: invalid JSON from LLM: {Raw}", raw);
            return Error("LLM zwrócił nieprawidłowy JSON — spróbuj ponownie lub użyj innego modelu.");
        }
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            var closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closing > 0) trimmed = trimmed[..closing];
            trimmed = trimmed.Trim();
        }
        if (!trimmed.StartsWith('{'))
        {
            var first = trimmed.IndexOf('{');
            var last = trimmed.LastIndexOf('}');
            if (first >= 0 && last > first) trimmed = trimmed[first..(last + 1)];
        }
        return trimmed;
    }

    private static GeneratedTemplate Error(string msg)
        => new(string.Empty, "Custom", string.Empty, string.Empty, string.Empty, string.Empty, 0.7,
            ResponseSeverity.Medium, true, ErrorMessage: msg);
}
