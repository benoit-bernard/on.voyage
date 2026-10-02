using Microsoft.Extensions.Logging;
using OnVoyage.Creators.Contracts;
using OnVoyage.Platform.Application.Ports;

namespace OnVoyage.Platform.Application.IntegrationEvents;

/// <summary>
/// A creator accepted the creator terms, or a founder's written consent was recorded (F-26): the account gets the <c>creator</c> role
/// (<c>roles</c> claim of the next tokens). Anonymous accounts never get it: a creator signs in with a verified e-mail. The role is added
/// once, so a redelivery or a second event for the same creator changes nothing.
/// </summary>
public sealed class CreatorTermsAcceptedHandler(ILogger<CreatorTermsAcceptedHandler> logger)
{
    public const string CreatorRole = "creator";

    public async Task Handle(CreatorTermsAcceptedV1 accepted, IAccountStore accounts, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(accepted.AccountId, cancellationToken);
        if (account is null)
        {
            logger.LogWarning("Creator {CreatorId}: account {AccountId} is unknown, the creator role was not granted.", accepted.CreatorId, accepted.AccountId);
            return;
        }

        if (account.IsAnonymous)
        {
            logger.LogWarning("Creator {CreatorId}: account {AccountId} is anonymous, the creator role was not granted.", accepted.CreatorId, accepted.AccountId);
            return;
        }

        var updated = account.WithRole(CreatorRole);
        if (!ReferenceEquals(updated, account))
        {
            await accounts.SaveAsync(updated, cancellationToken);
            logger.LogInformation("Creator {CreatorId}: the creator role was granted to account {AccountId} (terms {TermsVersion}).", accepted.CreatorId, accepted.AccountId, accepted.TermsVersion);
        }
    }
}
