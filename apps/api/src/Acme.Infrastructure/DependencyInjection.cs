using Acme.Application.Ai;
using Acme.Application.Admin;
using Acme.Application.Auth;
using Acme.Application.Billing;
using Acme.Application.BrandKit;
using Acme.Application.Displays;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Application.Export;
using Acme.Application.Messaging;
using Acme.Application.Invitations;
using Acme.Application.Platform;
using Acme.Application.Posters;
using Acme.Application.Social;
using Acme.Application.Hierarchy;
using Acme.Application.Sonar;
using Acme.Application.Templates;
using Acme.Application.Videos;
using Acme.Infrastructure.Billing;
using Acme.Infrastructure.Export;
using Acme.Infrastructure.Messaging;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Persistence;
using Acme.Infrastructure.Security;
using Acme.Infrastructure.Social;
using Acme.Infrastructure.Sonar;
using Acme.Infrastructure.Storage;
using Acme.Infrastructure.Templates;
using Acme.Infrastructure.VideoPipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
        // Mailjet keys stored under another common name (MAILJET_API_KEY, …) are
        // used when the canonical setting is empty, and reported at startup.
        // See MailjetSettingAliases.
        services.PostConfigure<MessagingOptions>(o =>
        {
            var used = MailjetSettingAliases.Apply(o, configuration);
            if (used.Count > 0)
                Console.Error.WriteLine(
                    "WARNING: Mailjet credentials read from " +
                    string.Join(", ", used.Select(MailjetSettingAliases.EnvName)) +
                    ". Rename to Messaging__Mailjet__ApiKey / Messaging__Mailjet__SecretKey.");
        });
        services.AddHostedService<EmailConfigurationReport>();
        // GET /api/health/ready — every dependency, checked from inside.
        services.AddScoped<Readiness.ReadinessService>();
        // No request logging: the checks carry provider secrets (see HttpClientName).
        services.AddHttpClient(Readiness.ReadinessService.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers();
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.SectionName));
        services.Configure<SuperAdminOptions>(configuration.GetSection(SuperAdminOptions.SectionName));
        services.Configure<SmokeAccountOptions>(configuration.GetSection(SmokeAccountOptions.SectionName));
        services.Configure<AiServiceOptions>(configuration.GetSection(AiServiceOptions.SectionName));

        services.AddSingleton<MongoContext>();

        services.AddScoped<IUserRepository, MongoUserRepository>();
        services.AddScoped<IRefreshTokenRepository, MongoRefreshTokenRepository>();
        services.AddScoped<IBrandKitRepository, MongoBrandKitRepository>();
        services.AddScoped<IAiGenerationRepository, MongoAiGenerationRepository>();
        services.AddScoped<ITemplateRepository, MongoTemplateRepository>();
        services.AddScoped<IPosterRepository, MongoPosterRepository>();
        services.AddScoped<ICampaignRepository, MongoCampaignRepository>();
        services.AddScoped<IPlatformAnalyticsRepository, MongoPlatformAnalyticsRepository>();
        services.AddScoped<IPatientRepository, MongoPatientRepository>();
        services.AddScoped<IPatientGroupRepository, MongoPatientGroupRepository>();
        services.AddScoped<IVideoRepository, MongoVideoRepository>();
        services.AddScoped<IProcessedWebhookRepository, MongoProcessedWebhookRepository>();
        services.AddScoped<IAdminAuditRepository, MongoAdminAuditRepository>();
        // Singletons: both are stateless over the singleton MongoContext and
        // memory cache, and the logger runs on every audited request.
        services.AddSingleton<Acme.Application.Audit.IAuditEventStore, Audit.MongoAuditEventStore>();
        services.AddSingleton<Acme.Application.Audit.IAuditLogger, Audit.AuditLogger>();
        services.AddSingleton<IPosterImageStore, AzurePosterImageStore>();
        services.AddScoped<IPharmacyInvitationRepository, MongoPharmacyInvitationRepository>();
        services.AddScoped<IAuthTokenRepository, MongoAuthTokenRepository>();
        services.AddScoped<Acme.Application.Patients.IEmailSuppressionRepository, MongoEmailSuppressionRepository>();
        services.AddScoped<IPharmacyMembershipRepository, MongoPharmacyMembershipRepository>();
        services.AddScoped<IPharmacyAccessRequestRepository, MongoPharmacyAccessRequestRepository>();
        services.AddSingleton<IInvitationLinkBuilder, ConfiguredInvitationLinkBuilder>();
        services.AddSingleton<IAuthLinkBuilder, ConfiguredAuthLinkBuilder>();
        services.AddScoped<IPlatformSettingRepository, MongoPlatformSettingRepository>();
        services.AddSingleton<IPlatformDefaults, Platform.ConfigurationPlatformDefaults>();
        services.AddScoped<ISocialConnectionRepository, MongoSocialConnectionRepository>();
        services.AddScoped<IScheduledPostRepository, MongoScheduledPostRepository>();
        services.AddScoped<IRecurringSeriesRepository, MongoRecurringSeriesRepository>();
        services.AddScoped<ISeriesOccurrenceStore, MongoSeriesOccurrenceStore>();
        services.AddScoped<IGoogleAdsCampaignRepository, MongoGoogleAdsCampaignRepository>();

        /* ---------------- Social publishing and ads ---------------- */

        services.Configure<TokenCipherOptions>(configuration.GetSection(TokenCipherOptions.SectionName));
        services.Configure<MetaOptions>(configuration.GetSection(MetaOptions.SectionName));
        services.Configure<GoogleOptions>(configuration.GetSection(GoogleOptions.SectionName));
        services.Configure<LinkedInOptions>(configuration.GetSection(LinkedInOptions.SectionName));
        services.Configure<TikTokOptions>(configuration.GetSection(TikTokOptions.SectionName));
        services.Configure<XOptions>(configuration.GetSection(XOptions.SectionName));

        // App ids and secrets pasted into the portal often carry a trailing
        // space, a newline or the quotes they were copied with. Google answers
        // that with "The OAuth client was not found" (invalid_client), which
        // sends you to the Cloud Console instead of the setting.
        services.PostConfigure<MetaOptions>(o =>
        {
            o.AppId = SocialCredentials.Clean(o.AppId);
            o.AppSecret = SocialCredentials.Clean(o.AppSecret);
        });
        services.PostConfigure<GoogleOptions>(o =>
        {
            o.ClientId = SocialCredentials.CleanGoogleClientId(o.ClientId);
            o.ClientSecret = SocialCredentials.Clean(o.ClientSecret);
            o.AdsDeveloperToken = SocialCredentials.Clean(o.AdsDeveloperToken);
        });
        services.PostConfigure<LinkedInOptions>(o =>
        {
            o.ClientId = SocialCredentials.Clean(o.ClientId);
            o.ClientSecret = SocialCredentials.Clean(o.ClientSecret);
        });
        services.PostConfigure<TikTokOptions>(o =>
        {
            o.ClientKey = SocialCredentials.Clean(o.ClientKey);
            o.ClientSecret = SocialCredentials.Clean(o.ClientSecret);
        });
        services.PostConfigure<XOptions>(o =>
        {
            o.ClientId = SocialCredentials.Clean(o.ClientId);
            o.ClientSecret = SocialCredentials.Clean(o.ClientSecret);
        });
        services.Configure<PublishWorkerOptions>(configuration.GetSection(PublishWorkerOptions.SectionName));

        // Singleton: the cipher validates its keys once, at construction, so a
        // misconfiguration fails at startup rather than on the first pharmacy
        // to connect an account.
        services.AddSingleton<ITokenCipher, TokenCipher>();

        services.AddMemoryCache();
        services.AddSingleton<ILoginThrottle, Security.MemoryLoginThrottle>();
        services.AddScoped<ISessionGuard, Security.CachedSessionGuard>();
        // In Mongo, not memory: Start, the callback and the account choice
        // may each reach a different instance. Singleton over the singleton
        // MongoContext and cipher.
        services.AddSingleton<IOAuthSessionStore, MongoOAuthSessionStore>();

        services.AddScoped<IPublishMediaResolver, PublishMediaResolver>();

        // RemoveAllLoggers on every platform client. IHttpClientFactory's
        // default logging writes each request URL at Information, and on these
        // clients the URL carries the app secret, OAuth codes and access
        // tokens (Meta's token exchange is a GET with them in the query). A
        // log store is not where those belong. Failures are still logged — by
        // the code that knows what the call was for, without the URL.
        services.AddHttpClient(SocialHttpClients.Meta, client =>
        {
            // Generous per request: Meta fetches the media from our URL while
            // the publish call is open, and a large video is not quick.
            client.Timeout = TimeSpan.FromMinutes(3);
        }).RemoveAllLoggers();

        services.AddHttpClient(SocialHttpClients.MetaOAuth, client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers();

        services.AddHttpClient(SocialHttpClients.Google, client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers();

        // Campaign creation uploads images inline; a minute is generous for
        // that and still short enough to report "no answer" while the pharmacy
        // is waiting.
        services.AddHttpClient(SocialHttpClients.GoogleAds, client => client.Timeout = TimeSpan.FromSeconds(60))
            .RemoveAllLoggers();

        // The whole video goes up in one request. Four minutes is generous for
        // a short clip, and deliberately inside the publish worker's default
        // five-minute lease: an upload that outlived the lease would be marked
        // Uncertain by another instance while this one was still sending, and
        // its answer would then be thrown away.
        services.AddHttpClient(SocialHttpClients.YouTube, client => client.Timeout = TimeSpan.FromMinutes(4))
            .RemoveAllLoggers();

        services.AddHttpClient(SocialHttpClients.LinkedIn, client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers();

        // Image and video parts are sent in the request body. Inside the
        // publish budget (LinkedInPublisherBase.Budget), like YouTube's.
        services.AddHttpClient(SocialHttpClients.LinkedInUpload, client => client.Timeout = TimeSpan.FromMinutes(3))
            .RemoveAllLoggers();

        services.AddHttpClient(SocialHttpClients.TikTok, client => client.Timeout = TimeSpan.FromSeconds(30))
            .RemoveAllLoggers();

        // The video goes up in the request body. Inside TikTokPublisher.Budget.
        services.AddHttpClient(SocialHttpClients.TikTokUpload, client => client.Timeout = TimeSpan.FromMinutes(3))
            .RemoveAllLoggers();

        // Upload segments are 4 MB each, so a minute per call is plenty.
        services.AddHttpClient(SocialHttpClients.X, client => client.Timeout = TimeSpan.FromSeconds(60))
            .RemoveAllLoggers();

        // Reading our own video before it goes to X. Inside XPublisher.Budget.
        services.AddHttpClient(SocialHttpClients.XUpload, client => client.Timeout = TimeSpan.FromMinutes(2))
            .RemoveAllLoggers();

        services.AddSingleton<IMetaSignedRequestReader, MetaSignedRequestReader>();
        services.AddScoped<ISocialOAuthProvider, MetaOAuthProvider>();
        services.AddScoped<ISocialOAuthProvider, GoogleOAuthProvider>();
        services.AddScoped<ISocialOAuthProvider, YouTubeOAuthProvider>();
        services.AddScoped<ISocialOAuthProvider, LinkedInOAuthProvider>();
        services.AddScoped<ISocialOAuthProvider, TikTokOAuthProvider>();
        services.AddScoped<ISocialOAuthProvider, XOAuthProvider>();
        services.AddScoped<ITikTokCreatorInfoClient, TikTokCreatorInfoClient>();

        // Registered as a collection: the publisher for a platform is selected
        // by its Platform property, so adding one is a registration rather
        // than an edit to a switch statement somewhere.
        services.AddScoped<ISocialPublisher, FacebookPagePublisher>();
        services.AddScoped<ISocialPublisher, InstagramPublisher>();
        services.AddScoped<ISocialPublisher, GoogleBusinessProfilePublisher>();
        services.AddScoped<ISocialPublisher, YouTubePublisher>();
        services.AddScoped<ISocialPublisher, LinkedInProfilePublisher>();
        services.AddScoped<ISocialPublisher, LinkedInPagePublisher>();
        services.AddScoped<ISocialPublisher, TikTokPublisher>();
        services.AddScoped<ISocialPublisher, XPublisher>();

        // The real client when the Google OAuth client is configured; otherwise the
        // placeholder, which says plainly why nothing can be connected.
        services.AddScoped<IGoogleAdsClient>(sp =>
            sp.GetRequiredService<IOptions<GoogleOptions>>().Value.IsAdsConfigured
                ? ActivatorUtilities.CreateInstance<GoogleAdsRestClient>(sp)
                : new NotConfiguredGoogleAdsClient());
        services.AddScoped<IAdsCreativeBuilder, AdsCreativeBuilder>();

        // Says at startup whether Google Ads can work and, if not, which setting is missing.
        services.AddHostedService<SocialConfigurationReport>();
        services.AddHostedService<PublishWorker>();
        // Queues recurring occurrences for the publish worker; never publishes itself.
        services.AddHostedService<SeriesWorker>();

        services.AddScoped<ISocialConnectionProbe, FacebookPageProbe>();
        services.AddScoped<ISocialConnectionProbe, InstagramProbe>();
        services.AddScoped<ISocialConnectionProbe, YouTubeProbe>();
        services.AddScoped<ISocialConnectionProbe, LinkedInProfileProbe>();
        services.AddScoped<ISocialConnectionProbe, LinkedInPageProbe>();
        services.AddScoped<ISocialConnectionProbe, TikTokProbe>();
        services.AddScoped<ISocialConnectionProbe, XProbe>();
        services.AddHostedService<ConnectionHealthWorker>();

        // Sends queued campaigns off the request thread. See the class comment:
        // this is what makes a thousand-recipient campaign possible, and what
        // recovers one whose process died halfway.
        services.AddHostedService<CampaignDispatchWorker>();

        services.AddScoped<ITemplateSeeder, TemplateSeeder>();

        services.AddScoped<IStartupTask, MongoIndexInitializer>();
        // After the indexes; copies staff history into audit_events once.
        services.AddScoped<IStartupTask, Audit.LegacyAuditBackfill>();
        // Must run before anything queries by role/tier/isActive — see the
        // class docs for why absent fields broke those filters.
        services.AddScoped<IStartupTask, UserFieldBackfill>();
        // Registered after the index initializer so the unique email index
        // exists before the seeder tries to insert.
        services.AddScoped<IStartupTask, SuperAdminSeeder>();
        // After SuperAdminSeeder so the bootstrap account always wins the first
        // email. Development-only and idempotent — see the class docs.
        services.AddScoped<IStartupTask, DevUserSeeder>();
        // The account `pnpm smoke` signs in as. Before MembershipBackfill, so it
        // gets its self-membership on the same boot and can use the product
        // immediately rather than on the next restart.
        services.AddScoped<IStartupTask, SmokeAccountSeeder>();
        // After the seeders, so demo and smoke accounts get memberships on a
        // fresh database in the same boot rather than on the next one.
        services.AddScoped<IStartupTask, MembershipBackfill>();

        // Replaces the invented contact details baked into the built-in
        // templates. The seeder cannot do this: it only inserts templates that
        // are missing, so an already-seeded database keeps the old text.
        services.AddScoped<IStartupTask, TemplatePlaceholderMigration>();

        // Moves each pharmacy's booking link from its profile onto its brand
        // kit, creating a kit where there was none. After the index initializer
        // above, because it inserts into brand_kits and that collection's unique
        // owner index is what stops a concurrent boot creating two.
        services.AddScoped<IStartupTask, BookingLinkToBrandKitMigration>();

        // Unsets worker-owned fields that were stored as explicit nulls. Until
        // this runs, the render worker's first usage write fails on any video
        // created before [BsonIgnoreIfNull] — including on its failure path, so
        // the video cannot even record that it failed and stays Rendering
        // for ever. See the class docs.
        services.AddScoped<IStartupTask, NullUsageFieldRepair>();

        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();

        // Singleton: Stripe.net's service clients are thread-safe and the
        // SDK's API key is process-wide anyway.
        services.AddSingleton<IStripeGateway, StripeGateway>();

        // In-pharmacy display screens. Repositories only — the services that
        // use them are application concerns and are registered there.
        services.AddScoped<IDisplayScreenRepository, MongoDisplayScreenRepository>();
        services.AddScoped<IPlaylistRepository, MongoPlaylistRepository>();

        services.AddSingleton<IBlobStorageService, AzureBlobStorageService>();
        services.AddSingleton<IBrandAssetService, AzureBrandAssetService>();

        services.AddSingleton<QuestPdfExporter>();
        services.AddSingleton<IPosterPdfExporter>(sp => sp.GetRequiredService<QuestPdfExporter>());
        // Same instance behind both interfaces: the image cache is per-object,
        // and two instances would download every poster photo twice.
        services.AddSingleton<ICanvasRasterizer>(sp => sp.GetRequiredService<QuestPdfExporter>());

        // Deliberately its own object rather than a third face of the exporter:
        // it holds no cache, touches no canvas, and only re-encodes bytes it is
        // handed.
        services.AddSingleton<IImageTranscoder, SkiaImageTranscoder>();
        services.AddSingleton<IQrCodeGenerator, Acme.Infrastructure.BrandKit.QrCoderGenerator>();

        // --- Video pipeline ---
        // The .NET side owns the Video entity (tier gating, ownership,
        // monthly caps, blob delete cleanup, read-URL signing). The actual
        // render pipeline lives in the Python ai-service (apps/ai-service),
        // which polls the same Mongo `videos` collection and mutates rows
        // Pending → Rendering → Ready.
        services.AddScoped<IVideoStorage, AzureBlobVideoStorage>();

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
            })
            // Do not hand this client a connection that has been sitting idle.
            //
            // Campaign sends were failing with `Broken pipe` partway through the
            // upload. It looked like a size limit — big requests failed, small
            // ones did not — and a request-size budget was added on that basis.
            // Then a send of 1,008,893 bytes failed where one of 1,008,621 had
            // succeeded minutes earlier. Two hundred and seventy-two bytes
            // apart, both outcomes: not a size limit.
            //
            // The timings say what it actually is. A successful 1 MB send takes
            // about two and a half seconds; a failing one takes twenty to
            // twenty-six. It is not uploading slowly, it is stalling and then
            // dying — which is what writing to a keep-alive connection the far
            // end has already closed looks like.
            //
            // .NET keeps a pooled connection for up to a minute by default and
            // has no way to know the server has dropped it. A two-kilobyte email
            // fits in one socket write and completes before the reset arrives,
            // so small sends always worked. A megabyte takes many writes over
            // several seconds, which is all the time the reset needs. Hence
            // "large requests fail", which is a real pattern with the wrong
            // cause attached.
            //
            // Fifteen seconds is comfortably inside any reasonable server-side
            // idle close. The cost of discarding a still-good connection is one
            // TCP and TLS handshake on a path that sends a campaign at a time.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
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

        services.AddScoped<ITemplateAssetService, TemplateAssetService>();
        services.AddScoped<IPharmacyPhotoStore, BlobPharmacyPhotoStore>();

        services.AddHttpClient<ITemplateGenerator, AiTemplateGenerator>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiServiceOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
                client.BaseAddress = new Uri(opts.BaseUrl);
            // Generation is a long synchronous LLM call with up to two repair
            // rounds — the 100s default would abort a request that was going
            // to succeed.
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });

        // Which voices the video dialog may offer. Same short-timeout reasoning
        // as the classifier: somebody is waiting for a dialog to open.
        services.AddHttpClient<INarrationCatalog, AiNarrationCatalog>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiServiceOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
                client.BaseAddress = new Uri(opts.BaseUrl);
        });

        services.AddHttpClient<IRefineClassifier, AiRefineClassifier>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiServiceOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.BaseUrl))
                client.BaseAddress = new Uri(opts.BaseUrl);
            // Deliberately not opts.TimeoutSeconds. That is sized for template
            // generation, which is minutes of model work nobody is watching.
            // This one has a pharmacist waiting on a spinner, and the classifier
            // enforces its own short ceiling — see AiRefineClassifier.Timeout.
        });

        return services;
    }
}
