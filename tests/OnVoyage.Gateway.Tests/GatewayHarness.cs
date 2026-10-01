using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OnVoyage.Gateway;
using OnVoyage.Gateway.Edge;
using OnVoyage.ServiceDefaults;
using OnVoyage.TestInfrastructure;

namespace OnVoyage.Gateway.Tests;

/// <summary>A downstream stand-in for Catalog and Platform: echoes what it received and serves the edge configuration.</summary>
internal sealed class DownstreamStub : IAsyncDisposable
{
    private readonly WebApplication _app;

    private DownstreamStub(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public ConcurrentQueue<string> AuthorizationSeen { get; } = new();

    public ConcurrentQueue<string> EdgeCallTokens { get; } = new();

    public string SecurityJson { get; set; } = "{}";

    public bool EdgeConfigDown { get; set; }

    public static async Task<DownstreamStub> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddServiceDefaults();
        var app = builder.Build();
        DownstreamStub? self = null;

        app.MapGet("/api/platform/v1/config", (HttpContext http) =>
        {
            if (self!.EdgeConfigDown)
            {
                return Results.StatusCode(503);
            }

            self.EdgeCallTokens.Enqueue(http.Request.Headers.Authorization.ToString());
            return Results.Text("{\"revision\":\"x\",\"config\":{\"security\":" + self.SecurityJson + "},\"flags\":{}}", "application/json");
        });
        app.Map("/api/{**rest}", (HttpContext http) =>
        {
            self!.AuthorizationSeen.Enqueue(http.Request.Headers.Authorization.ToString());
            return Results.Json(new { ok = true });
        });
        app.MapGet("/health", () => "ok");

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new DownstreamStub(app, address);
        return self;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>Records every activity tag and log line in the process so tests can prove a position never appears in them.</summary>
internal sealed class TelemetryCapture : ILoggerProvider, IDisposable
{
    private readonly ActivityListener _listener;

    public ConcurrentQueue<string> Observed { get; } = new();

    public TelemetryCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                Observed.Enqueue($"activity:{activity.DisplayName}");
                foreach (var tag in activity.TagObjects)
                {
                    Observed.Enqueue($"tag:{tag.Key}={tag.Value}");
                }

                foreach (var activityEvent in activity.Events)
                {
                    foreach (var tag in activityEvent.Tags)
                    {
                        Observed.Enqueue($"event:{tag.Key}={tag.Value}");
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose() => _listener.Dispose();

    private sealed class CapturingLogger(TelemetryCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Observed.Enqueue($"log:{category}:{formatter(state, exception)}");
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    owner.Observed.Enqueue($"logvalue:{category}:{pair.Key}={pair.Value}");
                }
            }
        }
    }
}

internal sealed class GatewayHarness : IAsyncDisposable
{
    private readonly WebApplicationFactory<GatewayMarker> _factory;

    private GatewayHarness(DownstreamStub stub, TelemetryCapture telemetry, WebApplicationFactory<GatewayMarker> factory)
    {
        Stub = stub;
        Telemetry = telemetry;
        _factory = factory;
        Client = factory.CreateClient();
    }

    public DownstreamStub Stub { get; }

    public TelemetryCapture Telemetry { get; }

    public HttpClient Client { get; }

    public EdgeSettingsStore Settings => _factory.Services.GetRequiredService<EdgeSettingsStore>();

    public static async Task<GatewayHarness> StartAsync(string? securityJson = null, bool edgeConfigDown = false, int refreshSeconds = 1)
    {
        var stub = await DownstreamStub.StartAsync();
        if (securityJson is not null)
        {
            stub.SecurityJson = securityJson;
        }

        stub.EdgeConfigDown = edgeConfigDown;
        var telemetry = new TelemetryCapture();
        var factory = new WebApplicationFactory<GatewayMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Gateway:PlatformBaseAddress", stub.Address);
            builder.UseSetting("Gateway:EdgeConfigRefreshSeconds", refreshSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("ReverseProxy:Clusters:catalog:Destinations:primary:Address", stub.Address);
            builder.UseSetting("ReverseProxy:Clusters:platform:Destinations:primary:Address", stub.Address);
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(telemetry);
            });
        });

        return new GatewayHarness(stub, telemetry, factory);
    }

    public async Task<bool> WaitForSettingsAsync(Func<EdgeSettings, bool> predicate, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate(Settings.Current))
            {
                return true;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return false;
    }

    public Task<HttpResponseMessage> GetAsync(string url, string? token = null, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static string ProblemType(string body) => JsonDocument.Parse(body).RootElement.GetProperty("type").GetString()!;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        Telemetry.Dispose();
        await Stub.DisposeAsync();
    }
}
