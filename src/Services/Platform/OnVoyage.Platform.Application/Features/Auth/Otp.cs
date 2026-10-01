using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Features.Auth;

public sealed record RequestOtpCommand(string Email);

public sealed record VerifyOtpCommand(Guid? CallerId, string Email, string Code);

/// <summary>Sends a 6-digit code by e-mail. The answer never reveals whether the address already has an account.</summary>
public static class RequestOtpHandler
{
    public static async Task<Result<bool>> Handle(
        RequestOtpCommand command, IOtpStore otps, ICredentialService credentials, IEmailSender email,
        IAuthSettingsProvider settingsProvider, TimeProvider clock, CancellationToken cancellationToken)
    {
        var address = EmailAddress.Normalize(command.Email);
        if (address is null)
        {
            return Result.Failure<bool>("validation", "Enter a valid e-mail address.");
        }

        var settings = await settingsProvider.GetAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var latest = await otps.LatestAsync(address, cancellationToken);
        if (latest is not null && now - latest.CreatedAt < settings.OtpResendCooldown)
        {
            var wait = (int)Math.Ceiling((settings.OtpResendCooldown - (now - latest.CreatedAt)).TotalSeconds);
            return Result.Throttled<bool>("otp_cooldown", "A code was just sent. Wait before asking for another one.", wait);
        }

        if (await otps.CountSinceAsync(address, now.AddHours(-1), cancellationToken) >= settings.OtpPerEmailPerHour)
        {
            return Result.Throttled<bool>("otp_rate_limited", "Too many codes were requested for this address. Try again later.", 3600);
        }

        var code = credentials.NewOtpCode();
        var id = Guid.CreateVersion7();
        var challenge = new OtpChallenge(id, address, credentials.HashOtp(id, address, code), now, now + settings.OtpLifetime, 0, null);
        await otps.AddAsync(challenge, now, cancellationToken);

        try
        {
            await email.SendOtpAsync(address, code, settings.OtpLifetime, cancellationToken);
        }
        catch (EmailDeliveryException)
        {
            // A code nobody received must not count against the traveler.
            await otps.SaveAsync(challenge with { ConsumedAt = now }, cancellationToken);
            return Result.Failure<bool>("email_unavailable", "The code could not be sent. Try again in a moment.");
        }

        return Result.Success(true);
    }
}

/// <summary>
/// Checks the code, then links the e-mail to the caller's anonymous account (same traveler id) or, if the address already has an
/// account, signs the caller in to it (restoring after a reinstall).
/// </summary>
public static class VerifyOtpHandler
{
    public static async Task<Result<AuthSessionDto>> Handle(
        VerifyOtpCommand command, IOtpStore otps, IAccountStore accounts, IRefreshTokenStore refreshTokens, ITokenIssuer tokens,
        ICredentialService credentials, IAuthSettingsProvider settingsProvider, TimeProvider clock, CancellationToken cancellationToken)
    {
        var address = EmailAddress.Normalize(command.Email);
        if (address is null || string.IsNullOrEmpty(command.Code) || command.Code.Length != 6 || !command.Code.All(char.IsAsciiDigit))
        {
            return Result.Failure<AuthSessionDto>("validation", "Enter the 6-digit code.");
        }

        var settings = await settingsProvider.GetAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var challenge = await otps.FindActiveAsync(address, cancellationToken);
        if (challenge is null)
        {
            return Result.Failure<AuthSessionDto>("otp_invalid", "This code is not valid. Ask for a new one.");
        }

        if (challenge.ExpiresAt <= now)
        {
            await otps.SaveAsync(challenge with { ConsumedAt = now }, cancellationToken);
            return Result.Failure<AuthSessionDto>("otp_expired", "This code has expired. Ask for a new one.");
        }

        if (!credentials.FixedTimeEquals(challenge.CodeHash, credentials.HashOtp(challenge.Id, address, command.Code)))
        {
            var attempts = challenge.Attempts + 1;
            var locked = attempts >= settings.OtpMaxAttempts;
            await otps.SaveAsync(challenge with { Attempts = attempts, ConsumedAt = locked ? now : null }, cancellationToken);
            return locked
                ? Result.Failure<AuthSessionDto>("otp_locked", "Too many wrong codes. Ask for a new one.")
                : Result.Failure<AuthSessionDto>("otp_invalid", "This code is not valid.");
        }

        var caller = command.CallerId is { } callerId ? await accounts.FindAsync(callerId, cancellationToken) : null;
        if (caller is { ReplacedBy: not null })
        {
            caller = null;
        }

        var existing = await accounts.FindByEmailAsync(address, cancellationToken);
        Account account;
        if (existing is not null)
        {
            account = existing with { LastActiveAt = now };
            if (caller is not null && caller.Id != existing.Id && caller.IsAnonymous)
            {
                // The anonymous session of this device is abandoned in favour of the existing account.
                await accounts.SaveAsync(caller with { ReplacedBy = existing.Id }, cancellationToken);
                await refreshTokens.RevokeAllForAccountAsync(caller.Id, now, cancellationToken);
            }
            else if (caller is not null && caller.Id != existing.Id)
            {
                return Result.Failure<AuthSessionDto>("email_already_linked", "This session already belongs to another verified account.");
            }
        }
        else if (caller is not null)
        {
            if (!caller.IsAnonymous)
            {
                return Result.Failure<AuthSessionDto>("email_already_linked", "This account already has a verified e-mail; changing it is not supported yet.");
            }

            account = caller.LinkEmail(address, now);
        }
        else
        {
            account = Account.NewAnonymous(Guid.CreateVersion7(), now).LinkEmail(address, now);
        }

        if (settings.BootstrapAdminEmails.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            account = account.WithRole("admin");
        }

        await otps.SaveAsync(challenge with { ConsumedAt = now }, cancellationToken);
        await accounts.SaveAsync(account, cancellationToken);
        return Result.Success(await SessionIssuer.IssueAsync(account, null, settings, now, tokens, credentials, refreshTokens, cancellationToken));
    }
}
