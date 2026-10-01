using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Home;

namespace OnVoyage.App.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddAppCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionService>();
        services.AddSingleton<ISessionProvider>(provider => provider.GetRequiredService<SessionService>());
        services.AddScoped<HomeFeedService>();
        return services;
    }
}
