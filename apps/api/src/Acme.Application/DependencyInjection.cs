using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Campaigns;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Profile;
using Acme.Application.Subscriptions;
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
        services.AddScoped<SubscriptionService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<PatientService>();
        services.AddScoped<PatientGroupService>();
        return services;
    }
}
