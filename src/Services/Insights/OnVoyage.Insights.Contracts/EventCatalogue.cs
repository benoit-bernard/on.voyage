namespace OnVoyage.Insights.Contracts;

public enum PropertyKind
{
    Text,
    WholeNumber,
    Number,
    Boolean,
}

/// <param name="Allowed">When set, the only values a text property may take.</param>
/// <param name="MaxLength">Longest text accepted.</param>
/// <param name="Max">Largest number accepted (all numbers are non-negative).</param>
public sealed record EventProperty(string Name, PropertyKind Kind, IReadOnlyList<string>? Allowed = null, int MaxLength = 64, double Max = 1_000_000);

public sealed record EventDefinition(string Name, bool Essential, IReadOnlyList<EventProperty> Properties);

/// <summary>
/// The catalogue of application events (§17.3), shared by the app (what it may queue) and Insights (what it accepts). Anything not listed here
/// is refused: this is how coordinates, free text and identifiers of the device stay out. <c>app_version</c> and <c>platform</c> travel as
/// fields of <see cref="EventDto"/>, so the essential events only add an error <c>code</c>.
/// </summary>
public static class EventCatalogue
{
    private static EventProperty Text(string name, int max = 64) => new(name, PropertyKind.Text, null, max);

    private static EventProperty Int(string name) => new(name, PropertyKind.WholeNumber);

    private static EventProperty Flag(string name) => new(name, PropertyKind.Boolean);

    private static EventProperty OneOf(string name, params string[] values) => new(name, PropertyKind.Text, values);

    private static EventDefinition Event(string name, params EventProperty[] properties) => new(name, false, properties);

    private static EventDefinition Essential(string name) => new(name, true, [Text("code", 32)]);

    private static readonly EventProperty Poi = Text("poi_id");
    private static readonly EventProperty Surface = Text("surface", 32);
    private static readonly EventProperty[] AudioProperties = [Text("story_id"), Int("version"), new("percent", PropertyKind.Number, Max: 100), OneOf("trigger", "manual", "auto")];
    private static readonly EventProperty[] RecommendationProperties =
    [
        Poi, Surface, Int("rank"), OneOf("cohort", Cohorts.Control, Cohorts.Personalized), Text("weights_version"), Flag("is_exploration"),

        // Not in §17.3: the strategic KPI (§26) groups the click rate by ProfileDepth, which only the app knows at that moment.
        Int("profile_depth"),
    ];

    public static IReadOnlyList<EventDefinition> All { get; } =
    [
        Event("app_open", Flag("cold_start")),
        Event("onboarding_started", Int("answers_count")),
        Event("onboarding_completed", Int("answers_count")),
        Event("onboarding_skipped", Int("answers_count")),
        Event("location_permission_result", OneOf("level", "denied", "when_in_use", "always")),
        Event("poi_viewed", Poi, Surface),
        Event("recommendation_viewed", RecommendationProperties),
        Event("recommendation_clicked", RecommendationProperties),
        Event("audio_started", AudioProperties),
        Event("audio_progress", AudioProperties),
        Event("audio_completed", AudioProperties),
        Event("audio_skipped", AudioProperties),
        Event("audio_replayed", AudioProperties),
        Event("story_triggered", Poi, Text("mode", 16), Int("distance_bucket_50m")),
        Event("poi_liked", Poi, Text("scope", 16)),
        Event("poi_disliked", Poi, Text("scope", 16)),
        Event("poi_meh", Poi, Text("scope", 16)),
        Event("poi_saved", Poi, Text("scope", 16)),
        Event("poi_unsaved", Poi, Text("scope", 16)),
        Event("navigation_started", Poi, Text("kind", 16)),
        Event("external_link_opened", Poi, Text("kind", 16)),
        Event("surprise_requested", Int("results_count")),
        Event("search_performed", Int("results_count")),
        Event("download_started", Text("destination"), Text("size_bucket", 16)),
        Event("download_completed", Text("destination"), Text("size_bucket", 16)),
        Event("download_failed", Text("destination"), Text("size_bucket", 16)),
        Event("paywall_viewed", Text("product_id")),
        Event("purchase_completed", Text("product_id")),
        Event("purchase_failed", Text("product_id")),
        Event("creator_profile_viewed", Text("creator_id"), Text("content_id"), Poi, Surface),
        Event("creator_card_viewed", Text("creator_id"), Text("content_id"), Poi, Surface),
        Event("creator_content_opened", Text("creator_id"), Text("content_id"), Poi, Surface),
        Event("creator_followed", Text("creator_id")),
        Event("creator_unfollowed", Text("creator_id")),
        Event("creator_list_viewed", Text("list_id"), Text("creator_id")),
        Event("creator_list_saved", Text("list_id"), Text("creator_id")),
        Event("voyage_comme_started", Text("list_id"), Text("creator_id")),
        Event("share_created", Text("target_type", 16)),
        Event("install_attributed", Text("creator_handle")),
        Essential("app_crash"),
        Essential("audio_error"),
        Essential("gps_loss"),
    ];

    private static readonly Dictionary<string, EventDefinition> ByName = All.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, out EventDefinition definition) => ByName.TryGetValue(name, out definition!);

    /// <summary>True for the technical events that leave the device without the statistics consent (§16.3).</summary>
    public static bool IsEssential(string name) => ByName.TryGetValue(name, out var definition) && definition.Essential;

    public static IReadOnlyList<string> EssentialNames { get; } = [.. All.Where(definition => definition.Essential).Select(definition => definition.Name)];
}
