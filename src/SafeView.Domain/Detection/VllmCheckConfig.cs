using SafeView.Domain.Vllm;

namespace SafeView.Domain.Detection;

/// <summary>
/// Konfiguracja walidacji kontekstowej triggera przez VLLM (Vision LLM).
/// Gdy włączona, po spełnieniu warunków triggera nie odpalamy akcji od razu —
/// najpierw pytamy VLLM "czy to naprawdę ogień/osoba w strefie niebezpiecznej?"
/// z klatką jako obrazem. VLLM zwraca structured JSON z polem <c>confirmed</c>.
/// Akcje odpalają się dopiero gdy <c>confirmed == true</c>.
///
/// Użyteczne do redukcji false-positive z modeli YOLO:
///  • "Fire" wykryte na lampce gazowej kuchenki → VLLM: "nie, to kontrolowany płomień"
///  • "Person" w strefie zakazanej → VLLM: "tak, pracownik konserwacji w uniformie"
/// </summary>
public sealed class VllmCheckConfig
{
    /// <summary>Główny przełącznik. Gdy false, check jest pomijany (trigger fire = akcja od razu).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// System prompt (role definition) — zasady interpretacji dla VLLM. Nieobowiązkowy.
    /// Domyślny: <c>"You are a safety analyst validating camera detections..."</c>.
    /// </summary>
    public string SystemPrompt { get; set; } =
        "You are a safety analyst validating camera detections in industrial settings. " +
        "Answer strictly in the required JSON format.";

    /// <summary>
    /// Prompt szczegółowy z placeholderami, np.:
    /// <c>"Detection: {labels}. Camera: {camera}. Zone: {zone}. Is this a genuine {trigger}? Confirm true/false."</c>.
    /// Placeholders: <c>{camera}</c>, <c>{zone}</c>, <c>{trigger}</c>, <c>{labels}</c>, <c>{detections}</c>.
    /// </summary>
    public string PromptTemplate { get; set; } =
        "Camera '{camera}' detected {labels} in zone '{zone}' (trigger '{trigger}', {detections} objects). " +
        "Looking at the attached frame, is this a GENUINE safety concern requiring immediate action, " +
        "or a FALSE POSITIVE (e.g., staged test, authorized personnel, non-threat)? " +
        "Answer in JSON with: confirmed (bool), confidence (0..1), reason (short).";

    /// <summary>
    /// Czy dołączyć klatkę kamery do zapytania (multimodal). true = wymaga modelu vision
    /// (np. <c>gpt-4o</c>, <c>llava</c>, <c>qwen2-vl</c>). false = VLLM dostaje tylko tekst — tańsze, szybsze.
    /// </summary>
    public bool IncludeFrame { get; set; } = true;

    /// <summary>Nazwa modelu (override <see cref="Configuration.LlmOptions.DefaultModel"/>). Null = użyj domyślnego.</summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// Próg <c>confidence</c> (0..1) powyżej którego uznajemy odpowiedź VLLM za wiarygodną.
    /// Gdy VLLM zwraca confidence &lt; threshold → traktujemy jak nie-confirmed.
    /// </summary>
    public double MinConfidenceToConfirm { get; set; } = 0.6;

    /// <summary>
    /// Co zrobić gdy VLLM zwróci błąd (timeout, niedostępny, bad JSON):
    /// <c>false</c> (default) — fail-open: akcja leci mimo wszystko (bezpieczeństwo > oszczędność)
    /// <c>true</c> — fail-closed: akcja nie leci (mniej false positives, ryzyko pominięć)
    /// </summary>
    public bool RejectOnError { get; set; }

    // ── Rozszerzenia po Fazie A (biblioteka szablonów + structured severity) ─

    /// <summary>
    /// ID szablonu z <see cref="Vllm.PromptTemplate"/> z którego ta konfiguracja
    /// została załadowana. Null = konfiguracja ręczna. Pozwala UI pokazać "opiera się na szablonie X"
    /// i zaproponować re-sync gdy szablon został ulepszony.
    /// </summary>
    public string? TemplateId { get; set; }

    /// <summary>
    /// JSON schema odpowiedzi (strict). Null lub pusty → VLLMChecker użyje domyślnego schematu
    /// z severity/quality/action. Szablony noszą swój schema wyspecjalizowany do scenariusza.
    /// </summary>
    public string? SchemaJson { get; set; }

    /// <summary>
    /// Minimalna severity zwrócona przez VLLM aby trigger wypalił. Domyślnie Medium —
    /// "none" i "low" są traktowane jak VLLM-rejected (pipeline nie odpala akcji).
    /// </summary>
    public ResponseSeverity MinSeverity { get; set; } = ResponseSeverity.Medium;

    /// <summary>
    /// Gdy VLLM zwraca <c>image_quality=poor</c> i to jest true → traktujemy jak not-confirmed
    /// (bezpieczniej nie alarmować na niewyraźnym obrazie). Dla scenariuszy krytycznych
    /// (pożar/awaria) ustaw false — lepiej false alarm niż pominięcie.
    /// </summary>
    public bool RequireGoodImageQuality { get; set; } = true;

    /// <summary>
    /// ID dostawcy LLM (<c>LlmProvider</c>) który ma obsłużyć ten check. Null = domyślny provider
    /// (z /admin/llm-providers, z flagą IsDefault). Pozwala mieć różne modele dla różnych triggerów
    /// — np. pożar przez szybki Ollama 7B, a analizy intruza przez większy model firmowy.
    /// </summary>
    public string? LlmProviderId { get; set; }
}
