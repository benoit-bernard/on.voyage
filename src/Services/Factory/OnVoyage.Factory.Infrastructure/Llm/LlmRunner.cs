using System.ClientModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Infrastructure.Persistence;

namespace OnVoyage.Factory.Infrastructure.Llm;

/// <summary>Meters of §8.10. <c>onvoyage.llm.cost_usd</c> is what the specification names.</summary>
internal static class LlmMetrics
{
    public static readonly Meter Meter = new("OnVoyage.Factory");
    public static readonly Counter<double> CostUsd = Meter.CreateCounter<double>("onvoyage.llm.cost_usd", "USD", "Estimated cost of language-model and speech calls");
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("onvoyage.llm.tokens", "token", "Tokens used by language-model calls");
}

/// <summary>Writes one row per call (model, tokens, cost, duration, content id) and updates the meters. Uses its own scope so a failed job still keeps its cost.</summary>
internal sealed class LlmUsageRecorder(IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider clock)
{
    public async Task RecordAsync(string kind, string model, string? promptId, string? promptVersion, Guid? contentId, int inputTokens, int outputTokens, TimeSpan duration, bool succeeded, double extraCost = 0d)
    {
        var inputPrice = configuration.GetValue<double?>($"Factory:Llm:Pricing:{model}:InputPerMillion") ?? 0d;
        var outputPrice = configuration.GetValue<double?>($"Factory:Llm:Pricing:{model}:OutputPerMillion") ?? 0d;
        var cost = (inputTokens * inputPrice / 1_000_000d) + (outputTokens * outputPrice / 1_000_000d) + extraCost;

        var tags = new TagList { { "kind", kind }, { "model", model } };
        LlmMetrics.CostUsd.Add(cost, tags);
        LlmMetrics.Tokens.Add(inputTokens + outputTokens, tags);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        db.LlmCalls.Add(new LlmCallRow
        {
            Id = Guid.CreateVersion7(), Kind = kind, Model = model, PromptId = promptId, PromptVersion = promptVersion, ContentId = contentId,
            InputTokens = inputTokens, OutputTokens = outputTokens, CostUsd = cost, DurationMs = (int)duration.TotalMilliseconds, Succeeded = succeeded, CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Runs a structured-output prompt and returns the parsed JSON. Any provider failure becomes an <see cref="ExternalServiceException"/> so the job is retried.</summary>
internal sealed class LlmRunner(IChatClient chat, IConfiguration configuration, LlmUsageRecorder usage, ILogger<LlmRunner> logger)
{
    public async Task<(JsonDocument Json, string Model)> RunAsync(
        PromptTemplate prompt, IReadOnlyDictionary<string, string> values, IEnumerable<string> taxonomyCodes, Guid? contentId, CancellationToken cancellationToken, float temperature = 0f)
    {
        var model = configuration[prompt.ModelSetting];
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"{prompt.ModelSetting} is not configured.");
        }

        var (system, user) = prompt.Render(values);
        using var schema = JsonDocument.Parse(prompt.SchemaWith(taxonomyCodes));
        var options = new ChatOptions
        {
            ModelId = model,
            Temperature = temperature,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), prompt.SchemaName, $"Structured output of {prompt.Id} v{prompt.Version}"),
        };

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)], options, cancellationToken);
            await usage.RecordAsync(prompt.Id, model, prompt.Id, prompt.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), contentId,
                (int)(response.Usage?.InputTokenCount ?? 0), (int)(response.Usage?.OutputTokenCount ?? 0), Stopwatch.GetElapsedTime(started), true);

            return (JsonDocument.Parse(response.Text), model);
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Model call {Prompt} failed: {Error}", prompt.Id, ex.GetType().Name);
            await usage.RecordAsync(prompt.Id, model, prompt.Id, prompt.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), contentId, 0, 0, Stopwatch.GetElapsedTime(started), false);
            throw new ExternalServiceException($"The model call '{prompt.Id}' failed ({ex.GetType().Name}).", ex);
        }
    }
}
