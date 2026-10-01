using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using OnVoyage.Web.Admin.Api;

namespace OnVoyage.Web.Admin.Auth;

internal static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        // Sign-in is two plain form posts (e-mail, then the six-digit code) so it works before any interactive circuit exists.
        app.MapPost("/login/code", async (HttpContext http, [FromForm] string email, PlatformAuthClient platform) =>
        {
            try
            {
                await platform.RequestCodeAsync(email.Trim(), http.RequestAborted);
                return Results.Redirect($"/login?step=code&email={Uri.EscapeDataString(email.Trim())}");
            }
            catch (AdminApiException exception)
            {
                return Results.Redirect($"/login?error={Uri.EscapeDataString(exception.Title)}");
            }
        });

        app.MapPost("/login/verify", async (HttpContext http, [FromForm] string email, [FromForm] string code, [FromForm] string? returnUrl, PlatformAuthClient platform, AdminTokenStore store) =>
        {
            try
            {
                var session = await platform.VerifyAsync(email.Trim(), code.Trim(), http.RequestAborted);
                var id = store.Add(session);
                var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, "sub", AdminClaims.Roles);
                identity.AddClaim(new Claim("sub", session.TravelerId.ToString()));
                identity.AddClaim(new Claim(AdminClaims.SessionId, id));
                if (session.Email is not null)
                {
                    identity.AddClaim(new Claim("email", session.Email));
                }

                foreach (var role in session.Roles)
                {
                    identity.AddClaim(new Claim(AdminClaims.Roles, role));
                }

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Redirect(IsLocal(returnUrl) ? returnUrl! : "/admin");
            }
            catch (AdminApiException exception)
            {
                return Results.Redirect($"/login?step=code&email={Uri.EscapeDataString(email.Trim())}&error={Uri.EscapeDataString(exception.Title)}");
            }
        });

        app.MapPost("/logout", async (HttpContext http, IFormCollection form, AdminTokenStore store, PlatformAuthClient platform) =>
        {
            _ = form;
            if (http.User.FindFirstValue(AdminClaims.SessionId) is { } id)
            {
                if (store.Find(id) is { } session)
                {
                    await platform.SignOutAsync(session.RefreshToken, http.RequestAborted);
                }

                store.Remove(id);
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        return app;
    }

    private static bool IsLocal(string? url) => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
