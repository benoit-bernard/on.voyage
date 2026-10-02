using System.ClientModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Infrastructure.Audio;
using OnVoyage.Factory.Infrastructure.Llm;
using OnVoyage.Factory.Infrastructure.Osm;
using OnVoyage.Factory.Infrastructure.Persistence;
using OnVoyage.Factory.Infrastructure.Sources;
using OpenAI;
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
        services.AddScoped<OnVoyage.Factory.Application.Features.DataRights.IDataRightsStore, DataRightsStore>();
        services.AddSingleton<IDestinationCatalog, ConfiguredDestinationCatalog>();
        services.AddSingleton<IClassificationRuleProvider, JsonClassificationRuleProvider>();
        services.AddSingleton(provider => configuration.GetSection("Factory:Heritage").Get<HeritageClasses>() ?? HeritageClasses.Defaults);
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient<IOsmImporter, OsmImporter>(client => client.Timeout = TimeSpan.FromHours(2));
        services.AddHttpClient<WikimediaRequester>(client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddScoped<OnVoyage.Factory.Application.Features.Videos.IVideoStore, VideoStore>();
        services.AddScoped<OnVoyage.Factory.Application.Features.Videos.IVideoQuotaStore, VideoQuotaStore>();
        services.AddSingleton(new OnVoyage.Factory.Application.Features.Videos.VideoQuotaOptions(
            configuration.GetValue("YouTube:DailyQuotaUnits", 10_000), 100, 1, configuration.GetValue("YouTube:SearchCacheHours", 24)));
        services.AddHttpClient<OnVoyage.Factory.Application.Features.Videos.IVideoSearch, YouTubeClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
        services.AddScoped<OnVoyage.Factory.Application.Features.Batches.IBatchStore, BatchStore>();
        services.AddScoped<OnVoyage.Factory.Application.Features.Admin.IAdminStore, AdminStore>();
        services.AddScoped<OnVoyage.Factory.Application.Features.Bootstrap.IBootstrapRunStore, BootstrapRunStore>();
        services.AddScoped<IWikipediaTextClient, WikipediaTextClient>();
        services.AddSingleton<IContentSettingsProvider>(new ConfiguredContentSettings(configuration.GetSection("Factory:Content").Get<ContentSettings>() ?? new ContentSettings()));
        services.AddSingleton<IAudioProcessor, FfmpegAudioProcessor>();
        services.AddSingleton<IMediaStorage, LocalMediaStorage>();
        services.AddSingleton<OnVoyage.Factory.Application.Features.Packs.IPackPublisher, OnVoyage.Factory.Infrastructure.Packs.FilePackPublisher>();
        services.AddSingleton<OnVoyage.Factory.Application.Features.Packs.IMapExtractor, OnVoyage.Factory.Infrastructure.Packs.PmtilesCliMapExtractor>();
        services.AddSingleton<LlmUsageRecorder>();
        services.AddSingleton<LlmRunner>();
        services.AddScoped<IFactExtractor, LlmFactExtractor>();
        services.AddScoped<IStoryWriter, LlmStoryWriter>();
        services.AddScoped<IStoryVerifier, LlmStoryVerifier>();
        services.AddScoped<IPlaceModelClassifier, LlmPlaceClassifier>();

        services.AddSingleton<OnVoyage.Factory.Application.Features.Snapshot.ISnapshotSource, OnVoyage.Factory.Infrastructure.Snapshot.FileSnapshotSource>();
        services.AddScoped<OnVoyage.Factory.Application.Features.Bootstrap.IUsageReader, UsageReader>();

        var provider = configuration["Factory:Llm:Provider"] ?? "openai";
        var speech = configuration["Factory:Tts:Provider"] ?? "auto";
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
            services.AddSingleton<IChatClient>(provider => client.GetChatClient(configuration["Factory:Llm:WriterModel"]!).AsIChatClient());
            speech = speech == "auto" ? "openai" : speech;
        }
        else if (provider is "disabled" or "offline")
        {
            // Without a model, places no rule covers wait for a person (NeedsReview) instead of failing the scoring job.
            services.Replace(ServiceDescriptor.Scoped<IPlaceModelClassifier, NoModelClassifier>());
            services.AddSingleton<IChatClient, DisabledChatClient>();
            if (provider == "offline")
            {
                // Deterministic adapters behind the same ports: the whole chain runs with no key and no network.
                services.Replace(ServiceDescriptor.Scoped<IFactExtractor, Offline.OfflineFactExtractor>());
                services.Replace(ServiceDescriptor.Scoped<IStoryWriter, Offline.OfflineStoryWriter>());
                services.Replace(ServiceDescriptor.Scoped<IStoryVerifier, Offline.OfflineStoryVerifier>());
                services.Replace(ServiceDescriptor.Scoped<IWikipediaTextClient, Offline.OfflineWikipediaTextClient>());
            }

            speech = speech == "auto" ? "espeak" : speech;
        }
        else
        {
            throw new InvalidOperationException($"Unknown Factory:Llm:Provider '{provider}' (openai, offline or disabled).");
        }

        switch (speech)
        {
            case "openai":
                if (provider != "openai")
                {
                    throw new InvalidOperationException("Factory:Tts:Provider=openai needs Factory:Llm:Provider=openai (the key and the client).");
                }

                services.AddSingleton<ITextToSpeechProvider, OpenAiSpeechProvider>();
                break;
            case "espeak":
                // Development voice; when the binary is missing, IsAvailable is false and stories are published as text (audio pending).
                services.AddSingleton<ITextToSpeechProvider, EspeakSpeechProvider>();
                break;
            case "none":
                services.AddSingleton<ITextToSpeechProvider, DisabledSpeechProvider>();
                break;
            default:
                throw new InvalidOperationException($"Unknown Factory:Tts:Provider '{speech}' (auto, openai, espeak or none).");
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
        public bool IsAvailable => false;

        public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Speech is disabled (Factory:Tts:Provider=none or Factory:Llm:Provider=disabled).");
    }

    /// <summary>Sum of the estimated costs (<c>factory.llm_call</c>) since a date: what a cost cap is checked against.</summary>
    private sealed class UsageReader(FactoryDbContext db, IConfiguration configuration) : OnVoyage.Factory.Application.Features.Bootstrap.IUsageReader
    {
        public async Task<double> CostSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            await db.LlmCalls.AsNoTracking().Where(call => call.CreatedAt >= since).SumAsync(call => (double?)call.CostUsd, cancellationToken) ?? 0d;

        public string? PriceProblem()
        {
            if ((configuration["Factory:Llm:Provider"] ?? "openai") != "openai")
            {
                return null; // offline and disabled providers cost nothing
            }

            var missing = new List<string>();
            foreach (var role in new[] { "ExtractorModel", "WriterModel", "VerifierModel" })
            {
                var model = configuration[$"Factory:Llm:{role}"];
                if (string.IsNullOrWhiteSpace(model))
                {
                    continue; // startup already refuses a missing model
                }

                if ((configuration.GetValue<double?>($"Factory:Llm:Pricing:{model}:InputPerMillion") ?? 0d) <= 0d && (configuration.GetValue<double?>($"Factory:Llm:Pricing:{model}:OutputPerMillion") ?? 0d) <= 0d)
                {
                    missing.Add($"Factory:Llm:Pricing:{model}:InputPerMillion|OutputPerMillion");
                }
            }

            var ttsModel = configuration["Factory:Tts:Model"] ?? "gpt-4o-mini-tts";
            if ((configuration["Factory:Tts:Provider"] ?? "auto") is "auto" or "openai" && (configuration.GetValue<double?>($"Factory:Llm:Pricing:{ttsModel}:PerMillionCharacters") ?? 0d) <= 0d)
            {
                missing.Add($"Factory:Llm:Pricing:{ttsModel}:PerMillionCharacters");
            }

            return missing.Count == 0
                ? null
                : $"A cost cap needs prices, otherwise every call costs zero. Set: {string.Join(", ", missing.Distinct())} (USD), or pass AllowUnpriced to run without a real cap.";
        }
    }

    /// <summary>Registers the migration task. Call it after <c>UseWolverine</c>; it runs before the host accepts requests.</summary>
    public static IServiceCollection AddFactoryInitializer(this IServiceCollection services) => services.AddHostedService<FactoryInitializer>();

    /// <summary>
    /// Imports the committed snapshot of every destination listed in <c>Factory:Snapshot:Destinations</c> (default: all configured ones that have a snapshot)
    /// once the host is running, when <c>Factory:Snapshot:ImportOnStart</c> is true. Idempotent, so restarting the worker is harmless.
    /// </summary>
    public static IServiceCollection AddFactorySnapshotOnStart(this IServiceCollection services) => services.AddHostedService<SnapshotOnStart>();

    private sealed class SnapshotOnStart(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<SnapshotOnStart> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            if (!configuration.GetValue("Factory:Snapshot:ImportOnStart", false))
            {
                return;
            }

            var started = new TaskCompletionSource();
            await using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);

            await using var scope = services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IDestinationCatalog>();
            var slugs = configuration.GetSection("Factory:Snapshot:Destinations").Get<string[]>() is { Length: > 0 } listed
                ? listed
                : [.. (await catalog.ListAsync(stoppingToken)).Select(destination => destination.Slug)];
            var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
            foreach (var slug in slugs)
            {
                try
                {
                    var result = await bus.InvokeAsync<OnVoyage.Factory.Application.Result<OnVoyage.Factory.Application.Features.Snapshot.SnapshotImportSummary>>(
                        new OnVoyage.Factory.Application.Features.Snapshot.ImportSnapshotCommand(slug), stoppingToken);
                    if (!result.IsSuccess)
                    {
                        logger.LogInformation("Snapshot import for {Destination} skipped: {Reason}", slug, result.Error!.Message);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Snapshot import for {Destination} failed.", slug);
                }
            }
        }
    }

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
