namespace SafeView.Domain.Users;

/// <summary>
/// Katalog uprawnień (permissions) — format "resource:action".
/// Role mapują się na zestawy uprawnień; polityka autoryzacji sprawdza pojedyncze permissions.
/// </summary>
public static class Permission
{
    // System admin
    public const string AdminUsers = "admin:users";
    public const string AdminRoles = "admin:roles";
    public const string AdminSettings = "admin:settings";
    public const string AdminLicense = "admin:license";
    public const string AdminAuditLog = "admin:audit";
    public const string AdminSystemEvents = "admin:system-events";

    // Cameras
    public const string CamerasView = "cameras:view";
    public const string CamerasEdit = "cameras:edit";
    public const string CamerasDelete = "cameras:delete";

    // Zones
    public const string ZonesView = "zones:view";
    public const string ZonesEdit = "zones:edit";

    // Models
    public const string ModelsView = "models:view";
    public const string ModelsEdit = "models:edit";
    public const string ModelsUpload = "models:upload";

    // Incidents
    public const string IncidentsView = "incidents:view";
    public const string IncidentsResolve = "incidents:resolve";

    // Reports
    public const string ReportsView = "reports:view";
    public const string ReportsGenerate = "reports:generate";

    // LLM
    public const string LlmChat = "llm:chat";
    public const string LlmConfigure = "llm:configure";

    // API keys / Notifications (admin)
    public const string AdminApiKeys = "admin:api_keys";
    public const string AdminNotifications = "admin:notifications";

    // Detection pipeline — Faza 1 (nowa architektura: ROI → Zone → Trigger → Action)
    public const string AdminRois = "admin:rois";
    public const string AdminTriggers = "admin:triggers";
    public const string AdminActions = "admin:actions";
    public const string AdminDetectionClasses = "admin:detection-classes";

    // Scopes dla MOD.API (przyznawane kluczom API; sprawdzane jak permissions)
    public const string ApiIncidentsRead = "api:incidents:read";
    public const string ApiCamerasRead = "api:cameras:read";
    public const string ApiZonesRead = "api:zones:read";

    /// <summary>Wszystkie permissions zdefiniowane w systemie — do zasilenia UI wyboru przy edycji roli.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        AdminUsers, AdminRoles, AdminSettings, AdminLicense, AdminAuditLog,
        AdminSystemEvents,
        AdminApiKeys, AdminNotifications,
        AdminRois, AdminTriggers, AdminActions, AdminDetectionClasses,
        CamerasView, CamerasEdit, CamerasDelete,
        ZonesView, ZonesEdit,
        ModelsView, ModelsEdit, ModelsUpload,
        IncidentsView, IncidentsResolve,
        ReportsView, ReportsGenerate,
        LlmChat, LlmConfigure,
        ApiIncidentsRead, ApiCamerasRead, ApiZonesRead
    ];
}
