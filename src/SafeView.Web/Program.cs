using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using SafeView.Cameras;
using SafeView.Infrastructure;
using SafeView.Licensing;
using SafeView.LLM;
using SafeView.ML;
using SafeView.Notifications;
using SafeView.Reports;
using SafeView.Roboflow;
using SafeView.Security;
using SafeView.Security.Auth;
using SafeView.Web.Auth;
using SafeView.Web.Components;
using SafeView.Web.Endpoints;
using Serilog;

// ────────────────────────────────────────────────────────────────────────────────
// Bootstrap logger (captures startup errors before full configuration is built)
// ────────────────────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("SafeView starting...");

    var builder = WebApplication.CreateBuilder(args);

    // Runtime overrides — plik edytowalny z UI (Ustawienia). Musi być PO pozostałych
    // JSON źródłach żeby wartości tu zapisane miały pierwszeństwo; reloadOnChange
    // sprawia że IOptionsMonitor widzi zmiany bez restartu.
    builder.Configuration.AddJsonFile("appsettings.Runtime.json", optional: true, reloadOnChange: true);

    // Serve static web assets from Razor Class Libraries (MudBlazor CSS/JS, etc.)
    // also in Production — by default UseStaticWebAssets is dev-only.
    builder.WebHost.UseStaticWebAssets();

    // Kestrel body limit — CLIP text-encoder ONNX w YOLO-World ma ~150MB. User może go
    // uploadować manualnie gdy auto-download z HuggingFace failuje (401 / gated repos).
    // 500MB z zapasem na cały pack YOLO-World + marża.
    builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 500L * 1024 * 1024);
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    {
        o.MultipartBodyLengthLimit = 500L * 1024 * 1024;
        o.ValueLengthLimit = int.MaxValue;
    });

    // Serilog from configuration — console + plik + MongoDB sink (system_events).
    // Sink MongoDB dostaje IServiceProvider (nie sam ISystemEventLogger!) — resolve jest
    // lazy, dopiero przy pierwszym zdarzeniu. Inaczej circular z LoggerFactory podczas
    // budowy hosta i aplikacja wisi na starcie.
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/safeview-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
        .WriteTo.Sink(new SafeView.Infrastructure.Diagnostics.SystemEventSink(
            services,
            services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SafeView.Application.Configuration.DiagnosticsOptions>>())));

    // ─── Infrastructure (MongoDB + FileStore) ──────────────────────────────────
    builder.Services.AddSafeViewInfrastructure(builder.Configuration);

    // ─── Licensing (AES-GCM + HMAC, loaded from disk, revalidated hourly) ──────
    builder.Services.AddSafeViewLicensing(builder.Configuration);

    // ─── Security (Argon2id + seeder + AuthService) ────────────────────────────
    builder.Services.AddSafeViewSecurity(builder.Configuration);

    // ─── Cameras (FFmpeg snapshot + MediaMTX) ──────────────────────────────────
    builder.Services.AddSafeViewCameras(builder.Configuration);

    // ─── ML (ONNX detector + frame observer + rule engine MVP) ────────────────
    builder.Services.AddSafeViewML(builder.Configuration);
    builder.Services.AddSafeViewRoboflow();

    // ─── LLM (single source of truth: LlmProvider w Mongo, /admin/llm-providers) ─
    builder.Services.AddSafeViewLLM();

    // ─── Reports (QuestPDF + CSV) ─────────────────────────────────────────────
    builder.Services.AddSafeViewReports();

    // ─── Notifications (SMTP + Webhooks → SIEM/Slack) ─────────────────────────
    builder.Services.AddSafeViewNotifications(builder.Configuration);

    // ─── Auth: cookie + policies per permission ────────────────────────────────
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "safeview.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/forbidden";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
        })
        .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(ApiKeyAuth.SchemeName, _ => { });
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
    builder.Services.AddAuthorizationBuilder()
        // Global fallback: każdy endpoint/page bez jawnego [AllowAnonymous] lub
        // [Authorize(Policy="perm:*")] wymaga uwierzytelnionego użytkownika.
        // Chroni przed omyłkowym dodaniem niechronionej trasy w przyszłości.
        .SetFallbackPolicy(new AuthorizationPolicyBuilder(
                CookieAuthenticationDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build())
        .AddSafeViewPolicies();
    builder.Services.AddCascadingAuthenticationState();
    builder.Services.AddScoped<SafeView.Web.Theme.ThemeState>();
    builder.Services.AddSingleton<SafeView.Web.Services.RuntimeConfigService>();
    builder.Services.AddSingleton<SafeView.Web.Services.IngestIdempotencyStore>();
    builder.Services.AddHttpClient(); // for ingest-json image_url fetching

    // ─── Rate limiting (MOD.API) ───────────────────────────────────────────────
    // Per-API-key sliding window: 120 req / min. Login: 10 req / min / IP.
    builder.Services.AddRateLimiter(opt =>
    {
        opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        opt.AddPolicy("api-key", ctx =>
        {
            var partitionKey = ctx.User.FindFirst("safeview.api_key_id")?.Value
                               ?? ctx.Connection.RemoteIpAddress?.ToString()
                               ?? "anon";
            return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
        });

        opt.AddPolicy("login", ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        });

        // Push-based ingest dla CameraTransport.Api — wyższy limit niż api-key (typowe 5-30 fps),
        // partition per kamera (URL route value) żeby ruch z jednej kamery nie zabijał innym.
        opt.AddPolicy("camera-ingest", ctx =>
        {
            var cameraId = ctx.Request.RouteValues.TryGetValue("cameraId", out var v)
                ? v?.ToString() ?? "unknown"
                : "unknown";
            return RateLimitPartition.GetSlidingWindowLimiter(cameraId, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 1800,                 // 30 fps × 60s
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
        });
    });

    // ─── Localization (PL/EN, rozszerzalne) ────────────────────────────────────
    builder.Services.AddLocalization();

    var supportedCultures = new[] { new CultureInfo("pl"), new CultureInfo("en") };
    builder.Services.Configure<RequestLocalizationOptions>(options =>
    {
        options.DefaultRequestCulture = new RequestCulture("pl");
        options.SupportedCultures = supportedCultures;
        options.SupportedUICultures = supportedCultures;
        options.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider
        {
            CookieName = CookieRequestCultureProvider.DefaultCookieName
        });
    });

    // ─── MudBlazor ─────────────────────────────────────────────────────────────
    builder.Services.AddMudServices(config =>
    {
        config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    });

    // ─── Blazor ────────────────────────────────────────────────────────────────
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddSignalR();

    // ─── Flow live events (SignalR) ───────────────────────────────────────────
    builder.Services.AddSingleton<SafeView.Application.Abstractions.Detection.IFlowEventPublisher,
                                   SafeView.Web.Hubs.SignalRFlowEventPublisher>();

    var app = builder.Build();

    // ─── Middleware ────────────────────────────────────────────────────────────
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    // ─── Security headers (defense-in-depth) ──────────────────────────────────
    app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // CSP — Blazor Server wymaga 'unsafe-inline' dla inicjalnego skryptu i 'wasm-unsafe-eval' jest niepotrzebny.
        h["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
            "font-src 'self' https://fonts.gstatic.com data:; " +
            "img-src 'self' data: blob:; " +
            "media-src 'self' blob:; " +
            "connect-src 'self' ws: wss:; " +
            "frame-ancestors 'none';";
        await next();
    });

    app.UseRateLimiter();

    // Serve static files from wwwroot AND Razor Class Library _content/* (MudBlazor CSS/JS, etc.)
    app.UseStaticFiles();

    // Localization BEFORE routing / antiforgery
    app.UseRequestLocalization();

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAntiforgery();

    // ─── Endpoints ─────────────────────────────────────────────────────────────
    // Culture switch endpoint (used by LanguageSwitcher).
    // PUBLIC z rozmysłem — musi działać przed logowaniem (user może zmienić język
    // na stronie /login). Zabezpieczenia:
    //   • Allowlist kultur — nie przyjmujemy arbitrary string (inaczej cookie injection)
    //   • Rate limit "login" (10 req/min per IP) — nie pozwala na brute/spam
    //   • LocalRedirect zamiast Redirect — ochrona przed open redirect
    //   • [AllowAnonymous] — explicite żeby future global fallback policy nie blokowała
    string[] allowedCultures = ["pl", "en"];
    app.MapGet("/culture/set", (HttpContext ctx, string culture, string redirect) =>
    {
        if (string.IsNullOrWhiteSpace(culture) || !allowedCultures.Contains(culture))
            return Results.BadRequest("Unsupported culture.");

        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                Path = "/",
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax
            });
        return Results.LocalRedirect(string.IsNullOrWhiteSpace(redirect) ? "/" : redirect);
    })
    .AllowAnonymous()
    .RequireRateLimiting("login");

    app.MapAuthEndpoints();
    app.MapReportEndpoints();
    app.MapApiV1Endpoints();
    app.MapIngestEndpoints();
    app.MapHealthEndpoints();
    app.MapCameraSnapshotEndpoint();
    app.MapPerformanceEndpoints();
    app.MapMonitorEndpoints();
    app.MapIncidentFrameEndpoint();
    app.MapPipelineFrameEndpoint();
    app.MapVllmStatsEndpoint();
    app.MapVllmTemplateIoEndpoints();
    app.MapDetectionClassRefEndpoints();
    app.MapDownloadYoloWorldEndpoint();
    app.MapModelTestDetectEndpoint();
    app.MapFlowEndpoints();
    app.MapHub<SafeView.Web.Hubs.FlowHub>("/hubs/flow");

    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    Log.Information("SafeView ready.");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SafeView failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
