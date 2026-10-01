using FluentValidation;
using JasperFx.CodeGeneration.Model;
using OnVoyage.Catalog.Api.Endpoints;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Infrastructure;
using OnVoyage.ServiceDefaults;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetNearbyPoisQuery>, GetNearbyPoisValidator>();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(GetNearbyPoisQuery).Assembly);

    // The reader depends on a factory-registered DbContext, which Wolverine can only reach by service location.
    // To be replaced by WolverineFx.EntityFrameworkCore integration with the persistent outbox (T-006).
    options.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
});

var app = builder.Build();

await app.Services.InitializeCatalogAsync();

app.UseExceptionHandler();
app.MapCatalogEndpoints();
app.MapDefaultEndpoints();

app.Run();

public partial class Program;
