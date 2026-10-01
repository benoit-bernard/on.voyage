using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace OnVoyage.ServiceDefaults.Security;

/// <summary>Short-lived tokens that internal hosts (Gateway, Web.Public) present to services; they carry the <c>internal</c> role (SEC-03).</summary>
public static class InternalTokens
{
    public static string Mint(string secret, string subject, TimeProvider clock, TimeSpan? lifetime = null)
    {
        var now = clock.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = OnVoyageClaims.Issuer,
            Audience = OnVoyageClaims.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + (lifetime ?? TimeSpan.FromMinutes(5))).UtcDateTime,
            SigningCredentials = new SigningCredentials(AuthenticationExtensions.SigningKey(secret), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                [OnVoyageClaims.IsAnonymous] = false,
                [OnVoyageClaims.EmailVerified] = false,
                [OnVoyageClaims.Roles] = new[] { "internal" },
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
