using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Detection;

/// <summary>
/// Wbudowane klasy detekcji dla typowych scenariuszy BHP — ładowane przez
/// <see cref="DetectionClassSeeder"/> przy starcie. Wszystkie są typu
/// <see cref="DetectionClassKind.Text"/> — kompatybilne z dowolnym modelem obsługującym
/// <c>ModelCapabilities.TextPrompts</c> (YOLO-World, YOLOE). User może je duplikować jako
/// bazę własnych klas (built-in są read-only).
///
/// Prompty po angielsku dla stabilności modeli CLIP-based; <see cref="DetectionClass.TextPromptPl"/>
/// niesie polski wariant do wyświetlenia w UI.
/// </summary>
public static class BuiltInDetectionClasses
{
    public static IEnumerable<DetectionClass> All()
    {
        // ─── PPE ────────────────────────────────────────────────────────────
        yield return Text("ppe-hard-hat",
            "Kask ochronny",
            "Osoba nosząca kask ochronny (budowlany / przemysłowy).",
            "PPE",
            textEn: "construction worker wearing hard hat",
            textPl: "pracownik w kasku ochronnym",
            recommendedMinConfidence: 0.35);

        yield return Text("ppe-no-hard-hat",
            "Brak kasku ochronnego",
            "Osoba bez kasku w strefie wymagającej ochrony głowy.",
            "PPE",
            textEn: "person without hard hat or helmet",
            textPl: "osoba bez kasku ochronnego",
            recommendedMinConfidence: 0.40);

        yield return Text("ppe-hi-vis-vest",
            "Kamizelka odblaskowa",
            "Osoba w kamizelce odblaskowej (yellow/orange high-vis).",
            "PPE",
            textEn: "person wearing high-visibility safety vest",
            textPl: "pracownik w kamizelce odblaskowej",
            recommendedMinConfidence: 0.35);

        yield return Text("ppe-no-hi-vis",
            "Brak kamizelki odblaskowej",
            "Osoba bez kamizelki odblaskowej w strefie ruchu pojazdów.",
            "PPE",
            textEn: "person without high-visibility safety vest",
            textPl: "osoba bez kamizelki odblaskowej",
            recommendedMinConfidence: 0.40);

        yield return Text("ppe-safety-gloves",
            "Rękawice ochronne",
            "Osoba nosząca rękawice ochronne.",
            "PPE",
            textEn: "person wearing safety work gloves",
            textPl: "pracownik w rękawicach ochronnych",
            recommendedMinConfidence: 0.35);

        yield return Text("ppe-safety-glasses",
            "Okulary ochronne",
            "Osoba nosząca okulary ochronne.",
            "PPE",
            textEn: "person wearing safety glasses or goggles",
            textPl: "osoba w okularach ochronnych",
            recommendedMinConfidence: 0.35);

        // ─── Pożar / zagrożenia ───────────────────────────────────────────────
        yield return Text("hazard-fire",
            "Pożar / płomienie",
            "Widoczne płomienie w kadrze.",
            "Pożar",
            textEn: "open flames or fire",
            textPl: "płomienie lub pożar",
            recommendedMinConfidence: 0.50);

        yield return Text("hazard-smoke",
            "Dym",
            "Widoczny dym w kadrze.",
            "Pożar",
            textEn: "visible smoke cloud",
            textPl: "kłęby dymu",
            recommendedMinConfidence: 0.50);

        yield return Text("hazard-spill",
            "Rozlanie cieczy",
            "Plama cieczy na podłodze — potencjalne ryzyko poślizgnięcia.",
            "Zagrożenia",
            textEn: "liquid spill puddle on the floor",
            textPl: "rozlanie cieczy na podłodze",
            recommendedMinConfidence: 0.40);

        // ─── Ruch / pojazdy ─────────────────────────────────────────────────
        yield return Text("traffic-person",
            "Osoba",
            "Osoba w kadrze (ogólna detekcja).",
            "Ruch",
            textEn: "person",
            textPl: "osoba",
            recommendedMinConfidence: 0.30);

        yield return Text("traffic-forklift",
            "Wózek widłowy",
            "Wózek widłowy w kadrze.",
            "Ruch",
            textEn: "forklift truck",
            textPl: "wózek widłowy",
            recommendedMinConfidence: 0.35);

        yield return Text("traffic-vehicle",
            "Pojazd",
            "Dowolny pojazd (samochód, ciężarówka, van).",
            "Ruch",
            textEn: "vehicle or truck or car",
            textPl: "pojazd",
            recommendedMinConfidence: 0.30);

        // ─── Bezpieczeństwo obiektu ─────────────────────────────────────────
        yield return Text("safety-fire-extinguisher",
            "Gaśnica",
            "Gaśnica przeciwpożarowa (do weryfikacji dostępności w strefie).",
            "Bezpieczeństwo",
            textEn: "fire extinguisher",
            textPl: "gaśnica",
            recommendedMinConfidence: 0.40);

        yield return Text("safety-open-electrical-cabinet",
            "Otwarta szafa rozdzielcza",
            "Otwarta drzwi szafy elektrycznej / rozdzielczej.",
            "Bezpieczeństwo",
            textEn: "open electrical cabinet with exposed wiring",
            textPl: "otwarta szafa rozdzielcza",
            recommendedMinConfidence: 0.45);
    }

    private static DetectionClass Text(
        string builtInKey,
        string name,
        string description,
        string category,
        string textEn,
        string textPl,
        double recommendedMinConfidence)
        => new()
        {
            Name = name,
            Description = description,
            Category = category,
            Kind = DetectionClassKind.Text,
            TextPrompt = textEn,
            TextPromptPl = textPl,
            RecommendedMinConfidence = recommendedMinConfidence,
            IsBuiltIn = true,
            BuiltInKey = builtInKey
        };
}
