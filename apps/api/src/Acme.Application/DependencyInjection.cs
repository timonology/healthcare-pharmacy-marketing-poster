using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Posters;
using Acme.Application.Templates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        services.AddScoped<AuthService>();
        services.AddScoped<BrandKitService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<PosterService>();
        return services;
    }
}
