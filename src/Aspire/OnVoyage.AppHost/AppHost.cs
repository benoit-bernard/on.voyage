var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddProject<Projects.OnVoyage_Catalog_Api>("catalog-api")
    .WithHttpHealthCheck("/health");

var gateway = builder.AddProject<Projects.OnVoyage_Gateway>("gateway")
    .WithReference(catalog)
    .WaitFor(catalog)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// The PWA reads the gateway address from its own configuration (wwwroot/appsettings.json can't be injected by Aspire),
// so the development gateway port is fixed in launchSettings and mirrored there.
builder.AddProject<Projects.OnVoyage_Web_Pwa>("web-pwa")
    .WithReference(gateway)
    .WaitFor(gateway)
    .WithExternalHttpEndpoints();

builder.Build().Run();
