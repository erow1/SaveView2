using SafeView.Domain.Vllm;

namespace SafeView.Infrastructure.Vllm;

/// <summary>
/// Biblioteka wbudowanych szablonów promptów dla VLLM — 12 typowych scenariuszy BHP.
///
/// Zasady wspólne (wpisane w każdy system prompt):
///  1. CHAIN-OF-THOUGHT: najpierw opisz co widzisz, potem rozważ alternatywy, potem decyduj.
///  2. ANTY-FALSE-POSITIVE: wymagaj "alternative_explanations" przed potwierdzeniem.
///  3. DRABINA SEVERITY: none/low/medium/high/critical + jawna rubryka.
///  4. SELF-ASSESSMENT jakości obrazu — VLLM ma wiedzieć że nie widzi wyraźnie.
///  5. REKOMENDOWANA AKCJA: ignore/monitor/investigate/alert/emergency — oddzielona od confirmed.
///  6. KALIBRACJA CONFIDENCE: widełki w prompt (0.9+ = od razu działam, &lt;0.7 = niepewne).
///
/// Output w języku angielskim (klucze JSON) — reason po polsku (czyta operator).
/// </summary>
internal static class BuiltInTemplates
{
    // Wspólny JSON schema — strict, używany przez wszystkie szablony.
    // VLLMChecker rozumie tylko ten format (fallback na legacy {confirmed,confidence,reason} dla backward compat).
    private const string CommonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["confirmed", "confidence", "severity", "reason", "image_quality", "recommended_action"],
          "properties": {
            "observations": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Krótkie obserwacje — co widoczne na obrazie, istotne dla oceny. Po polsku."
            },
            "alternative_explanations": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Inne wyjaśnienia tego co widać — anti-false-positive. Po polsku."
            },
            "severity": {
              "type": "string",
              "enum": ["none", "low", "medium", "high", "critical"]
            },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "confirmed": { "type": "boolean" },
            "image_quality": {
              "type": "string",
              "enum": ["good", "acceptable", "poor"]
            },
            "reason": { "type": "string", "description": "1-2 zdania uzasadnienia po polsku." },
            "recommended_action": {
              "type": "string",
              "enum": ["ignore", "monitor", "investigate", "alert", "emergency"]
            }
          }
        }
        """;

    // Wspólny fragment system promptu (reguły) — wklejany do każdego szablonu.
    private const string CommonSystemRules = """
        You are a senior industrial safety analyst (BHP) reviewing a still frame from a factory camera.
        Your decisions affect alarms sent to operators — false positives create alarm fatigue,
        false negatives miss real hazards. Be precise, not nervous.

        REASONING PROTOCOL (follow in order):
        1. OBSERVE — list only what is actually visible in the frame (no guessing).
        2. ALTERNATIVES — enumerate at least one plausible non-concerning explanation for what you see.
        3. DECIDE — only then output severity and confirmation.

        SEVERITY RUBRIC:
          none     — scene is unrelated to the scenario
          low      — possibly relevant but clearly no immediate issue
          medium   — likely a real concern, should be investigated
          high     — confirmed hazard, alert operator
          critical — imminent danger to life/health

        CONFIDENCE CALIBRATION:
          >= 0.9   — unambiguous, you would act on this personally
          0.7-0.9  — likely, but context/alternatives reduce certainty
          < 0.7    — uncertain — do NOT confirm

        IMAGE QUALITY:
          good       — details are clearly discernible
          acceptable — some details obscured (angle, distance, lighting) but judgement possible
          poor       — cannot reliably assess — set confirmed=false, severity=none

        OUTPUT: strict JSON matching the provided schema. All prose (reason, observations,
        alternative_explanations) MUST be in Polish. No text outside the JSON object.
        """;

    public static IEnumerable<PromptTemplate> All()
    {
        yield return Make(
            key: "ppe-helmet",
            name: "PPE — brak kasku ochronnego",
            category: "PPE",
            description: "Wykrywa czy osoba w kadrze nosi kask ochronny. Dla stref wymagających PPE.",
            scenarioEn: "Verify whether every visible person is wearing a hard hat / safety helmet.",
            confirmHint: "Confirm only if at least one person is clearly without a helmet AND is in a work area (not just passing through, not a visitor with a helmet carried).",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Sprawdź czy osoby widoczne w kadrze mają założone kaski ochronne. " +
                          "Potwierdź tylko gdy przynajmniej jedna osoba jest wyraźnie bez kasku, będąc w obszarze roboczym.",
            minConfidence: 0.7,
            severity: ResponseSeverity.Medium,
            requireGoodImage: true);

        yield return Make(
            key: "ppe-hiviz",
            name: "PPE — brak kamizelki odblaskowej",
            category: "PPE",
            description: "Wykrywa brak kamizelki odblaskowej u osób w strefie ruchu wózków / pojazdów.",
            scenarioEn: "Verify whether every visible person is wearing a high-visibility vest (hi-viz, orange/yellow).",
            confirmHint: "Confirm only if at least one person has no clearly visible high-visibility garment. A person may wear hi-viz under a jacket — be cautious. Weight reflective markings on jackets as hi-viz compliance.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Sprawdź czy osoby mają założone kamizelki odblaskowe (odblaskowy pomarańczowy/żółty). " +
                          "Uwzględnij kurtki z taśmami odblaskowymi jako spełniające wymóg.",
            minConfidence: 0.7,
            severity: ResponseSeverity.Medium,
            requireGoodImage: true);

        yield return Make(
            key: "fire-smoke",
            name: "Pożar / dym / iskry",
            category: "Pożar",
            description: "Weryfikacja czy widać rzeczywisty pożar/dym. Krytyczne — fail-open nawet przy słabym obrazie.",
            scenarioEn: "Verify whether there is genuine fire, smoke, or dangerous sparks in the frame.",
            confirmHint: "Fire/smoke detection is HIGH-STAKES — lean toward alerting unless you can clearly identify a benign alternative (steam, dust plume, controlled welding spark, LED glow, sun reflection, fog). If unsure between fire and steam — confirm at low severity for operator review.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy w kadrze widać rzeczywisty pożar, dym lub niebezpieczne iskry. " +
                          "Wyklucz: parę wodną, kurz, kontrolowane spawanie, odblaski LED, słońce, mgłę.",
            minConfidence: 0.55, // niższy próg — scenariusz krytyczny, lepiej false alarm niż pominięcie
            severity: ResponseSeverity.High,
            requireGoodImage: false); // nawet przy słabym obrazie chcemy alarm

        yield return Make(
            key: "intrusion-restricted-zone",
            name: "Intruz w strefie zakazanej",
            category: "Strefa",
            description: "Weryfikuje czy osoba w strefie ma tam prawo być (np. uniform, identyfikator, sprzęt).",
            scenarioEn: "Verify whether the detected person is an unauthorized intruder or authorized personnel.",
            confirmHint: "Confirm (intruder) only if person appears to lack company attire, ID badge, tools, or behavior consistent with scheduled work. A person in uniform with hi-viz is likely authorized. Behavior cues: walking purposefully with tools = worker; hesitating/looking around = possibly intruder. NEVER confirm based on ethnicity, gender, or clothing color alone.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy widoczna osoba wygląda na NIEUPRAWNIONĄ obecność w tej strefie. " +
                          "Wskazówki: brak odzieży roboczej/odblasków/identyfikatora, zachowanie niepewne, brak narzędzi. " +
                          "UWAGA: nie oceniaj na podstawie płci/wyglądu etnicznego/koloru ubrania.",
            minConfidence: 0.75, // wyższy próg — łatwo o false-positive na legalnej obecności
            severity: ResponseSeverity.High,
            requireGoodImage: true);

        yield return Make(
            key: "person-fallen",
            name: "Upadek / osoba leżąca",
            category: "Wypadek",
            description: "Wykrywa osobę w pozycji leżącej/skulonej w miejscu gdzie ludzie normalnie pracują stojąc.",
            scenarioEn: "Verify whether a person is lying on the ground or in an abnormal posture suggesting a fall/accident.",
            confirmHint: "Distinguish: (a) fallen/unconscious — body stretched out, static, at odd angle, (b) crouching for work — feet under knees, tools visible, (c) stretching/resting — typically standing or sitting upright. A person lying flat on an industrial floor is almost always an emergency.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy widoczna osoba upadła lub leży w nienaturalnej pozycji (poza miejscem odpoczynku). " +
                          "Rozróżnij: (a) leżąca/nieprzytomna, (b) kucająca przy pracy, (c) odpoczywająca. " +
                          "Osoba leżąca płasko na podłodze hali jest sytuacją krytyczną.",
            minConfidence: 0.7,
            severity: ResponseSeverity.High,
            requireGoodImage: false); // scenariusz krytyczny

        yield return Make(
            key: "forklift-pedestrian",
            name: "Wózek widłowy — pieszy zbyt blisko",
            category: "Ruch",
            description: "Weryfikuje czy osoba piesza jest w niebezpiecznej bliskości operującego wózka widłowego.",
            scenarioEn: "Verify whether a pedestrian is in the danger zone of an operating forklift (within ~2m of the forks or path of motion).",
            confirmHint: "Confirm if: (a) person within arm's length of forklift forks or mast, (b) person directly in path of moving forklift, (c) person crossing a forklift lane without awareness. Do NOT confirm: stationary forklift with driver away, person on a marked pedestrian walkway, person behind a safety barrier.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy pieszy jest w strefie niebezpiecznej operującego wózka widłowego (w zasięgu wideł lub na torze ruchu). " +
                          "Nie alarmuj jeśli wózek stoi, kierowca poza wózkiem, lub pieszy jest na oznakowanym przejściu.",
            minConfidence: 0.7,
            severity: ResponseSeverity.High,
            requireGoodImage: true);

        yield return Make(
            key: "machine-guard-open",
            name: "Osłona maszyny otwarta podczas pracy",
            category: "Maszyny",
            description: "Weryfikuje czy osłona bezpieczeństwa maszyny jest otwarta gdy maszyna pracuje.",
            scenarioEn: "Verify whether a machine guard/door is open while the machine appears to be operating.",
            confirmHint: "You need two signals: (1) guard visibly open/removed, (2) machine shows signs of operation (moving parts, lights, material flow). If only signal 1 — likely maintenance/setup, lower severity. If both — high severity.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy osłona bezpieczeństwa maszyny jest otwarta, a maszyna jednocześnie pracuje " +
                          "(ruch części, światła, przepływ materiału). Sama otwarta osłona bez pracy = prawdopodobnie serwis.",
            minConfidence: 0.7,
            severity: ResponseSeverity.High,
            requireGoodImage: true);

        yield return Make(
            key: "crowding",
            name: "Zbyt wiele osób w wąskim przejściu",
            category: "Strefa",
            description: "Wykrywa skupienie osób w strefie gdzie może dojść do utraty drożności ewakuacji.",
            scenarioEn: "Verify whether too many people are clustered in a narrow passage or evacuation route.",
            confirmHint: "Confirm only if: (a) ≥3 people clearly in a passageway narrower than normal working area, (b) static grouping (not just transient pass-by), (c) no visible coordination (not a safety briefing). A line of people moving through is usually fine.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy w wąskim przejściu/drodze ewakuacyjnej jest nadmierne skupienie osób " +
                          "(≥3 osoby stojące, nie tylko przechodzące). Odróżnij od odprawy/szkolenia w rozpoznawalnym miejscu.",
            minConfidence: 0.7,
            severity: ResponseSeverity.Medium,
            requireGoodImage: true);

        yield return Make(
            key: "spill-liquid",
            name: "Rozlanie / plama cieczy na podłodze",
            category: "Środowisko",
            description: "Wykrywa plamy cieczy które mogą powodować poślizg.",
            scenarioEn: "Verify whether there is a liquid spill on the floor that could cause a slipping hazard.",
            confirmHint: "Signs of spill: wet reflective patch on floor, discoloration, puddle, person stepping with caution around it. Distinguish from: floor markings (straight-line paint), cleaning in progress (mop/bucket visible), naturally wet area (near washing station).",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy na podłodze widać rozlaną ciecz (błyszcząca plama, kałuża, przebarwienie). " +
                          "Wyklucz: oznakowanie podłogi, trwającą pracę sprzątającą (mop/wiadro), stałe mokre strefy.",
            minConfidence: 0.7,
            severity: ResponseSeverity.Medium,
            requireGoodImage: true);

        yield return Make(
            key: "working-at-height",
            name: "Praca na wysokości bez zabezpieczenia",
            category: "Wysokość",
            description: "Weryfikuje czy osoba na wysokości (drabina, rusztowanie) używa szelek/zabezpieczeń.",
            scenarioEn: "Verify whether a person working at height has visible fall protection (harness, rails, tie-off).",
            confirmHint: "Confirm if: (a) person clearly elevated (>2m on ladder/scaffold/platform), (b) NO visible harness, tie-off line, or guard rails. Do NOT confirm if harness is present but partly obscured, or person is below threshold height.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy osoba na wysokości (>2m, drabina/rusztowanie/platforma) ma zabezpieczenie (szelki, lina, balustrady). " +
                          "Nie alarmuj gdy szelki widoczne choć częściowo zasłonięte, lub wysokość wyraźnie poniżej progu.",
            minConfidence: 0.7,
            severity: ResponseSeverity.High,
            requireGoodImage: true);

        yield return Make(
            key: "smoking",
            name: "Palenie w strefie zakazanej",
            category: "Środowisko",
            description: "Wykrywa osobę palącą papierosa/e-papierosa w strefie zakazanej (np. magazyn paliw, hala z materiałami łatwopalnymi).",
            scenarioEn: "Verify whether a person is smoking (cigarette, e-cigarette, visible smoke from hand/mouth area).",
            confirmHint: "Confirm if you can identify: (a) cigarette/vape in hand or mouth, (b) smoke emanating from mouth/nose, (c) lighter being used. Do NOT confirm: coffee cup, pen, hot drink steam, person just standing (no visible smoking behavior).",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy osoba pali (papieros, e-papieros, widoczny dym przy ustach/dłoniach). " +
                          "Nie myl z kubkiem kawy, długopisem, parą z gorącego napoju.",
            minConfidence: 0.75,
            severity: ResponseSeverity.High,
            requireGoodImage: true);

        yield return Make(
            key: "loitering",
            name: "Długotrwała obecność w miejscu przejściowym",
            category: "Strefa",
            description: "Weryfikuje czy osoba zatrzymała się w miejscu typu 'tylko przechodzić' (korytarz, droga ewakuacji).",
            scenarioEn: "Verify whether a person is loitering (stationary) in an area that is meant for transit only.",
            confirmHint: "This is a single-frame judgment — you can't see time elapsed. Instead, look for POSTURE cues: person clearly stopped (facing nothing, no task in hand), phone use, conversation without work context. A person walking = not loitering. A person bending to pick something = normal task.",
            userPromptPl: "Kontekst detekcji: {labels} na kamerze '{camera}', w strefie '{zone}' (trigger '{trigger}'). " +
                          "Oceń czy osoba stoi bezczynnie w miejscu przeznaczonym do przechodzenia (korytarz, droga ewakuacji). " +
                          "Wskazówki: brak widocznego zadania, telefon, rozmowa bez kontekstu pracy. Chód = brak problemu.",
            minConfidence: 0.7,
            severity: ResponseSeverity.Low, // single-frame hard to confirm, niska severity
            requireGoodImage: true);
    }

    private static PromptTemplate Make(
        string key, string name, string category, string description,
        string scenarioEn, string confirmHint,
        string userPromptPl,
        double minConfidence,
        ResponseSeverity severity,
        bool requireGoodImage)
    {
        var systemPrompt = $"""
            {CommonSystemRules}

            SCENARIO: {scenarioEn}

            DECISION GUIDELINES FOR THIS SCENARIO:
            {confirmHint}
            """;
        return new PromptTemplate
        {
            Name = name,
            Description = description,
            Category = category,
            SystemPrompt = systemPrompt,
            UserTemplate = userPromptPl,
            SchemaJson = CommonSchema,
            RecommendedMinConfidence = minConfidence,
            DefaultMinSeverity = severity,
            RequireGoodImageQuality = requireGoodImage,
            IsBuiltIn = true,
            BuiltInKey = key
        };
    }
}
