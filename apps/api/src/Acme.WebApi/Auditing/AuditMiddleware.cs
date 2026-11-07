using System.Text.RegularExpressions;
using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Infrastructure.Options;
using Acme.WebApi.Middleware;
using Microsoft.Extensions.Options;

namespace Acme.WebApi.Auditing;

/// <summary>
/// Gives every request an id, and writes the audit or activity event for
/// endpoints that ask for one — after the response is decided.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placed after authentication and before authorization</b>, so it knows
/// who is calling and still sees a 403 that authorization produces without
/// ever running the controller. That is the only way "a refused request is
/// recorded as denied" holds for requests refused by policy as well as by a
/// service.
/// </para>
/// <para>
/// One event per request. The endpoint's attribute names the action; the
/// service's <see cref="IAuditScope"/> fills in what only it knows; the status
/// code decides the result. Nothing is written for endpoints without an
/// attribute — in particular no GET is logged unless it is allow-listed.
/// </para>
/// </remarks>
public sealed partial class AuditMiddleware(RequestDelegate next)
{
    public const string RequestIdHeader = "X-Request-Id";
    internal const string ActorItemKey = "audit.actor";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeRequestId();

    public async Task InvokeAsync(
        HttpContext ctx,
        IAuditLogger audit,
        IAuditScope scope,
        IUserRepository users,
        IOptions<SmokeAccountOptions> smoke,
        ILogger<AuditMiddleware> log)
    {
        // The request id: the web app's if it sent a sane one (so a browser
        // error and the API's record share an id), otherwise a new one.
        var incoming = ctx.Request.Headers[RequestIdHeader].ToString();
        ctx.TraceIdentifier = SafeRequestId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[RequestIdHeader] = ctx.TraceIdentifier;
            return Task.CompletedTask;
        });

        var endpoint = ctx.GetEndpoint();
        var auditAttr = endpoint?.Metadata.GetMetadata<AuditAttribute>();
        var activityAttr = endpoint?.Metadata.GetMetadata<ActivityLogAttribute>();

        // Resolved before the endpoint runs, so a sign-out still knows who.
        var actor = AuditActorResolver.Resolve(ctx.User, smoke.Value.Email);
        ctx.Items[ActorItemKey] = actor;

        if (auditAttr is null && activityAttr is null)
        {
            await next(ctx);
            return;
        }

        int? thrownStatus = null;
        try
        {
            await next(ctx);
        }
        catch (Exception ex)
        {
            // The exception handler outside us turns this into a response;
            // record the status it will choose, not a guess.
            thrownStatus = ExceptionHandlingMiddleware.StatusFor(ex);
            throw;
        }
        finally
        {
            try
            {
                var status = thrownStatus ?? ctx.Response.StatusCode;
                await WriteAsync(ctx, status, actor, auditAttr, activityAttr, audit, scope, users, smoke.Value.Email);
            }
            catch (Exception ex)
            {
                // Never let recording a request break the request.
                log.LogError(ex, "Could not record audit event for {Path}", ctx.Request.Path);
            }
        }
    }

    private static async Task WriteAsync(
        HttpContext ctx, int status, AuditActor actor,
        AuditAttribute? auditAttr, ActivityLogAttribute? activityAttr,
        IAuditLogger audit, IAuditScope scope, IUserRepository users, string? smokeEmail)
    {
        var state = scope.State;
        if (state.Suppressed) return;

        var result = AuditResults.ForStatus(status);

        // A 401 with no token on a protected endpoint is an expired session or
        // a scanner, many times a minute, and names nobody. Recorded only where
        // it is the event itself: sign-in, and a screen's token being refused.
        if (status == StatusCodes.Status401Unauthorized && state.Actor is null
            && actor.Id == AuditActor.Anonymous.Id
            && (auditAttr?.Action ?? activityAttr?.Action) is { } name
            && !name.StartsWith("auth.", StringComparison.Ordinal)
            && !name.StartsWith("display.", StringComparison.Ordinal))
            return;

        var who = state.Actor
                  ?? (auditAttr?.SystemActor is { Length: > 0 } machine ? AuditActor.System(machine) : actor);

        // A delegated token from before act_role existed: look the actor up
        // once, rather than file an administrator's work as a pharmacy's.
        if (who.Role is null && who.Id != AuditActor.Anonymous.Id && !who.Id.StartsWith("system:"))
        {
            var user = await users.FindByIdAsync(who.Id, CancellationToken.None);
            if (user is not null)
            {
                var role = user.Role.ToString();
                // The smoke account stays system whatever its role.
                var kind = who.Kind == ActorKinds.System ? ActorKinds.System : AuditActorResolver.KindFor(role, null, null);
                who = who with { Role = role, Kind = kind };
            }
        }

        // The smoke account is a machine however it was named — by its token
        // or, at sign-in, by the service.
        if (who.Kind != ActorKinds.System
            && AuditActorResolver.KindFor(null, who.Email, smokeEmail) == ActorKinds.System)
            who = who with { Kind = ActorKinds.System };

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var meta = new Dictionary<string, string>(state.Meta, StringComparer.Ordinal);
        if (result != AuditResults.Ok) meta["status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (activityAttr is not null)
        {
            var entityId = state.EntitySet ? state.EntityId : RouteValue(ctx, activityAttr.IdRoute) ?? state.EntityId;
            var entityType = state.EntitySet ? state.EntityType ?? activityAttr.EntityType : activityAttr.EntityType;
            var action = state.Action ?? activityAttr.Action;

            var viewPharmacy = state.PharmacyId ?? RouteValue(ctx, activityAttr.PharmacyRoute);

            if (result == AuditResults.Ok)
            {
                // A view has to have a viewer: the public gallery is not "who did what".
                if (who.Id == AuditActor.Anonymous.Id) return;

                await audit.RecordActivityAsync(
                    AuditEvent.Create(who, action, entityType, entityId, result, meta,
                        viewPharmacy, ip, ctx.TraceIdentifier),
                    TimeSpan.FromSeconds(Math.Max(1, activityAttr.DedupeSeconds)),
                    CancellationToken.None);
            }
            else if (result == AuditResults.Denied)
            {
                // Trying to open something you may not see is a security event.
                await audit.RecordAsync(
                    AuditEvent.Create(who, action, entityType, entityId, result, meta,
                        viewPharmacy, ip, ctx.TraceIdentifier),
                    CancellationToken.None);
            }
            return;
        }

        if (auditAttr is null) return;
        if (auditAttr.OnlyWhenDenied && result != AuditResults.Denied) return;

        var auditAction = state.Action
                          ?? (result != AuditResults.Ok && auditAttr.FailedAction is { } f ? f : auditAttr.Action);
        var auditEntityId = state.EntitySet ? state.EntityId : RouteValue(ctx, auditAttr.IdRoute) ?? state.EntityId;
        var auditEntityType = state.EntitySet ? state.EntityType ?? auditAttr.EntityType : auditAttr.EntityType;

        await audit.RecordAsync(
            AuditEvent.Create(who, auditAction, auditEntityType, auditEntityId, result, meta,
                state.PharmacyId ?? RouteValue(ctx, auditAttr.PharmacyRoute), ip, ctx.TraceIdentifier),
            CancellationToken.None);
    }

    private static string? RouteValue(HttpContext ctx, string? key) =>
        key is not null && ctx.GetRouteValue(key)?.ToString() is { Length: > 0 } v ? v : null;
}
