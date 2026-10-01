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

if (builder.ExecutionContext.IsRunMode)
{
    catalog.WithEnvironment("Catalog__SeedDemoData", "true");
}

// Discovery: interactions, interest vector, onboarding. Reads Factory's publications through its own queue; audio URLs point at the gateway-served media.
var discovery = builder.AddProject<Projects.OnVoyage_Discovery_Api>("discovery-api")
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

if (builder.ExecutionContext.IsRunMode)
{
    // Local runs do not call a language model unless the developer sets Factory:Llm:* and OpenAI:ApiKey in user secrets.
    factoryWorker.WithEnvironment("Factory__Llm__Provider", "disabled");
}
else
{
    var openAiKey = builder.AddParameter("openai-api-key", secret: true);
    factoryWorker.WithEnvironment("OpenAI__ApiKey", openAiKey);
    foreach (var role in new[] { "Extractor", "Writer", "Verifier", "Classifier" })
    {
        factoryWorker.WithEnvironment($"Factory__Llm__{role}Model", builder.AddParameter($"llm-{role.ToLowerInvariant()}-model"));
    }
}

var gateway = builder.AddProject<Projects.OnVoyage_Gateway>("gateway")
    .WithReference(catalog)
    .WithReference(platform)
    .WithReference(factoryApi)
    .WithReference(discovery)
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
