var builder = DistributedApplication.CreateBuilder(args);

// One PostGIS database, one schema per service (cahier des charges §9.6). The Wolverine queues live here too.
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis", "16-3.4")
    .WithDataVolume("onvoyage-postgres");
var database = postgres.AddDatabase("onvoyage");

// Shared HS256 key used to sign (Platform) and validate (Gateway and services) access tokens. Generated once and persisted in the
// AppHost user secrets for local runs; supplied as a secret parameter in staging and production. Never committed.
var jwtSecret = builder.AddParameter("jwt-secret", new GenerateParameterDefault { MinLength = 64, Special = false }, secret: true, persist: true);

var platform = builder.AddProject<Projects.OnVoyage_Platform_Api>("platform-api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithHttpHealthCheck("/health");

if (builder.ExecutionContext.IsRunMode)
{
    // Local runs print the sign-in code in the Platform log instead of sending an e-mail.
    platform.WithEnvironment("Email__Provider", "log");
}
else
{
    // Staging/production: Resend. The key is a secret parameter; the sender domain must be verified in Resend.
    var resendKey = builder.AddParameter("resend-api-key", secret: true);
    platform.WithEnvironment("Email__Provider", "resend").WithEnvironment("Email__Resend__ApiKey", resendKey);
}

var catalog = builder.AddProject<Projects.OnVoyage_Catalog_Api>("catalog-api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(platform)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithHttpHealthCheck("/health");

// Discovery: interactions, interest vector, onboarding. Reads Factory's publications through its own queue; audio URLs point at the gateway-served media.
var discovery = builder.AddProject<Projects.OnVoyage_Discovery_Api>("discovery-api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(platform)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithHttpHealthCheck("/health");

// Insights: usage events (with the statistics consent), daily KPIs. Receives ConsentChangedV1 and ConfigChangedV1 from Platform on its own queue.
var insights = builder.AddProject<Projects.OnVoyage_Insights_Api>("insights-api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(platform)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithHttpHealthCheck("/health");

// Creators: founding creators, contents referenced by URL, place associations, follows, moderation. Keeps a name-only copy of the catalog's places
// (PoiProjectionChangedV1) and answers Platform's deletion and export requests; CreatorTermsAcceptedV1 makes Platform grant the creator role.
var creators = builder.AddProject<Projects.OnVoyage_Creators_Api>("creators-api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(platform)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithHttpHealthCheck("/health");

// Factory: the worker runs the pipeline jobs (osm2pgsql, Wikimedia, later LLM and TTS); the API is the back-office entry point.
var factoryWorker = builder.AddProject<Projects.OnVoyage_Factory_Worker>("factory-worker")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

var factoryApi = builder.AddProject<Projects.OnVoyage_Factory_Api>("factory-api")
    .WithReference(database)
    .WaitFor(factoryWorker)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithHttpHealthCheck("/health");

// Audio produced by the worker is served by Catalog under /media: both read the same directory (local volume in MVP-0, object storage later).
var mediaDirectory = Path.Combine(Path.GetTempPath(), "onvoyage-media");
catalog.WithEnvironment("Media__RootPath", mediaDirectory);
factoryWorker.WithEnvironment("Factory__MediaDirectory", mediaDirectory);

// Parts of the data exports (T-507): every service writes its part here, Platform assembles the archive.
var exportsDirectory = Path.Combine(Path.GetTempPath(), "onvoyage-exports");
foreach (var service in new IResourceBuilder<ProjectResource>[] { platform, discovery, insights, creators, factoryWorker })
{
    service.WithEnvironment("Exports__Directory", exportsDirectory);
}

if (builder.ExecutionContext.IsRunMode)
{
    // Local runs call no external service: deterministic offline adapters (and the espeak-ng voice when installed), and the committed
    // Marseille snapshot (data-pipeline/marseille/) is imported through Factory, so Catalog, Discovery and Creators receive the usual events.
    // A developer who wants the real pipeline sets Factory:Llm:* and OpenAI:ApiKey in user secrets (docs/runbooks/bootstrap-marseille.md).
    factoryWorker.WithEnvironment("Factory__Llm__Provider", "offline");
    factoryApi.WithEnvironment("Factory__Llm__Provider", "offline"); // the API registers the same clients at start-up and would otherwise demand an OpenAI key
    factoryWorker.WithEnvironment("Factory__Snapshot__ImportOnStart", "true");
    factoryWorker.WithEnvironment("Factory__Snapshot__Directory", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "..", "data-pipeline")));
    catalog.WithEnvironment("Catalog__SeedDemoData", "false");

    // Clients read the audio at the Gateway's public address (fixed to 5080 in its launchSettings; the PWA and the mobile app assume it).
    catalog.WithEnvironment("Media__PublicBaseUrl", "http://localhost:5080/media");
    discovery.WithEnvironment("Media__PublicBaseUrl", "http://localhost:5080/media");
}
else
{
    // Both Factory hosts register the model clients at start-up and refuse to run without them.
    var openAiKey = builder.AddParameter("openai-api-key", secret: true);
    factoryWorker.WithEnvironment("OpenAI__ApiKey", openAiKey);
    factoryApi.WithEnvironment("OpenAI__ApiKey", openAiKey);
    foreach (var role in new[] { "Extractor", "Writer", "Verifier", "Classifier" })
    {
        var model = builder.AddParameter($"llm-{role.ToLowerInvariant()}-model");
        factoryWorker.WithEnvironment($"Factory__Llm__{role}Model", model);
        factoryApi.WithEnvironment($"Factory__Llm__{role}Model", model);
    }
}

var gateway = builder.AddProject<Projects.OnVoyage_Gateway>("gateway")
    .WithReference(catalog)
    .WithReference(platform)
    .WithReference(factoryApi)
    .WithReference(discovery)
    .WithReference(insights)
    .WithReference(creators)
    .WaitFor(catalog)
    .WaitFor(platform)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

if (!builder.ExecutionContext.IsRunMode)
{
    // Published deployments put Caddy/Traefik in front of the Gateway: take the client address from X-Forwarded-For (rate limiting).
    gateway.WithEnvironment("Gateway__TrustForwardedHeaders", "true");
}

// Public SEO site (F-24): server-rendered, reads the Catalog directly with an internal token; no audio, no Premium text, no third party.
builder.AddProject<Projects.OnVoyage_Web_Public>("web-public")
    .WithReference(catalog)
    .WaitFor(catalog)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithEnvironment("Catalog__BaseAddress", "https+http://catalog-api")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// The PWA reads the gateway address from its own configuration (wwwroot/appsettings.json can't be injected by Aspire),
// so the development gateway port is fixed in launchSettings and mirrored there.
builder.AddProject<Projects.OnVoyage_Web_Pwa>("web-pwa")
    .WithReference(gateway)
    .WaitFor(gateway)
    .WithExternalHttpEndpoints();

// Back-office (Blazor, interactive server). It only talks to the Gateway, with the editor's own token. The media base URL is the address
// the editor's browser uses to play audio, which is the Gateway's external endpoint, not the internal service name.
builder.AddProject<Projects.OnVoyage_Web_Admin>("web-admin")
    .WithReference(gateway)
    .WaitFor(gateway)
    .WithEnvironment("Gateway__BaseUrl", gateway.GetEndpoint("http"))
    .WithEnvironment("Admin__MediaBaseUrl", gateway.GetEndpoint("http"))
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();
