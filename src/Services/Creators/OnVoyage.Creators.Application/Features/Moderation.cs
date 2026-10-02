using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Features;

public sealed record ReportCommand(Guid Traveler, ReportRequest Report);

public sealed record ListModerationQuery(string? Status, int Limit);

public sealed record DecideCaseCommand(Guid Actor, Guid CaseId, string Decision, string? StatementOfReasons);

/// <summary>
/// Simple moderation (F-33, MVP-0): travelers report, an administrator decides with a statement of reasons. A decision that upholds the
/// report takes the target down (a creator is suspended, a content hidden, an association rejected, a tip hidden) and announces it.
/// Notifying the creator and the appeal are part of T-1213.
/// </summary>
public static class ModerationHandler
{
    public static async Task<Result<ReportReceiptDto>> Handle(ReportCommand command, IModerationRepository cases, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var report = command.Report;
        if (!ModerationTargets.All.Contains(report.TargetType) || !ReportReasons.All.Contains(report.Reason))
        {
            return Result.Failure<ReportReceiptDto>("validation", "Type de cible ou motif inconnu.");
        }

        if (!await cases.TargetExistsAsync(report.TargetType, report.TargetId, cancellationToken))
        {
            return Result.Failure<ReportReceiptDto>("target_not_found", "Élément introuvable.");
        }

        // The same traveler reporting the same thing twice adds nothing.
        if (await cases.FindOpenAsync(command.Traveler, report.TargetType, report.TargetId, report.Reason, cancellationToken) is { } existing)
        {
            return Result.Success(new ReportReceiptDto(existing.Id));
        }

        var moderationCase = ModerationCase.Open(Guid.CreateVersion7(), report.TargetType, report.TargetId, report.Reason, command.Traveler, clock.GetUtcNow());
        await cases.StageAsync(moderationCase, cancellationToken);
        await unit.CommitAsync([], cancellationToken);
        return Result.Success(new ReportReceiptDto(moderationCase.Id));
    }

    public static async Task<Result<IReadOnlyList<ModerationCaseDto>>> Handle(ListModerationQuery query, ICreatorQueries queries, CancellationToken cancellationToken)
    {
        if (query.Status is not null and not (ModerationStatuses.Open or ModerationStatuses.Decided))
        {
            return Result.Failure<IReadOnlyList<ModerationCaseDto>>("validation", "Statut inconnu.");
        }

        return Result.Success(await queries.ListModerationAsync(query.Status, Math.Clamp(query.Limit, 1, 200), cancellationToken));
    }

    public static async Task<Result<ModerationCaseDto>> Handle(DecideCaseCommand command, IModerationRepository cases, ICreatorRepository creators, IContentRepository contents, ICreatorQueries queries, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await cases.FindAsync(command.CaseId, cancellationToken) is not { } moderationCase)
        {
            return Result.Failure<ModerationCaseDto>("case_not_found", "Signalement introuvable.");
        }

        var now = clock.GetUtcNow();
        var (decided, violation) = moderationCase.Decide(command.Decision, command.StatementOfReasons, now);
        if (decided is null)
        {
            return Result.Failure<ModerationCaseDto>(violation!);
        }

        await cases.StageAsync(decided, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.Actor, "moderation.decide", $"{decided.TargetType}:{decided.TargetId}", $"case={decided.Id}; decision={decided.Decision}; reason={decided.Reason}", now)];
        if (decided.Decision == ModerationCase.Upheld)
        {
            events.AddRange(await TakeDownAsync(decided, creators, contents, now, cancellationToken));
        }

        await unit.CommitAsync(events, cancellationToken);
        return Result.Success((await queries.GetModerationAsync(decided.Id, cancellationToken))!);
    }

    private static async Task<IReadOnlyList<object>> TakeDownAsync(ModerationCase moderationCase, ICreatorRepository creators, IContentRepository contents, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<object> events = [];
        switch (moderationCase.TargetType)
        {
            case ModerationTargets.Creator:
                if (await creators.FindAsync(moderationCase.TargetId, cancellationToken) is { } creator && creator.Status != CreatorStatuses.Suspended)
                {
                    await creators.StageAsync(creator.Suspended(now), cancellationToken);
                    if (creator.Status == CreatorStatuses.Published)
                    {
                        events.Add(CreatorEvents.Unpublished(creator, "moderation", now));
                    }
                }

                break;

            case ModerationTargets.Content:
                if (await contents.FindContentAsync(moderationCase.TargetId, cancellationToken) is { } content && content.Status == ContentStatuses.Imported)
                {
                    var hidden = content with { Status = ContentStatuses.Hidden };
                    await contents.StageContentAsync(hidden, cancellationToken);
                    events.AddRange((await contents.ListLinksOfContentAsync(content.Id, cancellationToken)).Where(link => link.IsValidated).Select(link => CreatorEvents.LinkChanged(link, hidden, false, now)));
                }

                break;

            case ModerationTargets.PlaceLink:
                if (await contents.FindLinkAsync(moderationCase.TargetId, cancellationToken) is { } link && link.Status != PlaceLinkStatuses.Rejected)
                {
                    var linkContent = link.ContentId is { } contentId ? await contents.FindContentAsync(contentId, cancellationToken) : null;
                    await contents.StageLinkAsync(link.WithStatus(PlaceLinkStatuses.Rejected, now), cancellationToken);
                    if (link.IsValidated && (linkContent is null || linkContent.IsOnline))
                    {
                        events.Add(CreatorEvents.LinkChanged(link, linkContent, false, now));
                    }
                }

                break;

            case ModerationTargets.Tip:
                if (await contents.FindTipAsync(moderationCase.TargetId, cancellationToken) is { } tip)
                {
                    await contents.StageTipAsync(tip with { Status = TipStatuses.Hidden, UpdatedAt = now }, cancellationToken);
                }

                break;
        }

        return events;
    }
}
