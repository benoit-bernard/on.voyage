using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Core.Analytics;
using OnVoyage.Insights.Contracts;

namespace OnVoyage.App.Infrastructure.Http;

/// <summary>
/// Sends event batches to Insights through the Gateway with the traveler's bearer token (<c>POST /api/insights/v1/events</c>). A fault of the
/// network or of the server means « try again later »; a refusal of the batch itself means « never », so it does not block the ones behind it.
/// </summary>
internal sealed class HttpAnalyticsTransport(HttpClient http) : IAnalyticsTransport
{
    public async Task<AnalyticsSendOutcome> SendAsync(IReadOnlyList<EventDto> batch, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/insights/v1/events", new EventBatchRequest(batch), cancellationToken);
            return Classify(response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return AnalyticsSendOutcome.Retry;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AnalyticsSendOutcome.Retry; // the request timed out
        }
    }

    internal static AnalyticsSendOutcome Classify(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
        {
            return AnalyticsSendOutcome.Sent;
        }

        // 401/403 (session being renewed), 408, 429 and every 5xx are passing; any other 4xx will not change by waiting.
        return (int)status is >= 400 and < 500 && status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            ? AnalyticsSendOutcome.Rejected
            : AnalyticsSendOutcome.Retry;
    }
}
