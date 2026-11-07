using Acme.Application.Ai;
using Acme.Application.Auth;
using System.Text;
using System.Text.Json.Serialization;
using Acme.Application;
using Acme.Application.Common;
using Acme.Application.Templates;
using Acme.Infrastructure;
using Acme.Infrastructure.Options;
using Acme.WebApi;
using Acme.WebApi.Auditing;
using Acme.WebApi.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

// In development, take the JWT settings from the repo-root .env — the same file
// ai-service and docker-compose read. Added last so it outranks user secrets,
// which is the whole point: a stale `dotnet user-secrets set Jwt:Secret`
// otherwise wins silently and ai-service rejects every token. See DevEnvFile.
string? devEnvFile = null;
if (builder.Environment.IsDevelopment())
{
    devEnvFile = DevEnvFile.AddJwtSettings(
        builder.Configuration, builder.Environment.ContentRootPath);
}

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddControllers(opts =>
    {
        // Fills an audited create's entity id from the response body, so
        // "poster.create" records which poster without every action saying so.
        opts.Filters.Add<AuditResultFilter>();
    })
    .AddJsonOptions(opts =>
    {
        // Serialize enums as their string names ("Free", "Email", "Draft", ...)
        // both on the wire (response) and during model binding (request).
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:3000", "http://localhost:3030" };

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// Development on a hosted App Service loads appsettings.Development.json —
// the development Meta, Google and Mailjet credentials and the dev seeders
// (security reviews SEC-03, R2-01). Fine for a shared dev site, never for
// production, and it has happened before (PRODUCTION_CHECKLIST.md §1). Not a
// refusal, because a shared dev App Service is legitimate; a loud line in the
// log stream, where whoever checks a deploy will see it.
if (builder.Environment.IsDevelopment()
    && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
{
    Console.Error.WriteLine(
        $"WARNING: '{Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")}' is running with "
        + "ASPNETCORE_ENVIRONMENT=Development. Development credentials and seeders are active. "
        + "If this is production, set ASPNETCORE_ENVIRONMENT=Production now.");
}

var jwt = builder.Configuration
    .GetSection(JwtOptions.SectionName)
    .Get<JwtOptions>()
    ?? throw new InvalidOperationException("Missing Jwt configuration.");

// A published or missing signing secret is a startup failure outside
// Development, not a quiet default. See JwtSecretPolicy.
if (JwtSecretPolicy.WhyUnsafe(jwt.Secret) is { } unsafeSecret)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException($"Refusing to start: {unsafeSecret}");
    Console.Error.WriteLine($"WARNING (Development only): {unsafeSecret}");
}

// Public links embedded in campaign emails — the video downloads, the poster
// image the body shows, and the unsubscribe link — are HMAC-signed. Each
// throws on first use if this is skipped, rather than issuing tokens nothing
// can validate.
//
// Each link type signs with its own key derived from Signing:LinkSecret, so
// the JWT secret can be rotated without breaking links already in inboxes
// (security review R2-13). Until LinkSecret is set, the JWT secret is the
// master; links signed before this change keep verifying until they expire.
var linkSecret = builder.Configuration["Signing:LinkSecret"];
if (string.IsNullOrWhiteSpace(linkSecret))
{
    linkSecret = jwt.Secret;
}
else if (JwtSecretPolicy.WhyUnsafe(linkSecret) is { } unsafeLinkSecret && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"Refusing to start: Signing:LinkSecret — {unsafeLinkSecret.Replace("Jwt:Secret", "the value").Replace("Jwt__Secret", "Signing__LinkSecret")}");
}
Acme.Application.Videos.VideoDownloadToken.Configure(linkSecret, legacySecret: jwt.Secret);
Acme.Application.Posters.PosterImageToken.Configure(linkSecret, legacySecret: jwt.Secret);
Acme.Application.Patients.UnsubscribeToken.Configure(linkSecret);
// Same secret, same reason: ai-service verifies this with the key it
// already shares with us. See AiQuotaAssertion — without it a pharmacy
// could call the public ai-service directly and never meet its allowance.
AiQuotaAssertion.Configure(jwt.Secret);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // A valid signature is not enough: the account must still be allowed
        // in, and must not have ended its sessions since this token was issued
        // (security review R2-09). A delegated token is checked for both the
        // pharmacy it acts as and the person acting.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var guard = context.HttpContext.RequestServices.GetRequiredService<ISessionGuard>();
                var issuedAt = context.SecurityToken.ValidFrom;
                var principal = context.Principal;
                var ct = context.HttpContext.RequestAborted;

                var subject = principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                              ?? principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                var actor = principal?.FindFirst(DelegationClaims.Actor)?.Value;
                var delegated = !string.IsNullOrEmpty(actor);

                // Ordinary token: the subject signed in. Delegated token: the
                // actor signed in and the subject is the pharmacy worked as,
                // which may be a branch with no login of its own.
                var why = await guard.WhyEndedAsync(
                    subject ?? "", issuedAt,
                    delegated ? SessionParty.WorkedAs : SessionParty.SignedIn, ct);

                if (why is null && delegated)
                    why = await guard.WhyEndedAsync(actor!, issuedAt, SessionParty.SignedIn, ct);

                if (why is not null) context.Fail($"Session no longer valid: {why}.");
            },
        };
    });

builder.Services.AddAppRateLimits(builder.Configuration);

builder.Services.AddAuthorization(options =>
{
    // Named policies rather than bare [Authorize(Roles = "...")] strings so
    // the role names exist in exactly one place. A typo in a policy name is a
    // startup failure; a typo in a role string silently denies everyone.
    options.AddPolicy(AuthPolicies.SuperAdminOnly, p =>
        p.RequireRole(nameof(Acme.Domain.Users.UserRole.SuperAdmin)));

    options.AddPolicy(AuthPolicies.AdminArea, p =>
        p.RequireRole(
            nameof(Acme.Domain.Users.UserRole.Admin),
            nameof(Acme.Domain.Users.UserRole.SuperAdmin)));
});

// The caller's address — for the audit record and, since SEC-05, for the
// per-address rate limits, so it has to be one the caller cannot choose
// (security review R2-03).
//
// With Edge:ClientIpKey set (the normal case): only the last X-Forwarded-For
// entry is trusted — the one the platform's front end appended, which is the
// address that actually connected. For a browser request that is the web app,
// and SignedClientIpMiddleware then swaps in the browser's address the web app
// signed. A direct caller gets its own real address, whatever it sends.
//
// Without the key (an environment not yet configured), the old two-hop rule
// is kept so the web app's users are not all one address — and a warning says
// that the address can then be spoofed by calling the API directly.
var clientIpKey = builder.Configuration["Edge:ClientIpKey"];
var signedClientIp = !string.IsNullOrWhiteSpace(clientIpKey);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.ForwardLimit = signedClientIp ? 1 : builder.Configuration.GetValue("Audit:ForwardLimit", 2);
    // KnownIPNetworks is .NET 10's name; KnownNetworks is obsolete (ASPDEPR005).
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
if (!signedClientIp && !builder.Environment.IsDevelopment())
{
    Console.Error.WriteLine(
        "WARNING: Edge:ClientIpKey is not set. Client addresses (rate limits, audit log) can be "
        + "spoofed by calling the API directly. Set Edge__ClientIpKey here and CLIENT_IP_SIGNING_KEY "
        + "on the web app to the same long random value.");
}

var app = builder.Build();

app.UseForwardedHeaders();
if (signedClientIp)
    app.UseMiddleware<SignedClientIpMiddleware>(System.Text.Encoding.UTF8.GetBytes(clientIpKey!));

// SEC-09. First, so even an error response carries the headers.
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    // HTTPS is terminated by App Service in front of us; HSTS tells browsers
    // never to try plain http for this host again.
    app.UseHsts();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
// Between the two on purpose: it needs to know who is calling, and it must
// still see the 403 that authorization returns without running the endpoint.
app.UseMiddleware<AuditMiddleware>();
app.UseAuthorization();
// After authentication, so a limit can be per person rather than per address.
app.UseRateLimiter();

app.MapControllers();
// `build` is the deploy's SHA tag (BUILD_ID, set by scripts/deploy.sh), or
// null for a local run. The smoke test compares it with the web app's and the
// ai-service's: a partial deploy — one service restarted, another not — looks
// healthy everywhere and fails only where the two meet.
var buildId = Environment.GetEnvironmentVariable("BUILD_ID");
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTime.UtcNow,
    build = string.IsNullOrWhiteSpace(buildId) ? null : buildId,
}));

try
{
    using var scope = app.Services.CreateScope();
    var sp = scope.ServiceProvider;

    foreach (var task in sp.GetServices<IStartupTask>())
        await task.ExecuteAsync(CancellationToken.None);

    await sp.GetRequiredService<ITemplateSeeder>()
            .SeedIfEmptyAsync(CancellationToken.None);
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogCritical(ex,
        "Startup tasks failed. If you see a Mongo connection refused error, " +
        "make sure the database is running: `docker compose up -d mongo`");
    return 1;
}

// Fingerprint of the JWT signing config.
//
// ai-service verifies the tokens this API signs and logs the same fingerprint
// at boot. If the two lines disagree, every forwarded call fails with a 401
// that reads like a login problem and says nothing about the real cause —
// which is exactly what happens when the API runs outside docker-compose
// (appsettings.Development.json supplies the secret) while ai-service reads
// JWT_SECRET from .env. Fingerprint only; never log the secret.
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var digest = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(jwt.Secret ?? string.Empty));
    var fingerprint = Convert.ToHexString(digest)[..12].ToLowerInvariant();

    logger.LogInformation(
        "JWT config: secret_fingerprint={Fingerprint} issuer={Issuer} audience={Audience}",
        fingerprint, jwt.Issuer, jwt.Audience);

    // Name where the value came from. ASP.NET layers configuration
    // (appsettings -> appsettings.{Env} -> user secrets -> env vars), so
    // knowing which layer won is the difference between a two-second fix and
    // an afternoon.
    if (builder.Configuration is IConfigurationRoot root)
    {
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet("Jwt:Secret", out _))
            {
                logger.LogInformation("JWT secret supplied by: {Provider}", provider);
                break;
            }
        }
    }

    if (devEnvFile is not null)
    {
        logger.LogInformation(
            "JWT settings taken from {EnvFile} so they match ai-service. " +
            "Any Jwt:Secret in user-secrets is intentionally ignored in Development.",
            devEnvFile);
    }
}

app.Run();
return 0;

public partial class Program;
