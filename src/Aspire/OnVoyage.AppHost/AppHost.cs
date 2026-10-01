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

var gateway = builder.AddProject<Projects.OnVoyage_Gateway>("gateway")
    .WithReference(catalog)
    .WithReference(platform)
    .WaitFor(catalog)
    .WaitFor(platform)
    .WithEnvironment("Auth__JwtSecret", jwtSecret)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// The PWA reads the gateway address from its own configuration (wwwroot/appsettings.json can't be injected by Aspire),
// so the development gateway port is fixed in launchSettings and mirrored there.
builder.AddProject<Projects.OnVoyage_Web_Pwa>("web-pwa")
    .WithReference(gateway)
    .WaitFor(gateway)
    .WithExternalHttpEndpoints();

builder.Build().Run();
