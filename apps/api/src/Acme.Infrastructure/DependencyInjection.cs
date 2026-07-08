using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Application.Export;
using Acme.Application.Messaging;
using Acme.Application.Posters;
using Acme.Application.Sonar;
using Acme.Application.Templates;
using Acme.Infrastructure.Export;
using Acme.Infrastructure.Messaging;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Persistence;
using Acme.Infrastructure.Security;
using Acme.Infrastructure.Sonar;
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
        services.Configure<SonarOptions>(configuration.GetSection(SonarOptions.SectionName));
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));

        services.AddSingleton<MongoContext>();

        services.AddScoped<IUserRepository, MongoUserRepository>();
        services.AddScoped<IRefreshTokenRepository, MongoRefreshTokenRepository>();
        services.AddScoped<IBrandKitRepository, MongoBrandKitRepository>();
        services.AddScoped<ITemplateRepository, MongoTemplateRepository>();
        services.AddScoped<IPosterRepository, MongoPosterRepository>();
        services.AddScoped<ICampaignRepository, MongoCampaignRepository>();
        services.AddScoped<IPatientRepository, MongoPatientRepository>();
        services.AddScoped<IPatientGroupRepository, MongoPatientGroupRepository>();

        services.AddScoped<ITemplateSeeder, TemplateSeeder>();

        services.AddScoped<IStartupTask, MongoIndexInitializer>();

        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();

        services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
        services.AddSingleton<IBrandAssetService, AzureBrandAssetService>();

        services.AddSingleton<IPosterPdfExporter, QuestPdfExporter>();

        // Register email sender based on the configured provider. Default is
        // SMTP for back-compat; "mailjet" swaps in the HTTP-based sender.
        var messagingProvider = configuration
            .GetSection(MessagingOptions.SectionName)["Provider"]
            ?? "smtp";
        if (string.Equals(messagingProvider, "mailjet", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IEmailSender, MailjetEmailSender>((sp, client) =>
            {
                var mj = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagingOptions>>()
                          .Value.Mailjet;
                if (!string.IsNullOrWhiteSpace(mj.BaseUrl))
                    client.BaseAddress = new Uri(mj.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }

        services.AddSingleton<ISmsSender, MockSmsSender>();

        services.AddHttpClient<ISonarApiClient, SonarApiClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SonarOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
                client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(opts.ApiKey))
                client.DefaultRequestHeaders.Add("X-Api-Key", opts.ApiKey);
        });

        return services;
    }
}
