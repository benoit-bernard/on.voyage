using OnVoyage.Creators.Application;

namespace OnVoyage.Creators.Api.Endpoints;

/// <summary>Turns a use-case result into the HTTP answer: Problem Details (RFC 9457) with a stable <c>type</c> built from the error code.</summary>
internal static class Responses
{
    public static async Task<IResult> Translate<T>(Task<Result<T>> pending, Func<T, IResult>? success = null)
    {
        var result = await pending;
        if (result.IsSuccess)
        {
            return success is null ? Results.Ok(result.Value) : success(result.Value!);
        }

        var error = result.Error!;
        var status = error.Code switch
        {
            _ when error.Code.EndsWith("not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
            "terms_required" or "specialty_required" => StatusCodes.Status422UnprocessableEntity,
            "handle_taken" or "content_exists" or "account_in_use" or "case_closed" or "not_published" or "content_removed" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Suggestion is not null)
        {
            extensions["suggestion"] = error.Suggestion;
        }

        return Results.Problem(title: error.Message, statusCode: status, type: $"https://on.voyage/problems/{error.Code}", extensions: extensions);
    }
}
