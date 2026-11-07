using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Application.Messaging;
using Acme.Application.Posters;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The real API, on a throwaway database.
/// </summary>
/// <remarks>
/// <para>
/// Boots the actual host — real routing, real authorisation policies, real
/// model binding, real Mongo driver. Everything the unit tests replace with a
/// fake is the part that has actually broken, so none of it is replaced here.
/// </para>
/// <para>
/// Only the <b>database name</b> is overridden, never the connection string.
/// The first version replaced the whole connection string with a bare
/// <c>mongodb://localhost:27017</c>, which threw away the credentials in
/// <c>appsettings.json</c> — every write then failed with "requires
/// authentication", and ten tests reported "the server has not been started"
/// instead. Inheriting the app's own connection string means these tests reach
/// the same server, with the same credentials, that the app already uses.
/// </para>
/// <para>
/// Email is forced to mock. These tests invite people, and an integration suite
/// that sends real mail to <c>@example.test</c> is a suite that gets an account
/// suspended for bounces.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DatabaseName { get; } =
        $"acme_it_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Set MONGO_TEST_URL to point the suite somewhere else entirely.</summary>
    private static readonly string? UrlOverride =
        Environment.GetEnvironmentVariable("MONGO_TEST_URL");

    /// <summary>The connection string the host will actually use.</summary>
    public string MongoUrl { get; private set; } = "";

    /// <summary>Why the suite cannot run, or null. Set by the preflight.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>
    /// Every email the app tried to send.
    /// </summary>
    /// <remarks>
    /// Replaces the sender outright rather than reading log lines. The shipped
    /// mock reports success and logs a string, which is right for development
    /// and useless here: an assertion about an attachment would become an
    /// assertion about log formatting.
    ///
    /// Nothing is lost by substituting it. <c>Messaging:UseMock</c> is already
    /// true for this suite, so <c>MailjetEmailSender</c> returns before it
    /// builds a request — the real sender was never being exercised.
    ///
    /// Shared across the assembly, like the host. xUnit runs a collection
    /// serially, so a test that calls <c>Clear()</c> first sees only its own
    /// mail.
    /// </remarks>
    public CapturingEmailSender Email { get; } = new();

    /// <summary>Rendered posters, kept in memory instead of in Azure.</summary>
    /// <remarks>
    /// See <see cref="InMemoryPosterImageStore"/> for why this one dependency
    /// is substituted while the rasterizer is left real.
    /// </remarks>
    public InMemoryPosterImageStore PosterImages { get; } = new();

    /// <summary>Video files, so a signed link can be followed to real bytes.</summary>
    public InMemoryVideoStorage VideoFiles { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development so the seeder runs and gives every test a superadmin and a
        // set of pharmacies to work with, without this file duplicating the
        // seeding logic and drifting from it.
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                // The one thing that must differ from the running app.
                ["Mongo:Database"] = DatabaseName,

                // No outbound anything.
                ["Messaging:UseMock"] = "true",
                ["Sonar:UseMock"] = "true",
                ["Stripe:Enabled"] = "false",

                ["Public:WebBaseUrl"] = "http://localhost:3000",

                // GET /api/health/ready is closed without it. Only the refusals
                // are tested here: an accepted call checks real providers.
                ["Readiness:Token"] = ReadinessToken,

                // Every request in this suite comes from one address, so the
                // per-address limits on sign-up and sign-in (SEC-05) would trip
                // after a handful of tests. Raised here; the per-person limits
                // (stock search, share-email) are left as shipped and tested.
                ["RateLimits:AuthPerMinute"] = "100000",
                ["RateLimits:AccountEmailPer15Minutes"] = "100000",
                ["RateLimits:DisplayClaimPerMinute"] = "100000",

                // Set deliberately, and it has to look publicly reachable.
                //
                // A campaign links its poster only when Public:ApiBaseUrl is an
                // address a mail provider's image proxy could fetch; otherwise
                // it embeds the design instead, because a link to localhost
                // reaches nobody. Leaving this unset would fall back to
                // http://localhost:5050 and quietly move this whole suite onto
                // the development path — the delivery tests would assert on a
                // shape production never sends, and the assertion that keeps a
                // poster out of every recipient's copy would be testing nothing.
                //
                // Nothing is ever fetched from it. The tests read the URL out of
                // the built message and request its path against the in-process
                // host, which is why an address that does not resolve is fine
                // here and would not be in a deployment.
                ["Public:ApiBaseUrl"] = "https://api.integration.example",

                // The publish worker and the recurring-series worker off: tests
                // that exercise publishing drive each step themselves, with the
                // clock they choose. A background worker claiming the same posts
                // in parallel would make "exactly one claim" a race against the
                // host instead of a property of the code. Bound with
                // Configure<T>, so this override applies.
                ["Social:Worker:Enabled"] = "false",

                // NOTHING under "Jwt" belongs here, and overriding it once cost
                // an afternoon.
                //
                // Program.cs reads the JWT settings *eagerly*:
                //
                //     var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>();
                //
                // That runs before `builder.Build()`, and the overrides here are
                // applied during Build — so the token *validator* was built from
                // appsettings.json while JwtTokenService, which resolves
                // IOptions<JwtOptions> lazily from the final configuration, signed
                // with the test secret. Register returned 201, and the very next
                // request with that token failed signature validation.
                //
                // The general rule: a setting read with `.Get<T>()` or `["key"]`
                // during startup is frozen at that moment and cannot be overridden
                // from here. A setting bound with `services.Configure<T>(...)` is
                // read on resolve and can be. Mongo, Messaging, Sonar and Stripe
                // above are all the second kind, which is why they work.
                //
                // Nothing needs overriding anyway: the host signs and validates
                // with its own secret, whatever that is.
            };

            // Only when explicitly asked. Otherwise the app's own connection
            // string — credentials and all — is left alone.
            if (!string.IsNullOrWhiteSpace(UrlOverride))
                settings["Mongo:ConnectionString"] = UrlOverride;

            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);

            services.RemoveAll<IPosterImageStore>();
            services.AddSingleton<IPosterImageStore>(PosterImages);

            services.RemoveAll<Acme.Application.Videos.IVideoStorage>();
            services.AddSingleton<Acme.Application.Videos.IVideoStorage>(VideoFiles);
        });
    }

    /// <summary>
    /// Reads the API's own configuration, without starting it.
    /// </summary>
    /// <remarks>
    /// So the preflight can test the connection the host will really make. A
    /// preflight that invents its own connection string is testing something
    /// nothing else uses.
    /// </remarks>
    private static readonly Lazy<IConfiguration> AppSettings = new(() =>
    {
        // bin/Debug/net10.0 -> Acme.IntegrationTests -> tests -> apps/api
        var apiRoot = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Parent?.Parent?.Parent?.Parent?.FullName;

        var webApi = apiRoot is null ? null : Path.Combine(apiRoot, "src", "Acme.WebApi");

        var builder = new ConfigurationBuilder().AddEnvironmentVariables();
        if (webApi is not null && Directory.Exists(webApi))
        {
            builder.SetBasePath(webApi)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile("appsettings.Development.json", optional: true);
        }
        return builder.Build();
    });

    private static string ResolveConnectionString() =>
        !string.IsNullOrWhiteSpace(UrlOverride)
            ? UrlOverride
            : AppSettings.Value["Mongo:ConnectionString"] ?? "mongodb://localhost:27017";

    /// <summary>
    /// Proves the suite can do what the app does, before any test runs.
    /// </summary>
    /// <remarks>
    /// Writes and deletes a document in the throwaway database. The first
    /// version pinged the <c>admin</c> database instead — and <c>ping</c> is
    /// allowed without authentication, so it passed happily against a secured
    /// server, let every test run, and the real failure surfaced as ten copies
    /// of "the server has not been started". A preflight has to exercise the
    /// permission the app actually needs.
    /// </remarks>
    async Task IAsyncLifetime.InitializeAsync()
    {
        MongoUrl = ResolveConnectionString();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(MongoUrl);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);

            var db = new MongoClient(settings).GetDatabase(DatabaseName);
            var probe = db.GetCollection<BsonDocument>("preflight");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await probe.InsertOneAsync(
                new BsonDocument { { "_id", "preflight" } }, cancellationToken: cts.Token);
            await db.DropCollectionAsync("preflight", cts.Token);
        }
        catch (MongoException ex)
        {
            // Matched on the message rather than on exception subtypes. The
            // driver spreads these across MongoAuthenticationException,
            // MongoCommandException and MongoConnectionException depending on
            // where the refusal happens, and guessing the right one is how a
            // catch block silently stops catching after a driver upgrade.
            var m = ex.Message;

            Unavailable = m.Contains("requires authentication", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
                ? $"MongoDB at {Redact(MongoUrl)} refused the credentials. The connection " +
                  "string comes from apps/api/src/Acme.WebApi/appsettings.json unless " +
                  "MONGO_TEST_URL is set — check that it still matches your local server."
                : m.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                ? $"MongoDB at {Redact(MongoUrl)} accepted the credentials but will not let " +
                  $"them create the database '{DatabaseName}'. The suite makes a throwaway " +
                  "database per run, so the account needs rights beyond the single app " +
                  "database — grant readWriteAnyDatabase, or point MONGO_TEST_URL at a " +
                  "server where it does."
                : $"MongoDB at {Redact(MongoUrl)} would not accept a write " +
                  $"({ex.GetType().Name}: {m}).";
        }
        catch (Exception ex)
        {
            Unavailable =
                $"Could not reach MongoDB at {Redact(MongoUrl)} ({ex.GetType().Name}: {ex.Message}). " +
                "These tests exercise real queries and indexes, so there is nothing useful " +
                "to run without a writable database.";
        }
    }

    /// <summary>Connection string with any password removed, for error messages.</summary>
    private static string Redact(string connectionString) =>
        System.Text.RegularExpressions.Regex.Replace(
            connectionString, @"://[^@/]*@", "://<credentials>@");

    // Explicit interface implementation: WebApplicationFactory already exposes
    // `ValueTask DisposeAsync()` from IAsyncDisposable, and IAsyncLifetime wants
    // `Task DisposeAsync()`. Hiding one with `new` compiles and then quietly
    // leaves the other unimplemented.
    async Task IAsyncLifetime.DisposeAsync()
    {
        if (Unavailable is null && !string.IsNullOrEmpty(MongoUrl))
        {
            try { await new MongoClient(MongoUrl).DropDatabaseAsync(DatabaseName); }
            catch { /* a leftover test database is untidy, not a failure */ }
        }
        await base.DisposeAsync();
    }

    /// <summary>
    /// A client, or an explanation of why the host would not start.
    /// </summary>
    /// <remarks>
    /// The host logs its startup failure and stops, so every later call throws
    /// "The server has not been started or no web application was configured" —
    /// which names nothing. Checking the preflight result first turns ten
    /// identical unhelpful failures into one that says what to fix.
    /// </remarks>
    public HttpClient NewClient()
    {
        if (Unavailable is not null) throw new InvalidOperationException(Unavailable);

        try
        {
            return CreateClient();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "The API host did not start. The cause is logged above this failure, " +
                "usually as a `crit: Startup[0]` line — startup tasks run migrations and " +
                "index creation, so a database permission problem shows up there first.",
                ex);
        }
    }

    /* ---------------------------------------------------------------- *
     * Signing in
     * ---------------------------------------------------------------- */

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The readiness token this host is configured with.</summary>
    public const string ReadinessToken = "integration-readiness-token-0123456789";

    /// <summary>An HTTP client carrying a real token for a real account.</summary>
    public async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = NewClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password });

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not sign in as {email} ({(int)res.StatusCode}). " +
                "The dev seeder creates these accounts on boot; if it did not run, " +
                "check ASPNETCORE_ENVIRONMENT and the seeder's configuration.");

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// The SuperAdmin the host seeds on a fresh database.
    /// </summary>
    /// <remarks>
    /// Credentials read from the same <c>SuperAdmin</c> section the seeder uses,
    /// not from a hardcoded guess. The guess was <c>ChangeMe-Dev-Only-123</c>,
    /// which is a placeholder rather than the configured value — the same class
    /// of mistake as inventing a connection string, and it would have failed at
    /// the first test that needed an admin.
    ///
    /// Environment variables still win, for pointing the suite at a database
    /// seeded by something other than this configuration.
    /// </remarks>
    public Task<HttpClient> SignInSuperAdminAsync()
    {
        var email = Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL")
                    ?? AppSettings.Value["SuperAdmin:Email"];
        var password = Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD")
                       ?? AppSettings.Value["SuperAdmin:Password"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                "No SuperAdmin credentials. The host seeds one from the SuperAdmin section of " +
                "appsettings.json; set SUPERADMIN_EMAIL and SUPERADMIN_PASSWORD to override.");

        return SignInAsync(email, password);
    }

    /// <summary>
    /// Puts a pharmacy on a paid plan, as a SuperAdmin would.
    /// </summary>
    /// <remarks>
    /// A freshly registered account is on Free, and several features are gated
    /// above it — campaign creation answers
    /// <c>403 "Campaigns are not available on the Free plan"</c>. A test that
    /// needs to exercise one of those has to arrange the plan explicitly rather
    /// than assume a default, and going through the real admin endpoint keeps
    /// that arrangement honest: no reaching into the database behind the API's
    /// back.
    /// </remarks>
    /// <summary>
    /// Makes a pharmacy a head office, so it may create sub-pharmacies.
    /// </summary>
    /// <remarks>
    /// A fixture step, like <see cref="UpgradeToProAsync"/>. Running branches is
    /// granted by staff rather than assumed, so any test whose subject is a
    /// group has to arrange the grant first — the same way a test about
    /// campaigns has to arrange a paid plan.
    /// </remarks>
    public async Task GrantHeadOfficeAsync(string userId, bool granted = true)
    {
        var admin = await SignInSuperAdminAsync();

        using var res = await admin.PostAsJsonAsync(
            $"/api/admin/users/{userId}/head-office",
            new { granted, reason = "Integration test needs a group" });

        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Could not set head office on {userId} ({(int)res.StatusCode}): {detail}");
        }
    }

    public Task UpgradeToProAsync(string userId) => SetTierAsync(userId, "Pro");

    /// <summary>Puts an account on any plan.</summary>
    /// <remarks>
    /// Pro is the common case and keeps its own name, but a limit is only worth
    /// testing on the tier that has one — display screens are unlimited on Pro
    /// and capped at one on Starter, so a test about the cap cannot use Pro.
    /// </remarks>
    public async Task SetTierAsync(string userId, string tier)
    {
        var admin = await SignInSuperAdminAsync();

        using var res = await admin.PostAsJsonAsync(
            $"/api/admin/users/{userId}/tier",
            new { tier, reason = "Integration test needs a specific plan" });

        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Could not put {userId} on the {tier} plan ({(int)res.StatusCode}): {detail}");
        }
    }

    /// <summary>
    /// A fresh pharmacy, registered through the real endpoint.
    /// </summary>
    /// <remarks>
    /// Registered rather than seeded so each test owns its own data. Two tests
    /// sharing a pharmacy is how an isolation test comes to pass because both
    /// sides were the same account.
    /// </remarks>
    public async Task<(HttpClient Client, string Email, string Id)> RegisterPharmacyAsync(
        string label)
    {
        var email = $"it-{label}-{Guid.NewGuid().ToString("N")[..8]}@example.test";
        const string password = "IntegrationTest-123!";

        var client = NewClient();
        var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            displayName = $"Integration {label}",
        });

        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Could not register a test pharmacy ({(int)res.StatusCode}): {detail}");
        }

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", Json);
        var id = me.TryGetProperty("id", out var idProp) ? idProp.GetString()! : "";

        return (client, email, id);
    }

    private IMongoCollection<BsonDocument>? _videos;

    private IMongoCollection<BsonDocument> Videos =>
        _videos ??= new MongoClient(MongoUrl).GetDatabase(DatabaseName)
            .GetCollection<BsonDocument>("videos");

    /// <summary>
    /// Writes a video row the way the render worker leaves one, without a
    /// render.
    /// </summary>
    /// <remarks>
    /// The worker is a separate Python process that is not part of this host,
    /// and the domain deliberately has no "mark Ready" for the API to call —
    /// Ready is the worker's to write. So a test that needs a finished video
    /// writes the row directly, with the fields the worker writes, and puts a
    /// small file in <see cref="VideoFiles"/> under its blob key.
    /// </remarks>
    public async Task<string> SeedVideoAsync(
        string ownerId,
        string name,
        Acme.Domain.Videos.VideoStatus status,
        int durationSeconds = 20,
        bool withFile = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        if (withFile) VideoFiles.Put($"videos/{id}.mp4", SeededVideoBytes);
        await Videos.InsertOneAsync(new BsonDocument
        {
            ["_id"] = id,
            ["ownerId"] = ownerId,
            ["name"] = name,
            ["topic"] = "Seeded for a test",
            ["source"] = 0,
            ["status"] = (int)status,
            ["scriptSource"] = 0,
            ["visualStyle"] = 0,
            ["aspect"] = "9:16",
            ["duration"] = durationSeconds,
            ["blobKey"] = withFile ? (BsonValue)$"videos/{id}.mp4" : BsonNull.Value,
            ["createdAtUtc"] = now,
            ["updatedAtUtc"] = now,
        });
        return id;
    }

    /// <summary>
    /// What a seeded video's file holds: 64 known bytes, so a test can ask for
    /// a range and check exactly what came back.
    /// </summary>
    public static readonly byte[] SeededVideoBytes =
        Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

    /// <summary>Moves a seeded video to another status, as the worker would.</summary>
    public Task SetVideoStatusAsync(string videoId, Acme.Domain.Videos.VideoStatus status) =>
        Videos.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", videoId),
            Builders<BsonDocument>.Update
                .Set("status", (int)status)
                .Set("updatedAtUtc", DateTime.UtcNow));

    private IMongoCollection<BsonDocument>? _campaigns;

    /// <summary>
    /// Blocks until no campaign in this database is still queued or sending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Call this after any request that creates or retries a campaign.</b>
    /// <c>POST /api/campaigns</c> used to send inline and return once the mail
    /// was away, so <c>Api.Email.Sent</c> was populated the moment the response
    /// arrived. It now queues, and a background worker sends a moment later —
    /// so the same assertion runs before any mail exists and fails on an empty
    /// mailbox. Four tests failed that way; a fifth, the render-cache one, would
    /// instead have compared nothing to nothing and <i>passed</i>.
    /// </para>
    /// <para>
    /// Waits on the database rather than on the campaign list endpoint, and on
    /// <b>every</b> campaign rather than on one id. Both for the same reason: an
    /// unwaited campaign left behind by an earlier test carries on sending into
    /// the shared mailbox during a later one, and the later test cannot see it
    /// over the API because it belongs to a different pharmacy. Under
    /// synchronous sending that could not happen, so <c>Email.Clear()</c> was
    /// enough on its own; it no longer is.
    /// </para>
    /// <para>
    /// Also the honest place to wait. The endpoint reports what the owner's
    /// campaigns look like; what a test needs to know is whether the worker has
    /// finished, and that is a fact about the queue.
    /// </para>
    /// </remarks>
    public async Task WaitForCampaignDispatchAsync(TimeSpan? timeout = null)
    {
        if (Unavailable is not null) return;

        _campaigns ??= new MongoClient(MongoUrl)
            .GetDatabase(DatabaseName)
            .GetCollection<BsonDocument>("campaigns");

        // Queued: accepted, no worker yet. Sending: a worker holds the lease.
        // Anything else is settled and will not produce more mail.
        var inFlight = Builders<BsonDocument>.Filter.In("status", new[]
        {
            (int)Acme.Domain.Campaigns.CampaignStatus.Queued,
            (int)Acme.Domain.Campaigns.CampaignStatus.Sending,
        });

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (true)
        {
            if (await _campaigns.CountDocumentsAsync(inFlight) == 0) return;

            if (DateTime.UtcNow >= deadline)
            {
                var stuck = await _campaigns.Find(inFlight).Limit(5).ToListAsync();
                var detail = string.Join("; ", stuck.Select(d =>
                    $"{d.GetValue("_id", "?")} status={d.GetValue("status", "?")} " +
                    $"note={d.GetValue("note", BsonNull.Value)}"));

                throw new InvalidOperationException(
                    $"Campaign dispatch did not finish within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0}s. " +
                    $"Still in flight: {detail}. Either CampaignDispatchWorker is not running in the " +
                    "test host, or a send is failing without ever completing the campaign.");
            }

            // Short: the worker polls every 3s when idle but picks up a new
            // campaign immediately after the one before it, so most waits here
            // are a few tens of milliseconds.
            await Task.Delay(50);
        }
    }
}

/// <summary>
/// One host for the whole assembly.
/// </summary>
/// <remarks>
/// Booting the host per test class would multiply an already slow suite by the
/// number of classes. Tests keep to their own records instead of their own
/// database — which is also closer to what production looks like, where
/// isolation has to come from the code rather than from the schema.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>Base class for tests that need the database.</summary>
[Collection(ApiCollection.Name)]
public abstract class IntegrationTest(ApiFactory api) : IAsyncLifetime
{
    protected ApiFactory Api { get; } = api;

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// No test may leave a campaign still sending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in the campaign tests, because the test that breaks is
    /// not the one at fault. <c>OwnershipIsolationTests</c> creates a campaign
    /// to prove a stranger cannot read it and has no interest in the mail;
    /// under synchronous sending the mail was already in the box by the time
    /// the test ended, and <c>Email.Clear()</c> in the next test disposed of
    /// it. Now the worker is still going, and its mail arrives in the middle of
    /// whichever test runs next — plausibly <c>AccountEmailTests</c>, asserting
    /// that a password reset produced exactly one message.
    /// </para>
    /// <para>
    /// That failure would name the wrong test, appear only in certain orders,
    /// and read as flakiness. One drain at teardown makes it impossible instead
    /// of rare.
    /// </para>
    /// </remarks>
    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await Api.WaitForCampaignDispatchAsync(TimeSpan.FromSeconds(15));
        }
        catch (InvalidOperationException)
        {
            // Swallowed on purpose, and only here. Teardown is hygiene for the
            // next test, not a verdict on this one — throwing would stamp a
            // second, unrelated error over whatever the test actually found.
            // A genuinely stuck worker still surfaces: the next campaign test
            // waits on the same queue and does throw.
        }
    }

    /// <summary>
    /// Call at the top of every test.
    /// </summary>
    /// <remarks>
    /// Fails, loudly, with an actionable message. xUnit 2.x has no runtime skip,
    /// and faking one by returning early would mean every test in this suite
    /// passing on a machine with no database — the precise failure mode this
    /// whole suite exists to stop.
    ///
    /// Loud is also correct here: this project is opt-in
    /// (`pnpm test:api:integration`), so anyone running it wants it to run.
    /// </remarks>
    protected void RequireDatabase()
    {
        if (Api.Unavailable is not null)
            throw new InvalidOperationException(Api.Unavailable);
    }
}
