using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SafeClass.Application.Interfaces;
using SafeClass.Application.Security;
using SafeClass.Domain.Entities;
using SafeClass.Domain.Enums;

namespace SafeClass.Api.Services;

public static class Policies
{
    public const string Responder = "Responder";        // acknowledge / resolve alerts
    public const string AlertCreator = "AlertCreator";  // raise an alert from the app
    public const string Manager = "Manager";            // classroom changes, demo reset
    public const string AdminOnly = "AdminOnly";        // school settings, device registration
    public const string ReportViewer = "ReportViewer";
    public const string DeviceOnly = "DeviceOnly";
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "SafeClass";
    public string Audience { get; set; } = "SafeClass.Clients";
    public int ExpiryMinutes { get; set; } = 480;

    /// <summary>HMAC signing key (32+ random characters). Supply via environment variable Jwt__Key or a secret store.</summary>
    public string Key { get; set; } = "";
}

/// <summary>Reads the caller from the validated JWT (staff) or the device-key principal (ESP32).</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public bool IsDevice => Principal?.FindFirstValue("role") == "Device";

    public Guid? UserId => IsAuthenticated && !IsDevice ? ParseGuid(Principal!.FindFirstValue("sub")) : null;
    public Guid? DeviceId => IsAuthenticated && IsDevice ? ParseGuid(Principal!.FindFirstValue("sub")) : null;
    public Guid? SchoolId => IsAuthenticated ? ParseGuid(Principal!.FindFirstValue("school_id")) : null;

    public UserRole? Role =>
        IsAuthenticated && !IsDevice && Enum.TryParse<UserRole>(Principal!.FindFirstValue("role"), out var role) ? role : null;

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time) : ITokenService
{
    public (string Token, DateTime ExpiresAtUtc) CreateToken(User user)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(o.ExpiryMinutes);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key)), SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("school_id", user.SchoolId.ToString()),
                new Claim("role", user.Role.ToString()),
                new Claim("name", user.FullName),
                new Claim("jti", Guid.NewGuid().ToString())
            ]),
            Issuer = o.Issuer, Audience = o.Audience,
            NotBefore = now, IssuedAt = now, Expires = expires,
            SigningCredentials = credentials
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

/// <summary>
/// Authenticates an ESP32 with two headers: X-Device-Identifier and X-Device-Key. Only a SHA-256 hash of the key
/// is stored, comparison is constant-time, and revoked or inactive devices are rejected.
/// </summary>
public sealed class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISafeClassDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "DeviceKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Device-Identifier", out var idValues) ||
            !Request.Headers.TryGetValue("X-Device-Key", out var keyValues))
            return AuthenticateResult.NoResult();

        var identifier = idValues.ToString().Trim();
        var key = keyValues.ToString();
        if (identifier.Length is 0 or > 64 || key.Length is 0 or > 200)
            return AuthenticateResult.Fail("Invalid device credentials.");

        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceIdentifier == identifier, Context.RequestAborted);
        var keyOk = DeviceCredentials.Verify(key, device?.ApiKeyHash);
        if (device is null || !keyOk || !device.IsActive || device.CredentialRevokedAtUtc.HasValue)
        {
            Logger.LogWarning("Rejected device authentication for identifier {Identifier}", identifier);
            return AuthenticateResult.Fail("Invalid device credentials.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", device.Id.ToString()),
                new Claim("school_id", device.SchoolId.ToString()),
                new Claim("role", "Device"),
                new Claim("device_identifier", device.DeviceIdentifier)
            ],
            Scheme, nameType: "device_identifier", roleType: "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}
