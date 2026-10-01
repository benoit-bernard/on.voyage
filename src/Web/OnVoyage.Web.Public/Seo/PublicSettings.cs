namespace OnVoyage.Web.Public.Seo;

/// <summary>Settings of the public site (<c>Public:*</c>).</summary>
public sealed class PublicSettings
{
    public string BaseUrl { get; set; } = "https://on.voyage";

    public string[] Languages { get; set; } = ["fr", "en"];

    /// <summary>Destinations listed on the home page and in the sitemap. The Catalog has no list endpoint yet.</summary>
    public string[] Destinations { get; set; } = ["marseille"];

    public string AppStoreUrl { get; set; } = "https://apps.apple.com/app/on-voyage/id0000000000";

    public string PlayStoreUrl { get; set; } = "https://play.google.com/store/apps/details?id=voyage.on.app";

    /// <summary>Universal links (Apple) and App Links (Android): replaced by the real identifiers once the developer accounts exist (H-003).</summary>
    public string AppleAppId { get; set; } = "TEAMID.voyage.on.app";

    public string AndroidPackage { get; set; } = "voyage.on.app";

    public string AndroidCertSha256 { get; set; } = "00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00";

    public const string TdmPolicyPath = "/fr/conditions#fouille-de-textes";

    public string Absolute(string path) => BaseUrl.TrimEnd('/') + path;
}
