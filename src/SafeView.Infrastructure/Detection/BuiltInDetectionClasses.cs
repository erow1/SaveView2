using SafeView.Domain.Detection;

namespace SafeView.Infrastructure.Detection;

/// <summary>
/// Wbudowane klasy detekcji dla typowych scenariuszy BHP — ładowane przez
/// <see cref="DetectionClassSeeder"/> przy starcie. Większość to typu
/// <see cref="DetectionClassKind.Text"/> (open-vocab przez YOLO-World/YOLOE/OWLv2),
/// kilka to <see cref="DetectionClassKind.ClosedSetBinding"/> bind-owanych do
/// konkretnych klas COCO (yolov8s-coco) — używane w scenariuszach kompozycyjnych
/// jak "osoba używa telefonu" przez containment.
///
/// User może je duplikować jako bazę własnych klas (built-in są read-only).
/// Prompty po angielsku dla stabilności modeli CLIP-based; <see cref="DetectionClass.TextPromptPl"/>
/// niesie polski wariant do wyświetlenia w UI.
///
/// <see cref="DetectionClassKind.ClosedSetBinding"/> klasy są warunkowe — wymagają
/// że odpowiedni model jest seedowany w DB (rozwiązywane w runtime przez resolver
/// w <see cref="DetectionClassSeeder"/>). Gdy model nie istnieje, klasa jest skipowana.
/// </summary>
public static class BuiltInDetectionClasses
{
    /// <summary>
    /// Zwraca listę wbudowanych klas. <paramref name="resolveModelIdByName"/> mapuje
    /// nazwę modelu (folder w runtime/models/) na ID w DB — używany przez ClosedSet klasy.
    /// Null = pomijamy ClosedSet bindings (np. w testach jednostkowych bez DB).
    /// </summary>
    public static IEnumerable<DetectionClass> All(Func<string, string?>? resolveModelIdByName = null)
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

        // ─── Obiekty (COCO closed-set) ──────────────────────────────────────
        // Bind-owane do bundled yolov8s-coco (fallback yolov8n-coco). Używane jako klasy
        // bazowe w kompozycjach przez ContainmentRule — np. "osoba używa telefonu":
        // warunek na coco-person + Containment(ContainsAny, coco-cell-phone, Center).
        // Pomijane gdy żaden COCO model nie jest seed-owany (np. user usunął runtime/models/).
        var cocoModelId = resolveModelIdByName?.Invoke("yolov8s-coco")
                       ?? resolveModelIdByName?.Invoke("yolov8n-coco");
        if (!string.IsNullOrEmpty(cocoModelId))
        {
            yield return ClosedSet("coco-person",
                "Osoba (COCO)",
                "Osoba wykryta przez ogólny model COCO. Klasyczna detekcja closed-set — używaj jako klasa A w containment-ach typu \"osoba bez kasku\", \"osoba z telefonem\".",
                "Obiekty",
                modelId: cocoModelId,
                label: "person",
                recommendedMinConfidence: 0.30);

            yield return ClosedSet("coco-cell-phone",
                "Telefon komórkowy (COCO)",
                "Telefon komórkowy / smartphone. Użyj jako klasa B w containment do scenariusza \"osoba używa telefonu\" — Containment(ContainsAny, Center) na warunku z osobą.",
                "Obiekty",
                modelId: cocoModelId,
                label: "cell phone",
                recommendedMinConfidence: 0.30);
        }
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

    private static DetectionClass ClosedSet(
        string builtInKey,
        string name,
        string description,
        string category,
        string modelId,
        string label,
        double recommendedMinConfidence)
        => new()
        {
            Name = name,
            Description = description,
            Category = category,
            Kind = DetectionClassKind.ClosedSetBinding,
            ClosedSetModelId = modelId,
            ClosedSetLabel = label,
            RecommendedMinConfidence = recommendedMinConfidence,
            IsBuiltIn = true,
            BuiltInKey = builtInKey
        };
}
