using FluentValidation;
using OnVoyage.Catalog.Api.Endpoints;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Infrastructure;
using OnVoyage.ServiceDefaults;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IValidator<GetNearbyPoisQuery>, GetNearbyPoisValidator>();
builder.Services.AddCatalogInfrastructure();
builder.Host.UseWolverine(options => options.Discovery.IncludeAssembly(typeof(GetNearbyPoisQuery).Assembly));

var app = builder.Build();

app.UseExceptionHandler();
app.MapCatalogEndpoints();
app.MapDefaultEndpoints();

app.Run();

public partial class Program;
