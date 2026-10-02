using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnVoyage.Web.Studio.Api;

/// <summary>An error the API explained with a Problem Details body; the page shows <see cref="Title"/> to the creator as it is.</summary>
public sealed class StudioApiException(string title, int status) : Exception(title)
{
    public string Title { get; } = title;

    public int Status { get; } = status;
}

internal static class StudioJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
