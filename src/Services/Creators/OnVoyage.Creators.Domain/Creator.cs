using OnVoyage.Taxonomy;

namespace OnVoyage.Creators.Domain;

/// <summary>A refused business rule: <c>Code</c> is stable (it becomes the Problem Details type), <c>Message</c> is for the administrator.</summary>
public sealed record Violation(string Code, string Message);

public static class CreatorStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Suspended = "suspended";
}

public static class TermsVersions
{
    /// <summary>The signed consent of a founding creator (F-26, H-009) stands for the acceptance of the creator terms.</summary>
    public const string Founder = "fondateur";
}

public sealed record CreatorLink(string Kind, string Url);

/// <summary>What an administrator (or, later, the creator in Studio) edits.</summary>
public sealed record CreatorProfile(
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarPath,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Specialties,
    IReadOnlyList<Guid> DestinationIds,
    IReadOnlyList<CreatorLink> Links);

public sealed record Creator(
    Guid Id,
    Guid? AccountId,
    CreatorProfile Profile,
    string Status,
    bool Founding,
    string? TermsVersion,
    string? TermsDocumentRef,
    DateTimeOffset? TermsAcceptedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string Handle => Profile.Handle;

    /// <summary>True once the creator terms were accepted, or the founder consent is recorded with the reference of its document.</summary>
    public bool TermsAccepted =>
        TermsVersion is { Length: > 0 } && TermsAcceptedAt is not null
        && (TermsVersion != TermsVersions.Founder || !string.IsNullOrWhiteSpace(TermsDocumentRef));

    /// <summary>Publication rule of F-26: terms (or founder consent) first, then at least one specialty. Null when the page can go public.</summary>
    public Violation? PublishBlock =>
        !TermsAccepted
            ? new Violation("terms_required", "Les CGU créateurs ne sont pas acceptées et aucun consentement fondateur n'est enregistré.")
            : Profile.Specialties.Count == 0
                ? new Violation("specialty_required", "Au moins une spécialité est requise pour publier.")
                : null;

    public static Creator NewDraft(Guid id, CreatorProfile profile, bool founding, DateTimeOffset now) =>
        new(id, null, profile, CreatorStatuses.Draft, founding, null, null, null, now, now);

    public Creator WithProfile(CreatorProfile profile, DateTimeOffset now) => this with { Profile = profile, UpdatedAt = now };

    public Creator WithFounderConsent(string documentRef, DateTimeOffset acceptedAt, DateTimeOffset now) =>
        this with { Founding = true, TermsVersion = TermsVersions.Founder, TermsDocumentRef = documentRef.Trim(), TermsAcceptedAt = acceptedAt, UpdatedAt = now };

    public Creator WithAccount(Guid accountId, DateTimeOffset now) => this with { AccountId = accountId, UpdatedAt = now };

    /// <summary>The caller has already checked <see cref="PublishBlock"/>.</summary>
    public Creator Published(DateTimeOffset now) => this with { Status = CreatorStatuses.Published, UpdatedAt = now };

    public Creator Unpublished(DateTimeOffset now) => this with { Status = CreatorStatuses.Draft, UpdatedAt = now };

    public Creator Suspended(DateTimeOffset now) => this with { Status = CreatorStatuses.Suspended, UpdatedAt = now };

    public Creator WithHandle(string handle, DateTimeOffset now) => this with { Profile = Profile with { Handle = handle }, UpdatedAt = now };
}

/// <summary>Checks and normalizes a profile before it is stored.</summary>
public static class CreatorProfileRules
{
    public const int MaxDisplayName = 80;
    public const int MaxBio = 300;
    public const int MaxSpecialties = 5;
    public const int MaxLanguages = 6;
    public const int MaxLinks = 10;
    public const int MaxPathLength = 300;

    public static readonly IReadOnlyList<string> LinkKinds = ["instagram", "youtube", "tiktok", "website"];

    public static (CreatorProfile? Profile, Violation? Violation) Normalize(CreatorProfile input)
    {
        if (!Handles.TryNormalize(input.Handle, out var handle))
        {
            return Fail("invalid_handle", $"Le handle doit faire de {Handles.MinLength} à {Handles.MaxLength} caractères (lettres, chiffres, point et tiret bas).");
        }

        var name = (input.DisplayName ?? string.Empty).Trim();
        if (name.Length is 0 or > MaxDisplayName)
        {
            return Fail("validation", $"Le nom affiché est obligatoire ({MaxDisplayName} caractères au plus).");
        }

        var bio = string.IsNullOrWhiteSpace(input.Bio) ? null : input.Bio.Trim();
        if (bio is { Length: > MaxBio })
        {
            return Fail("validation", $"La bio fait {MaxBio} caractères au plus.");
        }

        var avatar = string.IsNullOrWhiteSpace(input.AvatarPath) ? null : input.AvatarPath.Trim();
        if (avatar is { Length: > MaxPathLength } || avatar is not null && avatar.Any(char.IsWhiteSpace))
        {
            return Fail("validation", "Le chemin de la photo est invalide.");
        }

        var languages = input.Languages.Select(language => language.Trim().ToLowerInvariant()).Where(language => language.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (languages.Length > MaxLanguages || languages.Any(language => language.Length is < 2 or > 3 || !language.All(char.IsAsciiLetterLower)))
        {
            return Fail("validation", "Les langues sont des codes à 2 ou 3 lettres (6 au plus).");
        }

        var specialties = input.Specialties.Select(code => code.Trim()).Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (specialties.Length > MaxSpecialties)
        {
            return Fail("validation", $"{MaxSpecialties} spécialités au plus.");
        }

        if (specialties.FirstOrDefault(code => !Interests.All.Contains(code)) is { } unknown)
        {
            return Fail("validation", $"Spécialité inconnue de la taxonomie : {unknown}.");
        }

        var links = new List<CreatorLink>();
        foreach (var link in input.Links)
        {
            var kind = link.Kind.Trim().ToLowerInvariant();
            if (!LinkKinds.Contains(kind) || !Uri.TryCreate(link.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || link.Url.Length > MaxPathLength)
            {
                return Fail("validation", "Un lien de réseau doit être une adresse https d'Instagram, YouTube, TikTok ou d'un site.");
            }

            links.Add(new CreatorLink(kind, uri.AbsoluteUri));
        }

        if (links.Count > MaxLinks)
        {
            return Fail("validation", $"{MaxLinks} liens au plus.");
        }

        return (new CreatorProfile(handle, name, bio, avatar, languages, specialties, [.. input.DestinationIds.Distinct()], links), null);
    }

    private static (CreatorProfile?, Violation?) Fail(string code, string message) => (null, new Violation(code, message));
}
