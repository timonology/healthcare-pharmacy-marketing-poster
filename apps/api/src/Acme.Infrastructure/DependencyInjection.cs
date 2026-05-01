using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Templates;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Persistence;
using Acme.Infrastructure.Security;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MongoOptions>(configuration.GetSection(MongoOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AzureBlobOptions>(configuration.GetSection(AzureBlobOptions.SectionName));

        services.AddSingleton<MongoContext>();

        services.AddScoped<IUserRepository, MongoUserRepository>();
        services.AddScoped<IRefreshTokenRepository, MongoRefreshTokenRepository>();
        services.AddScoped<IBrandKitRepository, MongoBrandKitRepository>();
        services.AddScoped<ITemplateRepository, MongoTemplateRepository>();
        services.AddScoped<IPosterRepository, MongoPosterRepository>();

        services.AddScoped<ITemplateSeeder, TemplateSeeder>();

        // Startup tasks run once at boot, in the order they're registered.
        services.AddScoped<IStartupTask, MongoIndexInitializer>();

        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();

        services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
        services.AddSingleton<IBrandAssetService, AzureBrandAssetService>();

        return services;
    }
}
