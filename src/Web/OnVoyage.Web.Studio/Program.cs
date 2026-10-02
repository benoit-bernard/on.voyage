using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using OnVoyage.ServiceDefaults;
using OnVoyage.Web.Studio;
using OnVoyage.Web.Studio.Api;
using OnVoyage.Web.Studio.Auth;
using OnVoyage.Web.Studio.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton(TimeProvider.System);

// The cookie only carries an opaque session id; the Platform tokens and the roles stay on the server (StudioTokenStore).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ov_studio";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = context =>
        {
            // A restart or an expired refresh token drops the server-side session: the cookie is then worthless.
            var id = context.Principal?.FindFirst(StudioClaims.SessionId)?.Value;
            if (id is null || context.HttpContext.RequestServices.GetRequiredService<StudioTokenStore>().Find(id) is not { } session)
            {
                context.RejectPrincipal();
                return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            // The roles are Platform's, as of the last refresh: the creator role arrives after the sign-up, not at sign-in.
            if (context.Principal!.Identity is ClaimsIdentity identity)
            {
                foreach (var stale in identity.FindAll(StudioClaims.Roles).ToList())
                {
                    identity.RemoveClaim(stale);
                }

                foreach (var role in session.Roles)
                {
                    identity.AddClaim(new Claim(StudioClaims.Roles, role));
                }
            }

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(StudioClaims.PolicyName, policy => policy.RequireAuthenticatedUser().RequireClaim(StudioClaims.Roles, StudioClaims.CreatorRole));

var gateway = builder.Configuration["Gateway:BaseUrl"] ?? throw new InvalidOperationException("Gateway:BaseUrl is missing.");
builder.Services.AddHttpClient(GatewayCaller.ClientName, client =>
{
    client.BaseAddress = new Uri(gateway.EndsWith('/') ? gateway : gateway + "/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OnVoyage-Studio/1.0");
});
builder.Services.AddSingleton<StudioTokenStore>();
builder.Services.AddSingleton<PlatformAuthClient>();
builder.Services.AddScoped<StudioSession>();
builder.Services.AddScoped<IRoleRefresher>(provider => provider.GetRequiredService<StudioSession>());
builder.Services.AddScoped<IStudioApi, HttpStudioApi>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<StudioGate>(); // before authorization: an account without the role must get a plain 403, not a redirect
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/studio"));
app.MapAuthenticationEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

app.Run();

namespace OnVoyage.Web.Studio
{
    /// <summary>Marker for tests (<c>WebApplicationFactory</c>).</summary>
    public sealed partial class StudioAppMarker;
}
