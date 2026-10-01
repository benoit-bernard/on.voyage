using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Factory.Contracts;

namespace OnVoyage.Discovery.Application.IntegrationEvents;

/// <summary>
/// Discovery keeps what the learning rule and the onboarding need from Factory's publications: the vector of each place and the onboarding
/// clips. (The cahier routes this through the Catalog's <c>PoiProjectionChangedV1</c>, which arrives with T-502; reading Factory's events
/// directly gives the same data now.) Consumption is idempotent: only a higher version changes anything.
/// </summary>
public static class PoiPublishedHandler
{
    public static Task Handle(PoiPublishedV1 published, IProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyPlaceAsync(
            new PlaceProjection(
                published.PoiId,
                published.Slug,
                published.NameFr,
                published.Destination.Slug,
                published.Latitude,
                published.Longitude,
                published.Crowd.Peak,
                published.AccessRegulated,
                published.Interests.GroupBy(i => i.TaxonomyCode).ToDictionary(g => g.Key, g => (double)g.Max(i => i.Weight)),
                published.ImportanceScore / 100d,
                published.ContentQualityScore,
                published.HiddenGem,
                published.Fragile,
                true,
                published.Version),
            cancellationToken);
}

public static class PoiUnpublishedHandler
{
    public static Task Handle(PoiUnpublishedV1 unpublished, IProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.UnpublishPlaceAsync(unpublished.PoiId, unpublished.Version, cancellationToken);
}

public static class StoryPublishedHandler
{
    public const string OnboardingKind = "onboarding_clip";

    public static Task Handle(StoryPublishedV1 published, IProjectionWriter writer, CancellationToken cancellationToken)
    {
        if (published.Kind != OnboardingKind)
        {
            var parts = published.AudioParts.ToDictionary(part => part.Part, part => part.Path);
            return writer.ApplyStoryAsync(new StoryProjection(published.StoryId, published.PoiId, published.Lang, published.Kind, published.DurationSeconds, published.IsPremium, parts, published.Version), cancellationToken);
        }

        var main = published.AudioParts.FirstOrDefault(part => part.Part == "main");
        return main is null
            ? writer.RemoveClipAsync(published.StoryId, cancellationToken)
            : writer.ApplyClipAsync(new ClipProjection(published.StoryId, published.PoiId, published.Lang, published.Title, main.Path, main.DurationSeconds, published.Version), cancellationToken);
    }
}

public static class StoryUnpublishedHandler
{
    internal static async Task Both(IProjectionWriter writer, Guid storyId, CancellationToken cancellationToken)
    {
        await writer.RemoveClipAsync(storyId, cancellationToken);
        await writer.RemoveStoryAsync(storyId, cancellationToken);
    }

    public static Task Handle(StoryUnpublishedV1 unpublished, IProjectionWriter writer, CancellationToken cancellationToken) =>
        Both(writer, unpublished.StoryId, cancellationToken);
}

public static class StoryArchivedHandler
{
    public static Task Handle(StoryArchivedV1 archived, IProjectionWriter writer, CancellationToken cancellationToken) =>
        StoryUnpublishedHandler.Both(writer, archived.StoryId, cancellationToken);
}
