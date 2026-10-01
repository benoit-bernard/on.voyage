using System.Net.Http.Json;
using OnVoyage.App.Core.Reports;

namespace OnVoyage.App.Infrastructure.Http;

internal sealed class HttpReportClient(HttpClient http) : IReportClient
{
    public async Task ReportAsync(Guid storyId, ReportKind kind, string text, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"api/factory/v1/stories/{storyId}/reports", new { reason = ReportLabels.Compose(kind, text) }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
