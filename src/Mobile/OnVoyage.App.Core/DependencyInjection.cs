using Microsoft.Extensions.DependencyInjection;
using OnVoyage.App.Core.Home;

namespace OnVoyage.App.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddAppCore(this IServiceCollection services) => services.AddScoped<HomeFeedService>();
}
