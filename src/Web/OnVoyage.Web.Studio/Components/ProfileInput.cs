using OnVoyage.Creators.Contracts;

namespace OnVoyage.Web.Studio.Components;

/// <summary>The profile fields of the form, before they become a <see cref="CreatorProfileRequest"/>.</summary>
public sealed class ProfileInput
{
    public static readonly IReadOnlyList<(string Code, string Label)> Languages = [("fr", "Français"), ("en", "Anglais"), ("es", "Espagnol"), ("it", "Italien"), ("de", "Allemand")];

    public string Handle { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Bio { get; set; }

    public string? AvatarPath { get; set; }

    public HashSet<string> LanguageCodes { get; } = new(StringComparer.Ordinal) { "fr" };

    public HashSet<string> Specialties { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Guid>? DestinationIds { get; set; }

    public IReadOnlyList<CreatorLinkDto>? Links { get; set; }

    public static ProfileInput From(StudioProfileDto creator)
    {
        var input = new ProfileInput
        {
            Handle = creator.Handle,
            DisplayName = creator.DisplayName,
            Bio = creator.Bio,
            AvatarPath = creator.AvatarPath,
            DestinationIds = creator.DestinationIds,
            Links = creator.Links,
        };
        input.LanguageCodes.Clear();
        input.LanguageCodes.UnionWith(creator.Languages);
        input.Specialties.UnionWith(creator.Specialties);
        return input;
    }

    public CreatorProfileRequest ToRequest() =>
        new(Handle.Trim(), DisplayName.Trim(), string.IsNullOrWhiteSpace(Bio) ? null : Bio.Trim(), string.IsNullOrWhiteSpace(AvatarPath) ? null : AvatarPath.Trim(), [.. LanguageCodes], [.. Specialties], DestinationIds, Links);
}
