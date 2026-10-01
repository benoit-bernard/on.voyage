namespace OnVoyage.Gateway.Edge;

/// <summary>
/// Abuse-protection settings of the Gateway (<c>security.*</c> of annexe E), refreshed from Platform. The defaults below are the
/// annexe E values: they apply until the first successful fetch and whenever Platform is unreachable at start-up.
/// </summary>
public sealed record EdgeSettings(int RatePerTravelerPerMinute, int RatePerIpPerMinute, IReadOnlyList<string> BlockedUserAgents, int RefreshSeconds)
{
    public static EdgeSettings Defaults { get; } = new(
        120,
        300,
        [
            "GPTBot", "OAI-SearchBot", "ChatGPT-User", "ClaudeBot", "Claude-User", "Claude-SearchBot", "anthropic-ai", "Google-Extended",
            "GoogleOther", "Applebot-Extended", "PerplexityBot", "Perplexity-User", "CCBot", "Bytespider", "Meta-ExternalAgent",
            "Meta-ExternalFetcher", "FacebookBot", "Amazonbot", "cohere-ai", "cohere-training-data-crawler", "DuckAssistBot", "Diffbot",
            "Omgilibot", "Timpibot", "YouBot", "MistralAI-User",
        ],
        60);
}

/// <summary>Holds the settings currently in force. Reads are lock-free; the refresher swaps the whole record.</summary>
public sealed class EdgeSettingsStore
{
    private volatile EdgeSettings _current = EdgeSettings.Defaults;

    public EdgeSettings Current => _current;

    public void Update(EdgeSettings settings) => _current = settings;
}
