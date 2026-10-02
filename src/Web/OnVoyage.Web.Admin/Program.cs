using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using OnVoyage.ServiceDefaults;
using OnVoyage.Web.Admin;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Auth;
using OnVoyage.Web.Admin.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton(TimeProvider.System);

// The cookie only carries an opaque session id; the Platform tokens stay on the server (AdminTokenStore).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ov_admin";
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
            var id = context.Principal?.FindFirst(AdminClaims.SessionId)?.Value;
            if (id is null || context.HttpContext.RequestServices.GetRequiredService<AdminTokenStore>().Find(id) is null)
            {
                context.RejectPrincipal();
                return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AdminClaims.PolicyName, policy => policy.RequireAuthenticatedUser().RequireClaim(AdminClaims.Roles, AdminClaims.AdminRole));

var gateway = builder.Configuration["Gateway:BaseUrl"] ?? throw new InvalidOperationException("Gateway:BaseUrl is missing.");
builder.Services.AddHttpClient(HttpAdminApi.ClientName, client =>
{
    client.BaseAddress = new Uri(gateway.EndsWith('/') ? gateway : gateway + "/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OnVoyage-Admin/1.0");
});
builder.Services.AddSingleton<AdminTokenStore>();
builder.Services.AddSingleton<PlatformAuthClient>();
builder.Services.AddScoped<AdminSession>();
builder.Services.AddScoped<IAdminApi, HttpAdminApi>();
builder.Services.AddScoped<ICreatorsAdminApi, HttpCreatorsAdminApi>();
builder.Services.AddSingleton(new AdminOptions(builder.Configuration["Admin:MediaBaseUrl"] is { Length: > 0 } media ? media : gateway));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<AdminGate>(); // before authorization: an account without the role must get a plain 403, not a redirect
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/admin"));
app.MapAuthenticationEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

app.Run();

namespace OnVoyage.Web.Admin
{
    /// <summary>Marker for tests (<c>WebApplicationFactory</c>).</summary>
    public sealed partial class AdminAppMarker;

    public sealed record AdminOptions(string MediaBaseUrl);
}
