namespace SafeView.Domain.Vllm;

/// <summary>
/// Drabina nasilenia — zwracana przez VLLM, odczytywana przez pipeline jako gate
/// przed dispatch-em akcji (trigger fires tylko gdy <c>severity &gt;= trigger.MinSeverity</c>).
///
/// Mapowanie na recommended_action (patrz <see cref="RecommendedAction"/>):
///  • None → nic nie rób
///  • Low → monitor (log)
///  • Medium → investigate (akcje informacyjne)
///  • High → alert (email/webhook/SMS)
///  • Critical → emergency (wszystko + eskalacja)
/// </summary>
public enum ResponseSeverity
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>Rekomendowana akcja jaką model sugeruje operatorowi.</summary>
public enum RecommendedAction
{
    Ignore = 0,
    Monitor = 1,
    Investigate = 2,
    Alert = 3,
    Emergency = 4
}

/// <summary>Samoocena VLLM czy obraz jest wystarczająco czytelny do wiarygodnej oceny.</summary>
public enum ImageQuality
{
    Good = 0,
    Acceptable = 1,
    Poor = 2
}
