using System.Collections.Concurrent;
using System.Reflection;
using Acme.Application.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Acme.WebApi.Auditing;

/// <summary>
/// For an audited request with no id in the route — a create — takes the
/// entity id from the response body's <c>Id</c>, so <c>poster.create</c>
/// records which poster was created without every action having to say so.
/// </summary>
/// <remarks>
/// Only touches audited endpoints, only successful object results, and never
/// overrides an id the route or a service already supplied. The property
/// lookup is cached per response type.
/// </remarks>
public sealed class AuditResultFilter(IAuditScope scope) : IAsyncResultFilter
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> IdProperty = new();

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        var audited = metadata.OfType<AuditAttribute>().FirstOrDefault();

        if (audited is not null
            && context.HttpContext.GetRouteValue(audited.IdRoute) is null
            && context.Result is ObjectResult { Value: { } value } result
            && (result.StatusCode ?? StatusCodes.Status200OK) < 300)
        {
            var prop = IdProperty.GetOrAdd(value.GetType(), static t =>
                t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
            if (prop?.GetValue(value)?.ToString() is { Length: > 0 } id)
                scope.SetEntityIdIfEmpty(id);
        }

        return next();
    }
}
