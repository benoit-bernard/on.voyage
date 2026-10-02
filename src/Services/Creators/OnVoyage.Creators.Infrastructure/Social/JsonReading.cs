using System.Text.Json;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>Reads what a platform answered without assuming a field is there or has the type the documentation says.</summary>
internal static class JsonReading
{
    public static string? Str(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static JsonElement Obj(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
