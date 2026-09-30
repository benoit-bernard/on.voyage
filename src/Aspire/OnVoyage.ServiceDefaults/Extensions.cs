using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace OnVoyage.ServiceDefaults;

public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Privacy (copilot-instructions): positions travel in the query string and must never reach traces.
                    options.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.Ordinal)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.Ordinal);
                    options.EnrichWithHttpRequest = static (activity, request) =>
                    {
                        activity.SetTag("url.query", null);
                        activity.SetTag("url.full", $"{request.Scheme}://{request.Host}{request.Path}");
                    };
                })
                .AddHttpClientInstrumentation(options =>
                    options.EnrichWithHttpRequestMessage = static (activity, request) =>
                    {
                        activity.SetTag("url.query", null);
                        activity.SetTag("url.full", request.RequestUri is { } uri ? uri.GetLeftPart(UriPartial.Path) : null);
                    }));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(HealthEndpointPath);
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
        return app;
    }
}
