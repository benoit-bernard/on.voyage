using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Features.Consents;

public sealed record GetConsentsQuery(Guid TravelerId);

public sealed record SetConsentCommand(Guid TravelerId, string Kind, bool Granted, string TextVersion);

public static class ConsentHandler
{
    /// <summary>Every kind is returned; an absent record means "not granted" (consent is opt-in, §16.3).</summary>
    public static async Task<Result<IReadOnlyList<ConsentDto>>> Handle(GetConsentsQuery query, IConsentStore store, CancellationToken cancellationToken)
    {
        var stored = (await store.ListAsync(query.TravelerId, cancellationToken)).ToDictionary(consent => consent.Kind);
        IReadOnlyList<ConsentDto> all =
        [
            .. ConsentKinds.All.Select(kind => stored.TryGetValue(kind, out var consent)
                ? new ConsentDto(kind, consent.Granted, consent.TextVersion, consent.UpdatedAt)
                : new ConsentDto(kind, false, string.Empty, DateTimeOffset.MinValue)),
        ];
        return Result.Success(all);
    }

    public static async Task<Result<ConsentDto>> Handle(SetConsentCommand command, IConsentStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!ConsentKinds.All.Contains(command.Kind))
        {
            return Result.Failure<ConsentDto>("validation", "Unknown consent kind.");
        }

        if (string.IsNullOrWhiteSpace(command.TextVersion) || command.TextVersion.Length > 32)
        {
            return Result.Failure<ConsentDto>("validation", "A consent text version (up to 32 characters) is required.");
        }

        var now = clock.GetUtcNow();
        var existing = (await store.ListAsync(command.TravelerId, cancellationToken)).FirstOrDefault(consent => consent.Kind == command.Kind);
        var consent = new Consent(command.TravelerId, command.Kind, command.Granted, command.TextVersion, now);
        if (existing is not null && existing.Granted == command.Granted && existing.TextVersion == command.TextVersion)
        {
            return Result.Success(new ConsentDto(existing.Kind, existing.Granted, existing.TextVersion, existing.UpdatedAt));
        }

        await store.SaveAsync(consent, new ConsentChangedV1(Guid.CreateVersion7(), now, consent.TravelerId, consent.Kind, consent.Granted, consent.TextVersion), cancellationToken);
        return Result.Success(new ConsentDto(consent.Kind, consent.Granted, consent.TextVersion, consent.UpdatedAt));
    }
}
