using Acme.Application.Ai;
using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Campaigns;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Profile;
using Acme.Application.Subscriptions;
using Acme.Application.Templates;
using Acme.Application.Videos;
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
        services.Configure<Posters.CanvasLimitsOptions>(configuration.GetSection(Posters.CanvasLimitsOptions.SectionName));

        services.AddScoped<AuthService>();
        services.AddScoped<AccountEmailService>();
        services.AddScoped<PatientImportService>();
        services.AddScoped<BrandKitService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<PosterService>();
        services.AddScoped<Patients.UnsubscribeService>();
        // Poster design for pharmacies: the admin generator, wrapped in the
        // monthly allowance the pricing page has always advertised.
        services.AddScoped<PosterAiService>();
        services.AddScoped<PosterPhotoService>();
        services.AddScoped<PosterImageService>();

        // Two display services on purpose. DisplayService is owner-scoped;
        // DisplayKioskService answers anonymous screens. They share no method,
        // so a missing owner check on one cannot expose the other's data.
        services.AddScoped<Displays.DisplayService>();
        services.AddScoped<Displays.DisplayKioskService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<Billing.BillingService>();
        services.AddScoped<Admin.AdminService>();
        services.AddScoped<Admin.PharmacySupportService>();
        services.AddScoped<Platform.PlatformSettingsService>();
        services.AddScoped<Invitations.InvitationService>();
        services.AddScoped<Hierarchy.PharmacyHierarchyService>();
        services.AddScoped<Hierarchy.PharmacyContextService>();
        services.AddScoped<Hierarchy.PharmacyAccessRequestService>();
        services.AddScoped<Social.SocialConnectionService>();
        services.AddScoped<Social.PublishService>();
        services.AddScoped<Social.GoogleAdsService>();
        services.AddScoped<Social.MetaCallbackService>();
        services.AddScoped<Social.ConnectionHealthService>();
        services.AddScoped<Social.RecurringSeriesService>();
        services.AddScoped<Social.TikTokCreatorService>();
        services.AddScoped<Social.ISeriesNotifier, Social.SeriesNotifier>();
        services.AddScoped<ProfileService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<PatientService>();
        services.AddScoped<PatientGroupService>();
        services.AddScoped<GlobalGroupService>();
        services.AddScoped<VideoService>();
        services.AddScoped<Analytics.AnalyticsService>();

        // One per request: services add what only they know (the entity, the
        // target pharmacy, a recipient count) to the event the request writes.
        services.AddScoped<Audit.IAuditScope, Audit.AuditScope>();
        services.AddScoped<Audit.AuditEventService>();
        return services;
    }
}
