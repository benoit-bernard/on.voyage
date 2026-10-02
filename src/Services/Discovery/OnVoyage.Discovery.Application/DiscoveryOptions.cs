namespace OnVoyage.Discovery.Application;

/// <summary>Settings of the Discovery service (<c>Discovery:*</c>).</summary>
/// <param name="AllowTextOnlyStories">
/// Whether the discovery mode may be offered places whose story was published without audio (no TTS voice at the time, as in the Marseille
/// snapshot): the device then reads the text. On for the MVP-0; <c>Discovery:AllowTextOnlyStories</c> = false restores "recorded stories only".
/// </param>
public sealed record DiscoveryOptions(bool AllowTextOnlyStories = true);
