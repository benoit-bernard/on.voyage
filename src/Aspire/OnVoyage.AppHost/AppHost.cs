var builder = DistributedApplication.CreateBuilder(args);

// One PostGIS database, one schema per service (cahier des charges §9.6). The Wolverine queues live here too.
var postgres = builder.AddPostgres("postgres")
    .WithImage("postgis/postgis", "16-3.4")
    .WithDataVolume("onvoyage-postgres");
var database = postgres.AddDatabase("onvoyage");

var platform = builder.AddProject<Projects.OnVoyage_Platform_Api>("platform-api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

var catalog = builder.AddProject<Projects.OnVoyage_Catalog_Api>("catalog-api")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(platform)
    .WithEnvironment("Catalog__SeedDemoData", "true")
    .WithHttpHealthCheck("/health");

var gateway = builder.AddProject<Projects.OnVoyage_Gateway>("gateway")
    .WithReference(catalog)
    .WithReference(platform)
    .WaitFor(catalog)
    .WaitFor(platform)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// The PWA reads the gateway address from its own configuration (wwwroot/appsettings.json can't be injected by Aspire),
// so the development gateway port is fixed in launchSettings and mirrored there.
builder.AddProject<Projects.OnVoyage_Web_Pwa>("web-pwa")
    .WithReference(gateway)
    .WaitFor(gateway)
    .WithExternalHttpEndpoints();

builder.Build().Run();
