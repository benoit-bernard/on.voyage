using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OnVoyage.Factory.Application;
using Microsoft.Extensions.AI;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Infrastructure.Audio;
using OnVoyage.Factory.Infrastructure.Llm;
using OpenAI;
using System.ClientModel;
using OnVoyage.Factory.Infrastructure.Osm;
using OnVoyage.Factory.Infrastructure.Persistence;
using OnVoyage.Factory.Infrastructure.Sources;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddFactoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<FactoryDbContext>(options => FactoryPersistence.Configure(options, connectionString));
        services.AddScoped<IPlaceStore, PlaceStore>();
        services.AddSingleton<IDestinationCatalog, ConfiguredDestinationCatalog>();
        services.AddSingleton<IClassificationRuleProvider, JsonClassificationRuleProvider>();
        services.AddSingleton(provider => configuration.GetSection("Factory:Heritage").Get<HeritageClasses>() ?? HeritageClasses.Defaults);
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient<IOsmImporter, OsmImporter>(client => client.Timeout = TimeSpan.FromHours(2));
        services.AddHttpClient<WikimediaRequester>(client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddScoped<IWikidataClient, WikidataSparqlClient>();
        services.AddScoped<IPageviewsClient, WikimediaPageviewsClient>();

        services.AddFactoryContent(configuration);

        services.AddHealthChecks().AddCheck<FactoryDatabaseHealthCheck>("factory-db", tags: ["ready"]);
        return services;
    }

    private static readonly string[] RequiredLlmSettings = ["OpenAI:ApiKey", "Factory:Llm:ExtractorModel", "Factory:Llm:WriterModel", "Factory:Llm:VerifierModel", "Factory:Llm:ClassifierModel"];

    private static void AddFactoryContent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IContentStore, ContentStore>();
        services.AddScoped<IWikipediaTextClient, WikipediaTextClient>();
        services.AddSingleton<IContentSettingsProvider>(new ConfiguredContentSettings(configuration.GetSection("Factory:Content").Get<ContentSettings>() ?? new ContentSettings()));
        services.AddSingleton<IAudioProcessor, FfmpegAudioProcessor>();
        services.AddSingleton<IMediaStorage, LocalMediaStorage>();
        services.AddSingleton<LlmUsageRecorder>();
        services.AddSingleton<LlmRunner>();
        services.AddScoped<IFactExtractor, LlmFactExtractor>();
        services.AddScoped<IStoryWriter, LlmStoryWriter>();
        services.AddScoped<IStoryVerifier, LlmStoryVerifier>();
        services.AddScoped<IPlaceModelClassifier, LlmPlaceClassifier>();

        var provider = configuration["Factory:Llm:Provider"] ?? "openai";
        if (provider == "openai")
        {
            // Fail at startup rather than on the first job: a worker that cannot call the model must not accept work.
            var missing = RequiredLlmSettings
                .Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException($"Factory:Llm:Provider is 'openai' but these settings are missing: {string.Join(", ", missing)}. Set Factory:Llm:Provider=disabled to run without a model (Development only).");
            }

            var client = new OpenAIClient(new ApiKeyCredential(configuration["OpenAI:ApiKey"]!));
            services.AddSingleton(client);
            services.AddSingleton<ITextToSpeechProvider, OpenAiSpeechProvider>();
            services.AddSingleton<IChatClient>(provider => client.GetChatClient(configuration["Factory:Llm:WriterModel"]!).AsIChatClient());
        }
        else if (provider == "disabled")
        {
            services.AddSingleton<IChatClient, DisabledChatClient>();
            services.AddSingleton<ITextToSpeechProvider, DisabledSpeechProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Unknown Factory:Llm:Provider '{provider}'.");
        }
    }

    private sealed class ConfiguredContentSettings(ContentSettings settings) : IContentSettingsProvider
    {
        public ContentSettings Current { get; } = settings;
    }

    private sealed class DisabledChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The language model is disabled (Factory:Llm:Provider=disabled).");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The language model is disabled (Factory:Llm:Provider=disabled).");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class DisabledSpeechProvider : ITextToSpeechProvider
    {
        public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Speech is disabled (Factory:Llm:Provider=disabled).");
    }

    /// <summary>Registers the migration task. Call it after <c>UseWolverine</c>; it runs before the host accepts requests.</summary>
    public static IServiceCollection AddFactoryInitializer(this IServiceCollection services) => services.AddHostedService<FactoryInitializer>();

    private sealed class FactoryInitializer(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Factory:Migrate", true))
            {
                await scope.ServiceProvider.GetRequiredService<FactoryDbContext>().Database.MigrateAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FactoryDatabaseHealthCheck(FactoryDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Factory database unreachable.");
    }
}
