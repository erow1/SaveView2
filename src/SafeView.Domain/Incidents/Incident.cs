using SafeView.Domain.Common;

namespace SafeView.Domain.Incidents;

public enum IncidentSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum IncidentStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2,
    FalsePositive = 3
}

/// <summary>Wynik analizy LLM dla incydentu — strukturalna ocena false-positive + rekomendacja.</summary>
public sealed class IncidentLlmAnalysis
{
    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    public string Model { get; set; } = string.Empty;

    /// <summary>Krótkie podsumowanie zdarzenia (1–2 zdania).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Prawdopodobieństwo, że to false-positive (0..1).</summary>
    public double FalsePositiveProbability { get; set; }

    /// <summary>Rekomendowane działanie: "monitor", "investigate", "escalate", "dismiss".</summary>
    public string RecommendedAction { get; set; } = "monitor";

    /// <summary>Uzasadnienie (1–3 zdania) — co LLM dostrzegł.</summary>
    public string Rationale { get; set; } = string.Empty;
}

/// <summary>Pojedynczy box wykryty w klatce i zapisany razem z incydentem.</summary>
public sealed class IncidentDetection
{
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

/// <summary>
/// Incydent — wykryte zdarzenie. Na razie generowany przez pipeline detekcji
/// (jedno zdarzenie per klatka, w której coś reguła zaklasyfikowała).
/// W fazie 4 dojdą źródła ze stref i reguł kombinowanych.
/// </summary>
public sealed class Incident : Entity
{
    public string CameraId { get; set; } = string.Empty;
    public string CameraName { get; set; } = string.Empty;

    /// <summary>Trigger, który wywołał incydent (Faza 1+).</summary>
    public string? TriggerId { get; set; }
    public string? TriggerName { get; set; }

    /// <summary>ROI, na którym wykryto obiekt (Faza 1+).</summary>
    public string? RoiId { get; set; }
    public string? RoiName { get; set; }

    /// <summary>Strefa, w której trigger wypalił (Faza 1+).</summary>
    public string? ZoneId { get; set; }
    public string? ZoneName { get; set; }

    public string? ModelId { get; set; }
    public string? ModelName { get; set; }

    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;

    /// <summary>Kategoria zdarzenia (np. "ppe.no_helmet", "zone.intrusion", …).</summary>
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }

    /// <summary>Timestamp klatki, w której wykryto incydent.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ścieżka RELATYWNA (FileKind.Frame) do klatki dowodowej.</summary>
    public string? FrameRelativePath { get; set; }

    /// <summary>Ścieżka RELATYWNA (FileKind.Clip) do klipu dowodowego, jeśli nagrano.</summary>
    public string? ClipRelativePath { get; set; }

    public List<IncidentDetection> Detections { get; set; } = [];

    /// <summary>Strukturalna analiza LLM (JSON wygenerowany przez vLLM/OpenAI z guided schema).</summary>
    public IncidentLlmAnalysis? LlmAnalysis { get; set; }

    public string? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }

    // ── Faza D: pętla jakości VLLM ─────────────────────────────────────────
    /// <summary>
    /// ID szablonu VLLM który walidował ten alert. Null gdy trigger nie używał VLLM
    /// albo gdy VllmCheck nie miał podpiętego szablonu. Pozwala liczyć statystyki per-szablon.
    /// </summary>
    public string? VllmTemplateId { get; set; }

    /// <summary>Confidence zwrócony przez VLLM (0..1). Null gdy VLLM był wyłączony.</summary>
    public double? VllmConfidence { get; set; }

    /// <summary>
    /// Czy operator oznaczył ten incident jako false-positive. Feedback dla pętli jakości
    /// szablonu VLLM — agregowany w statystykach /admin/vllm-templates.
    /// </summary>
    public bool WasFalsePositive { get; set; }

    public DateTime? FalsePositiveAt { get; set; }
    public string? FalsePositiveByUserId { get; set; }

    // ── Faza 7: pętla jakości DetectionClass ──────────────────────────────
    /// <summary>
    /// ID klasy detekcji która wywołała trigger. Null gdy trigger używał legacy path
    /// (ModelId + Labels) albo w mieszanych warunkach — bierzemy pierwszy warunek z klasą.
    /// Pozwala liczyć statystyki per-klasa niezależnie od statystyk per-VLLM-template.
    /// </summary>
    public string? DetectionClassId { get; set; }

    /// <summary>Średnie confidence detekcji które wywołały trigger (0..1). Null gdy
    /// matchingDetections było puste (szczególny przypadek nie powinien się zdarzyć).</summary>
    public double? DetectionConfidence { get; set; }
}
