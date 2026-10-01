using OnVoyage.ServiceDefaults;
using OnVoyage.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
// SEC-02: the Gateway validates the JWT, and so does every service behind it.
builder.Services.AddOnVoyageAuthentication(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

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

app.UseCors();
app.UseOnVoyageAuthentication();
app.MapReverseProxy();
app.MapDefaultEndpoints();

app.Run();
