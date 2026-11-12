using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using SafeClass.Api.Hubs;
using SafeClass.Api.Middleware;
using SafeClass.Api.Services;
using SafeClass.Application;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Options;
using SafeClass.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

// ---------- Options ----------
builder.Services.Configure<SafeClassOptions>(config.GetSection(SafeClassOptions.Section));
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set to a random value of at least 32 characters (environment variable Jwt__Key or a secret store).");

// ---------- Data + application ----------
var connectionString = InfrastructureServiceCollectionExtensions.BuildConnectionString(
    config["Database:ConnectionString"], config["Database:Path"]);
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddApplication();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();
builder.Services.AddScoped<IMonitoringNotifier, SignalRMonitoringNotifier>();

// ---------- Auth ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name", RoleClaimType = "role"
        };
        // Browsers cannot set headers on WebSocket/SSE connections, so SignalR sends the token in the query string.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(DeviceKeyAuthenticationHandler.Scheme, _ => { });

builder.Services.AddAuthorization(o =>
{
    // Everything requires a signed-in staff user unless a controller says otherwise.
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser().RequireAssertion(c => c.User.FindFirst("role")?.Value != "Device").Build();
    o.DefaultPolicy = o.FallbackPolicy;
    o.AddPolicy(Policies.Responder, p => p.RequireClaim("role", "Administrator", "SafetyCoordinator", "SecurityStaff"));
    o.AddPolicy(Policies.AlertCreator, p => p.RequireClaim("role", "Administrator", "SafetyCoordinator", "SecurityStaff", "Teacher"));
    o.AddPolicy(Policies.Manager, p => p.RequireClaim("role", "Administrator", "SafetyCoordinator"));
    o.AddPolicy(Policies.AdminOnly, p => p.RequireClaim("role", "Administrator"));
    o.AddPolicy(Policies.ReportViewer, p => p.RequireClaim("role", "Administrator", "SafetyCoordinator", "SecurityStaff", "Viewer"));
    o.AddPolicy(Policies.DeviceOnly, p => p.AddAuthenticationSchemes(DeviceKeyAuthenticationHandler.Scheme)
        .RequireAuthenticatedUser().RequireClaim("role", "Device"));
});

// ---------- Rate limiting ----------
var loginLimit = config.GetValue("RateLimiting:LoginPerMinute", 10);
var deviceLimit = config.GetValue("RateLimiting:DevicePerMinute", 120);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("device", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["X-Device-Identifier"].ToString() is { Length: > 0 } id ? id : (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = deviceLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---------- Web ----------
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model-binding failures use the same problem+json shape as every other error.
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var details = new ValidationProblemDetails(ctx.ModelState) { Status = 400, Title = "Validation failed" };
            details.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
            return new BadRequestObjectResult(details) { ContentTypes = { "application/problem+json" } };
        };
    });
builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddOpenApi();

var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust only proxies you configure; by default none are trusted so spoofed headers are ignored.
});

builder.Services.AddHostedService<OfflineDeviceMonitor>();
builder.Services.AddHostedService<HeartbeatRetentionService>();
var demoEnabled = config.GetValue<bool>("SafeClass:DemoModeEnabled");
if (demoEnabled) builder.Services.AddHostedService<DemoHeartbeatSimulator>();

var app = builder.Build();

// ---------- Database init, seed, bootstrap ----------
await DatabaseInitializer.InitializeAsync(app.Services, app.Logger);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SafeClassDbContext>();
    var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();

    if (config.GetValue<bool>("Seed:Enabled"))
    {
        if (!env.IsDevelopment() && !demoEnabled)
            throw new InvalidOperationException("Seed:Enabled is only allowed in Development or when SafeClass:DemoModeEnabled is true.");
        var seed = new DemoSeedOptions { UserPassword = config["Seed:DemoPassword"] ?? "", DeviceKey = config["Seed:DemoDeviceKey"] ?? "" };
        if (seed.UserPassword.Length < 8 || seed.DeviceKey.Length < 16)
            throw new InvalidOperationException("Seed:DemoPassword (8+) and Seed:DemoDeviceKey (16+) are required when Seed:Enabled is true.");
        if (await DemoSeeder.SeedAsync(db, seed, time)) app.Logger.LogWarning("DEMO DATA seeded (school DEMO-001). Do not use in production.");
    }

    var boot = config.GetSection("Bootstrap").Get<BootstrapOptions>() ?? new BootstrapOptions();
    if (await Bootstrapper.BootstrapAsync(db, boot, time)) app.Logger.LogInformation("Initial school and administrator created.");
}

// ---------- Pipeline ----------
app.UseForwardedHeaders();
if (!env.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

// Static frontend (served from the repo's frontend folder) and API docs.
var frontendPath = Path.GetFullPath(config["Frontend:Path"] ?? "../../frontend", app.Environment.ContentRootPath);
if (Directory.Exists(frontendPath))
{
    var files = new PhysicalFileProvider(frontendPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapControllers();
app.MapHub<MonitoringHub>("/hubs/monitoring");
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// OpenAPI document + Swagger UI (not exposed in production unless Docs:Enabled=true).
if (env.IsDevelopment() || config.GetValue<bool>("Docs:Enabled"))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapGet("/swagger", () => Results.Content(SwaggerPage.Html, "text/html")).AllowAnonymous();
}

app.Run();

public partial class Program { }

internal static class SwaggerPage
{
    public const string Html = """
<!doctype html><html><head><meta charset="utf-8"><title>SafeClass API</title>
<link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.17.14/swagger-ui.css"></head>
<body><div id="ui"></div>
<script src="https://unpkg.com/swagger-ui-dist@5.17.14/swagger-ui-bundle.js"></script>
<script>
SwaggerUIBundle({
  url: '/openapi/v1.json', dom_id: '#ui',
  // Paste a JWT from POST /api/v1/auth/login into the box below to call protected endpoints.
  requestInterceptor: function (req) {
    var t = window.localStorage.getItem('swagger-token');
    if (t && !req.headers['Authorization']) req.headers['Authorization'] = 'Bearer ' + t;
    return req;
  }
});
var bar = document.createElement('div');
bar.style.cssText = 'padding:8px 16px;background:#eef;font:14px sans-serif';
bar.innerHTML = 'Bearer token: <input id="tk" size="80"> <button id="sv">Use</button>';
document.body.prepend(bar);
document.getElementById('sv').onclick = function () { window.localStorage.setItem('swagger-token', document.getElementById('tk').value.trim()); };
</script></body></html>
""";
}
