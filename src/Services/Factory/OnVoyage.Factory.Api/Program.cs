using OnVoyage.Factory.Api.Endpoints;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Contracts;
using OnVoyage.Factory.Infrastructure;
using OnVoyage.Messaging;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Security;
using Wolverine;
using Wolverine.Postgresql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddFactoryInfrastructure(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString(DependencyInjection.ConnectionName)!;
builder.Host.UseWolverine(options =>
{
    options.Discovery.IncludeAssembly(typeof(ImportPlacesCommand).Assembly);

    // The API only publishes: the worker consumes the "factory" queue, so long jobs never run inside a request.
    options.AddOnVoyageMessaging(connectionString, "factory", listen: false, configureFailures: MessagingRoutes.RetryProviderFailures(builder.Configuration));
    options.RouteFactoryJobs();
    options.RouteFactoryEvents();
});
builder.Services.AddFactoryInitializer();

var app = builder.Build();

app.UseExceptionHandler();
app.UseOnVoyageAuthentication();
app.MapFactoryEndpoints();
app.MapDefaultEndpoints();

app.Run();
