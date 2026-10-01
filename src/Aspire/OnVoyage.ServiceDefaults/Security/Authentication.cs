using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace OnVoyage.ServiceDefaults.Security;

/// <summary>Names of the authorization policies of SEC-03.</summary>
public static class Policies
{
    /// <summary>Any authenticated session, anonymous ones included.</summary>
    public const string Traveler = "traveler";

    /// <summary>A session whose e-mail has been verified.</summary>
    public const string Account = "account";

    public const string Admin = "admin";
    public const string Creator = "creator";

    /// <summary>Service-to-service token (Web.Public, Gateway).</summary>
    public const string Internal = "internal";

    /// <summary>Public read endpoints: a traveler session or an internal host.</summary>
    public const string TravelerOrInternal = "traveler_or_internal";
}

public static class OnVoyageClaims
{
    public const string Issuer = "on.voyage";
    public const string Audience = "on.voyage";
    public const string Roles = "roles";
    public const string EmailVerified = "email_verified";
    public const string IsAnonymous = "is_anonymous";

    public static Guid? TravelerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var id) ? id : null;

    public static bool HasRole(this ClaimsPrincipal user, string role) =>
        user.FindAll(Roles).Any(claim => string.Equals(claim.Value, role, StringComparison.Ordinal));
}

/// <summary>
/// Access-token validation shared by the Gateway and every service (SEC-02: defence in depth). Tokens are HS256 JWTs issued by Platform;
/// the shared secret comes from <c>Auth:JwtSecret</c> (an Aspire secret parameter), never from source control.
/// </summary>
public static class AuthenticationExtensions
{
    public const int MinimumSecretBytes = 32;

    public static TokenValidationParameters ValidationParameters(string secret) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = OnVoyageClaims.Issuer,
        ValidateAudience = true,
        ValidAudience = OnVoyageClaims.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(secret),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = "sub",
        RoleClaimType = OnVoyageClaims.Roles,
    };

    public static SymmetricSecurityKey SigningKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    public static IServiceCollection AddOnVoyageAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var secret = configuration["Auth:JwtSecret"];
        if (string.IsNullOrEmpty(secret) || Encoding.UTF8.GetByteCount(secret) < MinimumSecretBytes)
        {
            throw new InvalidOperationException($"Auth:JwtSecret must be set and at least {MinimumSecretBytes} bytes long.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TimeProvider>((options, clock) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = ValidationParameters(secret);
                // Token lifetimes are judged on the same clock that issued them (and that tests can control).
                options.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, parameters) =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    return expires is not null && notBefore is not null
                        && now >= notBefore.Value - parameters.ClockSkew
                        && now <= expires.Value + parameters.ClockSkew;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Traveler, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Policies.Account, policy => policy.RequireAuthenticatedUser().RequireClaim(OnVoyageClaims.EmailVerified, "true"))
            .AddPolicy(Policies.Admin, policy => policy.RequireAuthenticatedUser().RequireClaim(OnVoyageClaims.Roles, "admin"))
            .AddPolicy(Policies.Creator, policy => policy.RequireAuthenticatedUser().RequireClaim(OnVoyageClaims.Roles, "creator"))
            .AddPolicy(Policies.Internal, policy => policy.RequireAuthenticatedUser().RequireClaim(OnVoyageClaims.Roles, "internal"))
            .AddPolicy(Policies.TravelerOrInternal, policy => policy.RequireAuthenticatedUser());

        return services;
    }

    public static WebApplication UseOnVoyageAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

}
