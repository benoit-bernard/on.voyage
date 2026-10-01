using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace OnVoyage.ServiceDefaults.Security;

/// <summary>
/// What an admin write asked for, in one line for the audit journal (SEC-10): the route values and the request body, each cut short.
/// Framework arguments (bus, token, HTTP context, user) are left out. It describes the request, not the previous state.
/// </summary>
public static class AdminActionSummary
{
    private const int MaxValueLength = 200;
    private const int MaxLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string? Of(EndpointFilterInvocationContext context)
    {
        var parameters = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<MethodInfo>()?.GetParameters();
        var builder = new StringBuilder();
        for (var index = 0; index < context.Arguments.Count; index++)
        {
            var argument = context.Arguments[index];
            if (argument is null || IsFramework(argument.GetType()))
            {
                continue;
            }

            var name = parameters is not null && index < parameters.Length ? parameters[index].Name : $"arg{index}";
            var value = argument is string text ? text : JsonSerializer.Serialize(argument, argument.GetType(), Json);
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(name).Append('=').Append(value.Length > MaxValueLength ? value[..MaxValueLength] + "…" : value);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        return builder.Length > MaxLength ? builder.ToString(0, MaxLength) : builder.ToString();
    }

    private static bool IsFramework(Type type) =>
        type == typeof(CancellationToken) || type.Namespace is { } ns && (ns.StartsWith("Wolverine", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal) || ns.StartsWith("System.Security", StringComparison.Ordinal));
}
