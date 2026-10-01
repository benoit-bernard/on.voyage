using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Infrastructure;
using OnVoyage.UI.Components;
using OnVoyage.Web.Pwa;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<PwaApp>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The Gateway is the only backend the client talks to. Empty value = same origin (Gateway serves the PWA in production).
var gateway = builder.Configuration["Gateway:BaseAddress"];
var gatewayAddress = string.IsNullOrWhiteSpace(gateway) ? new Uri(builder.HostEnvironment.BaseAddress) : new Uri(gateway);

builder.Services.AddAppCore();
// Tile file and fonts of the map; without Map:TilesUrl the map page says it is not configured.
builder.Services.AddSingleton(builder.Configuration.GetSection("Map").Get<OnVoyage.App.Core.Map.MapSettings>() ?? new OnVoyage.App.Core.Map.MapSettings());
builder.Services.AddAppInfrastructure(gatewayAddress, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
builder.Services.AddScoped<ISessionStore, BrowserSessionStore>();
builder.Services.AddScoped<OnVoyage.App.Core.Profile.IProfileStore, BrowserProfileStore>();
builder.Services.AddSingleton<OnVoyage.App.Core.Wishes.IReminderStore, BrowserReminderStore>();
builder.Services.AddSingleton<OnVoyage.UI.Components.Device.BrowserLocation>();
builder.Services.AddSingleton<ILocationProvider>(provider => provider.GetRequiredService<OnVoyage.UI.Components.Device.BrowserLocation>());
builder.Services.AddSingleton<OnVoyage.App.Core.Discovery.ILocationSource>(provider => provider.GetRequiredService<OnVoyage.UI.Components.Device.BrowserLocation>());
builder.Services.AddSingleton<OnVoyage.App.Core.Discovery.IScreenKeepAwake, OnVoyage.UI.Components.Device.BrowserKeepAwake>();
builder.Services.AddSingleton<OnVoyage.App.Core.Audio.IAudioPlayer, OnVoyage.UI.Components.Audio.BrowserAudioPlayer>();

await builder.Build().RunAsync();
