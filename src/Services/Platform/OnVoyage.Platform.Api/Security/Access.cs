using System.Security.Cryptography;
using System.Text;

namespace OnVoyage.Platform.Api.Security;

/// <summary>
/// Interim access control until Supabase Auth lands (T-004): admin endpoints need <c>X-Admin-Key</c> matching <c>Auth:AdminApiKey</c>
/// (disabled when unset); traveler endpoints read <c>X-Traveler-Id</c> only when <c>Auth:AllowTravelerIdHeader</c> is true.
/// </summary>
internal static class Access
{
    public static bool IsAdmin(HttpContext context)
    {
        var expected = context.RequestServices.GetRequiredService<IConfiguration>()["Auth:AdminApiKey"];
        if (string.IsNullOrEmpty(expected) || !context.Request.Headers.TryGetValue("X-Admin-Key", out var provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(provided.ToString()));
    }

    public static Guid? Traveler(HttpContext context)
    {
        var allowed = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Auth:AllowTravelerIdHeader", false);
        return allowed && Guid.TryParse(context.Request.Headers["X-Traveler-Id"].ToString(), out var id) ? id : null;
    }
}

internal sealed class AdminOnly : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        Access.IsAdmin(context.HttpContext) ? await next(context) : Results.Problem(title: "Admin access required.", statusCode: StatusCodes.Status401Unauthorized, type: "https://on.voyage/problems/unauthorized");
}
