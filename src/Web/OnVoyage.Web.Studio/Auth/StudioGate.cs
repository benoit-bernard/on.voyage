using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace OnVoyage.Web.Studio.Auth;

/// <summary>
/// Every <c>/studio/*</c> route answers 403 to someone who is signed in without the <c>creator</c> role, and the attempt is logged. The two
/// doors that exist before the role does stay open to any signed-in account: the home (which sends to the sign-up) and the sign-up itself.
/// Someone who is not signed in is sent to the sign-in page.
/// </summary>
public sealed class StudioGate(RequestDelegate next, ILogger<StudioGate> logger)
{
    private static readonly string[] OpenToAccounts = ["/studio", "/studio/join"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/studio", StringComparison.OrdinalIgnoreCase))
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var open = OpenToAccounts.Any(path => string.Equals(context.Request.Path.Value?.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase));
            if (!open && !context.User.IsCreator())
            {
                logger.LogWarning("Studio access denied for {Subject} on {Path}.", context.User.FindFirstValue("sub") ?? "unknown", context.Request.Path.Value);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("403 - this area is reserved to ON.VOYAGE creators.");
                return;
            }
        }

        await next(context);
    }
}
