using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Ports;

/// <summary>What the assistant reads of a content: its title, the excerpt of its caption (500 characters) and its chapters. Public texts of the creator; nothing about a traveler.</summary>
public sealed record GeotagInput(string Title, string? Caption, IReadOnlyList<Chapter> Chapters, string Language);

/// <summary>
/// Reads the places a creator's content talks about (F-28). The production adapter calls a language model (<c>Creators:GeoAssociation:Provider = openai</c>,
/// in the background only, never on a request path, never with traveler data); an offline deterministic adapter exists for tests and local runs.
/// Whatever the adapter, a mention is only a proposal: the matcher and the creator decide.
/// </summary>
public interface IPlaceMentionExtractor
{
    Task<IReadOnlyList<PlaceMention>> ExtractAsync(GeotagInput input, CancellationToken cancellationToken);
}

/// <summary>Settings of the geo-association (annexe E: <c>creators.geotag.*</c>).</summary>
public interface IGeoAssociationSettings
{
    /// <summary>False: nothing is analysed (no model configured). Contents then stay « not analysed » and nothing fails.</summary>
    bool Enabled { get; }

    /// <summary>« Tout valider » applies from this confidence (0.9).</summary>
    double BulkThreshold { get; }

    double MinProposal { get; }

    int MaxPerContent { get; }

    /// <summary>The model's own confidence that a mention is a real place, from which an unknown place is suggested to the editorial team.</summary>
    double SuggestionConfidence { get; }
}
