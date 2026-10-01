using FluentValidation;
using OnVoyage.Catalog.Api.Endpoints;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Configuration;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using Wolverine.ErrorHandling;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddSingleton<IValidator<GetNearbyPoisQuery>, GetNearbyPoisValidator>();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(GetNearbyPoisQuery).Assembly);
    options.AddOnVoyageMessaging(connectionString, "catalog", configureFailures: failures =>
        // Queues are not ordered against each other: a story may arrive before its place.
        failures.OnException<OnVoyage.Catalog.Application.Ports.PoiNotProjectedException>()
            .RetryWithCooldown(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)));
});

var app = builder.Build();

await app.Services.InitializeCatalogAsync();

app.UseExceptionHandler();
// Story audio produced by Factory (local storage in MVP-0; the public base URL moves to a CDN later).
if (builder.Configuration["Media:RootPath"] is { Length: > 0 } mediaRoot && Directory.Exists(mediaRoot))
{
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(mediaRoot)), RequestPath = "/media" });
}

app.UseOnVoyageAuthentication();
app.UseMinAppVersionGate();
app.MapCatalogEndpoints();
app.MapDefaultEndpoints();

app.Run();

