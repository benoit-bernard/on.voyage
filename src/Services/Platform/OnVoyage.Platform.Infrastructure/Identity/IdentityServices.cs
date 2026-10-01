using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Domain;
using OnVoyage.ServiceDefaults.Security;

namespace OnVoyage.Platform.Infrastructure.Identity;

internal sealed class JwtTokenIssuer(IConfiguration configuration) : ITokenIssuer
{
    private readonly SigningCredentials _credentials = new(AuthenticationExtensions.SigningKey(configuration["Auth:JwtSecret"]
        ?? throw new InvalidOperationException("Auth:JwtSecret is missing.")), SecurityAlgorithms.HmacSha256);

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Account account, TimeSpan lifetime, DateTimeOffset now)
    {
        var expires = now + lifetime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = OnVoyageClaims.Issuer,
            Audience = OnVoyageClaims.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = account.Id.ToString("D"),
                ["jti"] = Guid.NewGuid().ToString("N"),
                [OnVoyageClaims.IsAnonymous] = account.IsAnonymous,
                [OnVoyageClaims.EmailVerified] = !account.IsAnonymous,
                [OnVoyageClaims.Roles] = account.Roles.ToArray(),
            },
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

internal sealed class CredentialService(IConfiguration configuration) : ICredentialService
{
    private readonly byte[] _pepper = SHA256.HashData(Encoding.UTF8.GetBytes("onvoyage-otp-pepper:" + (configuration["Auth:JwtSecret"] ?? string.Empty)));

    public string NewOtpCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public string NewRefreshToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public string HashOtp(Guid challengeId, string email, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes($"{challengeId:N}|{email}|{code}")));

    public string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

/// <summary>Auth settings: annexe E keys (<c>auth</c>, <c>security</c>) from remote config, with the spec defaults as fallback.</summary>
internal sealed class AuthSettingsProvider(IRemoteConfigStore config, IConfiguration configuration) : IAuthSettingsProvider
{
    public async Task<AuthSettings> GetAsync(CancellationToken cancellationToken)
    {
        var auth = await ReadAsync("auth", cancellationToken);
        var security = await ReadAsync("security", cancellationToken);

        return new AuthSettings(
            TimeSpan.FromMinutes(Number(auth, "otp_ttl_minutes", 10)),
            Number(auth, "otp_max_attempts", 5),
            TimeSpan.FromSeconds(Number(auth, "otp_resend_seconds", 60)),
            Number(security, "otp_per_email_per_hour", 5),
            TimeSpan.FromMinutes(Number(auth, "access_token_minutes", 60)),
            TimeSpan.FromDays(configuration.GetValue("Auth:RefreshTokenDays", 90)),
            [.. (configuration.GetSection("Auth:BootstrapAdminEmails").Get<string[]>() ?? []).Select(email => email.Trim().ToLowerInvariant())]);
    }

    private async Task<JsonElement?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var entry = await config.FindAsync(key, cancellationToken);
        return entry is null ? null : JsonDocument.Parse(entry.ValueJson).RootElement.Clone();
    }

    private static int Number(JsonElement? section, string name, int fallback) =>
        section is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) && number > 0 ? number : fallback;
}

/// <summary>Sends sign-in codes through the Resend HTTP API. The recipient address is the only personal data sent (see docs/PRIVACY.md).</summary>
internal sealed class ResendEmailSender(HttpClient http, IConfiguration configuration, ILogger<ResendEmailSender> logger) : IEmailSender
{
    public async Task SendOtpAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var apiKey = configuration["Email:Resend:ApiKey"];
        var from = configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(from))
        {
            throw new EmailDeliveryException("Resend is not configured (Email:Resend:ApiKey, Email:From).");
        }

        var minutes = (int)Math.Round(lifetime.TotalMinutes);
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new
            {
                from,
                to = new[] { email },
                subject = $"Votre code ON.VOYAGE : {code}",
                text = $"Votre code de connexion ON.VOYAGE est {code}.\nIl est valable {minutes} minutes. Si vous n'êtes pas à l'origine de cette demande, ignorez ce message.",
                html = $"<p>Votre code de connexion ON.VOYAGE est&nbsp;:</p><p style=\"font-size:28px;letter-spacing:6px\"><strong>{code}</strong></p><p>Il est valable {minutes}&nbsp;minutes. Si vous n'êtes pas à l'origine de cette demande, ignorez ce message.</p>",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Status only: the body can echo the recipient.
                logger.LogWarning("Resend rejected the sign-in e-mail with status {Status}.", (int)response.StatusCode);
                throw new EmailDeliveryException($"Resend answered {(int)response.StatusCode}.");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new EmailDeliveryException("Resend is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailDeliveryException("Resend timed out.", ex);
        }
    }
}

/// <summary>Development only: prints the code to the log instead of sending it. Refused outside Development at startup.</summary>
internal sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendOtpAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        logger.LogWarning("DEVELOPMENT sign-in code for {Email}: {Code} (valid {Minutes} min)", email, code, (int)lifetime.TotalMinutes);
        return Task.CompletedTask;
    }
}
