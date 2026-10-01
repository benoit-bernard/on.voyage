using OnVoyage.Discovery.Contracts;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Privacy;

/// <summary>
/// Everything the privacy settings need (F-22): consents (Platform), language and ethical mode, readable profile, history (Discovery), then the
/// right to export and to be forgotten (Platform). Network failures surface as <see cref="HttpRequestException"/>.
/// </summary>
public interface IPrivacyApi
{
    Task<IReadOnlyList<ConsentDto>> GetConsentsAsync(CancellationToken cancellationToken);

    Task SetConsentAsync(string kind, bool granted, string textVersion, CancellationToken cancellationToken);

    Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken);

    Task<SettingsDto> UpdateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken);

    Task<ProfileDto> GetProfileAsync(CancellationToken cancellationToken);

    Task<ProfileDto> CorrectProfileAsync(IReadOnlyList<ProfileCorrection> corrections, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryItemDto>> GetHistoryAsync(CancellationToken cancellationToken);

    Task<InteractionBatchResponse> DeleteHistoryAsync(Guid poiId, CancellationToken cancellationToken);

    Task<ExportStatusDto> StartExportAsync(CancellationToken cancellationToken);

    Task<ExportStatusDto> GetExportAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>The archive as JSON text, once the export is ready.</summary>
    Task<string> DownloadExportAsync(Guid exportId, CancellationToken cancellationToken);

    Task<DeletionStatusDto> RequestDeletionAsync(CancellationToken cancellationToken);

    Task<DeletionStatusDto?> GetDeletionAsync(CancellationToken cancellationToken);
}

/// <summary>Version of the text shown next to the statistics consent; recorded with the choice (§16.3).</summary>
public static class ConsentTexts
{
    public const string AnalyticsVersion = "analytics-2026-10";
    public const string AnalyticsKind = "analytics";
}
