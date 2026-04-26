namespace SafeView.Licensing.Modules;

/// <summary>
/// Kody modułów licencyjnych. Każdy kod to osobny flag w pliku .lic.
/// Odpowiada tabeli z sekcji 3 PLAN_PRAC.md.
/// </summary>
public static class LicenseModules
{
    public const string Core            = "CORE";

    public const string Ppe             = "MOD.PPE";
    public const string Zones           = "MOD.ZONES";
    public const string Falls           = "MOD.FALLS";
    public const string Collision       = "MOD.COLLISION";
    public const string Fire            = "MOD.FIRE";
    public const string Thermal         = "MOD.THERMAL";
    public const string Atex            = "MOD.ATEX";

    public const string Analytics       = "MOD.ANALYTICS";
    public const string Reports         = "MOD.REPORTS";
    public const string Llm             = "MOD.LLM";
    public const string Api             = "MOD.API";
    public const string Siem            = "MOD.SIEM";
    public const string Roboflow        = "MOD.ROBOFLOW";
    public const string CustomModels    = "MOD.CUSTOM_MODELS";

    public static readonly IReadOnlyList<string> All =
    [
        Core, Ppe, Zones, Falls, Collision, Fire, Thermal, Atex,
        Analytics, Reports, Llm, Api, Siem, Roboflow, CustomModels
    ];
}
