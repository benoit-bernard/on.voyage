using FluentValidation;
using OnVoyage.Catalog.Api.Endpoints;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Configuration;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetNearbyPoisQuery>, GetNearbyPoisValidator>();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(GetNearbyPoisQuery).Assembly);
    options.AddOnVoyageMessaging(connectionString, "catalog");
});

var app = builder.Build();

await app.Services.InitializeCatalogAsync();

app.UseExceptionHandler();
app.UseMinAppVersionGate();
app.MapCatalogEndpoints();
app.MapDefaultEndpoints();

app.Run();

