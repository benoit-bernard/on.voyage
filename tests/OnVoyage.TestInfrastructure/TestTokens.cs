using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OnVoyage.ServiceDefaults.Security;

namespace OnVoyage.TestInfrastructure;

/// <summary>Mints access tokens exactly like Platform does, for tests of services that only validate them.</summary>
public static class TestTokens
{
    public const string Secret = "integration-tests-secret-0123456789abcdef-0123456789";

    public static string Mint(Guid? travelerId = null, bool anonymous = true, string[]? roles = null, TimeSpan? lifetime = null, string? secret = null, DateTimeOffset? now = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = OnVoyageClaims.Issuer,
            Audience = OnVoyageClaims.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = (issuedAt + (lifetime ?? TimeSpan.FromHours(1))).UtcDateTime,
            SigningCredentials = new SigningCredentials(AuthenticationExtensions.SigningKey(secret ?? Secret), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = (travelerId ?? Guid.NewGuid()).ToString("D"),
                [OnVoyageClaims.IsAnonymous] = anonymous,
                [OnVoyageClaims.EmailVerified] = !anonymous,
                [OnVoyageClaims.Roles] = roles ?? [],
            },
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static void Authenticate(this HttpClient client, string? token = null) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Mint());
}
