using System.Text.Json;
using FluentValidation;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Contracts;

namespace OnVoyage.Insights.Application.Features.Events;

public sealed record IngestEventsCommand(Guid TravelerId, IReadOnlyList<EventDto> Events);

/// <summary>
/// Checks a batch against the event catalogue (§17.3): known event, known properties only (so no coordinates, no free text), right type and
/// range. A batch with one bad event is refused as a whole; the app only sends what it built from the same catalogue.
/// </summary>
public sealed class IngestEventsValidator : AbstractValidator<IngestEventsCommand>
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    public IngestEventsValidator(TimeProvider clock)
    {
        RuleFor(c => c.Events).NotNull().Must(events => events.Count > 0).WithMessage("a batch holds at least one event");
        RuleForEach(c => c.Events).ChildRules(item =>
        {
            item.RuleFor(e => e.Id).NotEqual(Guid.Empty).WithMessage("id is required");
            item.RuleFor(e => e.SessionId).NotEqual(Guid.Empty).WithMessage("sessionId is required");
            item.RuleFor(e => e.Name).Must(name => name is not null && EventCatalogue.TryGet(name, out _)).WithMessage("unknown event name");
            item.RuleFor(e => e.OccurredAt).LessThanOrEqualTo(_ => clock.GetUtcNow() + ClockSkew).WithMessage("occurredAt is in the future");
            item.RuleFor(e => e.AppVersion).NotEmpty().WithMessage("appVersion is required").MaximumLength(32).WithMessage("appVersion is 32 characters at most");
            item.RuleFor(e => e.Platform).Must(platform => platform is not null && Platforms.All.Contains(platform)).WithMessage("unknown platform");
            item.RuleFor(e => e).Custom((e, context) =>
            {
                if (PropertyError(e) is { } error)
                {
                    context.AddFailure(error);
                }
            });
        });
    }

    /// <summary>The first problem with the properties of an event, or null. Events with an unknown name are reported by the name rule.</summary>
    internal static string? PropertyError(EventDto e)
    {
        if (e.Name is null || !EventCatalogue.TryGet(e.Name, out var definition) || e.Props is null)
        {
            return null;
        }

        foreach (var (name, value) in e.Props)
        {
            var property = definition.Properties.FirstOrDefault(p => p.Name == name);
            if (property is null)
            {
                return $"property '{name}' is not part of event '{e.Name}'";
            }

            if (!Fits(property, value))
            {
                return $"property '{name}' of event '{e.Name}' has an invalid value";
            }
        }

        return null;
    }

    private static bool Fits(EventProperty property, JsonElement value) => property.Kind switch
    {
        PropertyKind.Text => value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text.Length <= property.MaxLength
            && (property.Allowed is null || property.Allowed.Contains(text)),
        PropertyKind.WholeNumber => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var whole) && whole >= 0 && whole <= property.Max,
        PropertyKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 && number <= property.Max,
        PropertyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };
}

public static class IngestEventsHandler
{
    public static async Task<Result<EventBatchResponse>> Handle(
        IngestEventsCommand command,
        IValidator<IngestEventsCommand> validator,
        IEventStore store,
        IInsightsSettings settings,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var limits = await settings.GetAsync(cancellationToken);
        if (command.Events is { Count: > 0 } && command.Events.Count > limits.MaxEventsPerBatch)
        {
            return Result.Failure<EventBatchResponse>("validation", $"a batch holds 1 to {limits.MaxEventsPerBatch} events");
        }

        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<EventBatchResponse>("validation", validation.Errors[0].ErrorMessage);
        }

        var now = clock.GetUtcNow();
        var rawCutoff = now.AddMonths(-limits.RawMonths);
        var technicalCutoff = now.AddDays(-limits.TechnicalDays);
        var consented = await store.HasAnalyticsConsentAsync(command.TravelerId, cancellationToken);

        var distinct = command.Events.DistinctBy(e => e.Id).ToList();
        var ignored = 0;
        var keep = new List<NewEvent>(distinct.Count);
        foreach (var e in distinct)
        {
            var essential = EventCatalogue.IsEssential(e.Name);
            var occurredAt = e.OccurredAt.ToUniversalTime();
            if ((!essential && !consented) || occurredAt < (essential ? technicalCutoff : rawCutoff))
            {
                ignored++;
                continue;
            }

            keep.Add(new NewEvent(e.Id, command.TravelerId, e.SessionId, e.Name, JsonSerializer.Serialize(e.Props ?? new Dictionary<string, JsonElement>()), e.AppVersion, e.Platform, occurredAt));
        }

        var accepted = keep.Count == 0 ? 0 : await store.InsertAsync(keep, cancellationToken);
        return Result.Success(new EventBatchResponse(accepted, command.Events.Count - ignored - accepted, ignored));
    }
}
