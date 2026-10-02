using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Background;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Profile;

namespace OnVoyage.UI.Components.Tests;

public sealed class BackgroundWiringTests
{
    private static ServiceProvider Build(bool withBattery)
    {
        var services = new ServiceCollection();
        services.AddAppCore();
        services.AddBackgroundLocation();
        services.AddSingleton(Substitute.For<IPlatformLocationUpdates>());
        services.AddSingleton(Substitute.For<ILocationPermissions>());
        services.AddSingleton(Substitute.For<IBackgroundSession>());
        services.AddSingleton(Substitute.For<IAudioPlayer>());
        services.AddSingleton(Substitute.For<ICatalogClient>());
        services.AddSingleton(Substitute.For<IProfileStore>());
        services.AddSingleton(Substitute.For<ISessionStore>());
        if (withBattery)
        {
            services.AddSingleton(Substitute.For<IBatteryOptimization>());
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_background_source_is_the_location_source_the_discovery_mode_uses_and_its_status(bool withBattery)
    {
        using var provider = Build(withBattery);

        var source = provider.GetRequiredService<ILocationSource>();

        source.ShouldBeOfType<BackgroundLocationSource>();
        provider.GetRequiredService<IBackgroundStatus>().ShouldBeSameAs(source);
        provider.GetRequiredService<IBackgroundRationale>().ShouldBeSameAs(provider.GetRequiredService<BackgroundRationaleBroker>());
    }
}
