using Microsoft.AspNetCore.HttpOverrides;
using OnVoyage.Gateway;
using OnVoyage.Gateway.Edge;
using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// SEC-02: the Gateway validates the JWT, and so does every service behind it.
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

// SEC-04 and F-24: abuse protection driven by Platform's security.* settings.
builder.Services.AddSingleton<EdgeSettingsStore>();
builder.Services.AddEdgeRateLimiting();
builder.Services.AddHttpClient(EdgeConfigRefresher.ClientName, client =>
    client.BaseAddress = new Uri(builder.Configuration["Gateway:PlatformBaseAddress"] ?? "http://platform-api"));
builder.Services.AddHostedService<EdgeConfigRefresher>();

// Behind a reverse proxy (Caddy/Traefik) the client address is in X-Forwarded-For. Trusted only when explicitly enabled, otherwise
// anyone could pick their own rate-limit bucket by sending the header.
var trustForwardedHeaders = builder.Configuration.GetValue("Gateway:TrustForwardedHeaders", false);
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.ForwardLimit = 1;
    });
}

// The PWA is served from another origin in development; origins are configuration, never hard-coded.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().WithMethods("GET", "POST", "OPTIONS");
    }
}));

var app = builder.Build();

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseUserAgentBlocking();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapReverseProxy();
app.MapDefaultEndpoints();

app.Run();
