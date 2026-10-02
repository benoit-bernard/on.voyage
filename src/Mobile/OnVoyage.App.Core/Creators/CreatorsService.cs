using System.Globalization;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Interactions;
using OnVoyage.Creators.Contracts;
using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.App.Core.Creators;

/// <summary>One creator of the "Vu par les créateurs" block, ready to show.</summary>
public sealed record CreatorCard(
    Guid CreatorId,
    string Handle,
    string DisplayName,
    string? AvatarUrl,
    string? Tip,
    Guid? ContentId,
    string? ContentKind,
    string? ContentTitle,
    string? ContentUrl,
    string? Duration,
    bool IsCommercial,
    int Affinity);

/// <summary><paramref name="Total"/> counts every creator of the place; <paramref name="Cards"/> is what is shown.</summary>
public sealed record CreatorBlock(int Total, IReadOnlyList<CreatorCard> Cards)
{
    public static CreatorBlock Empty { get; } = new(0, []);
}

public static class CreatorLabels
{
    /// <summary>The block of a place shows three creators (F-30, annexe E); "Tous les créateurs" shows more.</summary>
    public const int BlockSize = 3;

    public const int AllLimit = 50;

    public const string Advertising = "Publicité";

    /// <summary>The platforms whose account the creator proved they own (F-26 badge).</summary>
    public static string Platform(string platform) => platform switch
    {
        "instagram" => "Instagram",
        "youtube" => "YouTube",
        "tiktok" => "TikTok",
        _ => platform,
    };

    public static string Kind(string? kind) => kind switch
    {
        "video" => "Vidéo",
        "photo" => "Photo",
        "carousel" => "Carrousel",
        "article" => "Article",
        _ => "Contenu",
    };

    public static string Duration(int? seconds) =>
        seconds is { } total and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}") : string.Empty;

    public static string Reason(string reason) => reason switch
    {
        ReportReasons.Inaccurate => "Information inexacte",
        ReportReasons.Misleading => "Trompeur",
        ReportReasons.UndeclaredAd => "Publicité non déclarée",
        ReportReasons.Inappropriate => "Contenu inapproprié",
        ReportReasons.Impersonation => "Usurpation d'identité",
        _ => "Autre",
    };

    public static string Followers(CreatorPageDto page) => page.FollowerCount is { } count
        ? count == 1 ? "1 abonné" : string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{count:N0} abonnés")
        : "Nouveau créateur";
}

/// <summary>
/// Creators on the traveler's side (T-1203): the block of a place, a creator page, follow and report. The order of the block is the
/// affinity with the traveler's tastes (§6.15), asked from Discovery, which already holds the profile; without an answer the order is the
/// one Creators gave. Content links are plain links out (D-17): opening one only records <c>creator_content_opened</c>.
/// </summary>
public sealed class CreatorsService(ICreatorsClient creators, InteractionRecorder recorder, MediaLocator media, IAnalyticsSink analytics)
{
    public async Task<CreatorBlock> LoadBlockAsync(Guid poiId, string destination, bool all, CancellationToken cancellationToken)
    {
        var block = await creators.GetPoiCreatorsAsync(poiId, CreatorLabels.AllLimit, cancellationToken);
        if (block is null || block.Items.Count == 0)
        {
            return CreatorBlock.Empty;
        }

        var affinities = await AffinitiesAsync(destination, cancellationToken);
        var cards = block.Items
            .Select(item => ToCard(item, affinities.GetValueOrDefault(item.Creator.Id)))
            .OrderByDescending(card => card.Affinity)
            .ThenBy(card => card.Handle, StringComparer.Ordinal)
            .Take(all ? CreatorLabels.AllLimit : CreatorLabels.BlockSize)
            .ToArray();
        return new CreatorBlock(Math.Max(block.Total, block.Items.Count), cards);
    }

    public Task<CreatorPageDto?> LoadPageAsync(string handle, CancellationToken cancellationToken) =>
        creators.GetCreatorAsync(handle.TrimStart('@'), cancellationToken);

    public string? Avatar(string? path) => media.Resolve(path);

    public string? Cover(string? path) => media.Resolve(path);

    /// <summary>Follows or unfollows. False when the creator is gone (the page then reloads); the server writes the learning signal, not the app.</summary>
    public async Task<bool?> SetFollowAsync(Guid creatorId, bool following, CancellationToken cancellationToken)
    {
        var state = await creators.SetFollowAsync(creatorId, following, cancellationToken);
        if (state is null)
        {
            return null;
        }

        analytics.Track(state.Following ? "creator_followed" : "creator_unfollowed", new Dictionary<string, object?> { ["creator_id"] = creatorId.ToString() });
        return state.Following;
    }

    /// <summary>The traveler opened a creator's content on its own platform: a mild taste signal on the place (+0.3, §6.2) and an event. Nothing else leaves.</summary>
    public async Task OpenedContentAsync(CreatorCard card, Guid poiId, IReadOnlyDictionary<string, double> weights, CancellationToken cancellationToken = default)
    {
        analytics.Track("creator_content_opened", new Dictionary<string, object?>
        {
            ["creator_id"] = card.CreatorId.ToString(),
            ["content_id"] = card.ContentId?.ToString(),
            ["poi_id"] = poiId.ToString(),
            ["surface"] = "place",
        });
        await recorder.RecordAsync(InteractionKinds.CreatorContentOpened, poiId, weights, cancellationToken: cancellationToken);
    }

    public async Task ReportAsync(string targetType, Guid targetId, string reason, CancellationToken cancellationToken) =>
        await creators.ReportAsync(new ReportRequest(targetType, targetId, reason), cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, int>> AffinitiesAsync(string destination, CancellationToken cancellationToken)
    {
        try
        {
            var forMe = await creators.GetCreatorsForMeAsync(destination, cancellationToken);
            return forMe?.Items.ToDictionary(item => item.CreatorId, item => item.Affinity) ?? new Dictionary<Guid, int>();
        }
        catch (HttpRequestException)
        {
            return new Dictionary<Guid, int>(); // the order of Creators is good enough when Discovery cannot be reached
        }
    }

    private CreatorCard ToCard(PoiCreatorItemDto item, int affinity) => new(
        item.Creator.Id,
        item.Creator.Handle,
        item.Creator.DisplayName,
        media.Resolve(item.Creator.AvatarPath),
        item.Tip,
        item.Content?.Id,
        item.Content?.Kind,
        item.Content?.Title,
        item.Content?.Url,
        item.Content is null ? null : CreatorLabels.Duration(item.Content.DurationSeconds),
        item.Content?.IsCommercial ?? false,
        affinity);
}
