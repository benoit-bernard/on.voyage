using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Infrastructure;
using OnVoyage.App.LocalData;

namespace OnVoyage.UI.Components.Tests;

/// <summary>
/// Mirrors the registrations of <c>MauiProgram</c> with the same <c>Add*</c> extensions (the platform implementations replaced by fakes) and checks
/// that every service the Razor components inject can be resolved, so a missing registration fails here instead of at first launch on a phone.
/// </summary>
public sealed class MobileWiringTests
{
    // Provided by the Blazor host itself (BlazorWebView, WebAssembly), not by the application.
    private static readonly HashSet<Type> HostProvided = [typeof(NavigationManager), typeof(Microsoft.JSInterop.IJSRuntime), typeof(IServiceProvider)];

    private static ServiceProvider BuildMobileProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppCore();
        services.AddAppInfrastructure(new Uri("http://10.0.2.2:5080/"), "0.1.0");
        services.AddSingleton(Substitute.For<ISessionStore>());
        services.AddSingleton(Substitute.For<IProfileStore>());
        services.AddSingleton(Substitute.For<ILocationProvider>());
        services.AddSingleton(Substitute.For<ILocationSource>());
        services.AddSingleton(Substitute.For<IAudioPlayer>());
        services.AddLocalData(Path.Combine(Path.GetTempPath(), $"onvoyage-wiring-{Guid.NewGuid():N}.db"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public static TheoryData<string> InjectedTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in InjectedServiceTypes())
        {
            data.Add(type.AssemblyQualifiedName!);
        }

        return data;
    }

    private static IEnumerable<Type> InjectedServiceTypes() =>
        typeof(OnVoyage.UI.Components.Pages.Home).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(p => p.GetCustomAttribute<InjectAttribute>() is not null)
            .Select(p => p.PropertyType)
            .Where(t => !HostProvided.Contains(t))
            .Distinct();

    [Fact]
    public void Components_inject_the_expected_number_of_services() =>
        Assert.True(InjectedServiceTypes().Count() >= 10);

    [Theory]
    [MemberData(nameof(InjectedTypes))]
    public void Every_service_injected_by_a_component_is_registered(string typeName)
    {
        var type = Type.GetType(typeName, throwOnError: true)!;
        using var provider = BuildMobileProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(type));
    }
}
