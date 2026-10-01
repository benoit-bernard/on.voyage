using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace OnVoyage.Web.Admin.Auth;

/// <summary>
/// Every <c>/admin/*</c> route answers 403 to someone who is signed in without the admin role, and the attempt is logged (F-25).
/// Someone who is not signed in is sent to the sign-in page.
/// </summary>
public sealed class AdminGate(RequestDelegate next, ILogger<AdminGate> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            if (!context.User.IsAdmin())
            {
                logger.LogWarning("Admin access denied for {Subject} on {Path}.", context.User.FindFirstValue("sub") ?? "unknown", context.Request.Path.Value);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("403 - this area is reserved to ON.VOYAGE administrators.");
                return;
            }
        }

        await next(context);
    }
}
