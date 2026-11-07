using System.Text.Json;
using Acme.Domain.Common;

namespace Acme.WebApi.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception");
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "Internal server error.");
        }
    }

    /// <summary>
    /// The status a thrown exception becomes. Shared with the audit middleware,
    /// which sits inside this one and must record what the caller actually got.
    /// </summary>
    public static int StatusFor(Exception ex) =>
        ex is DomainException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;

    private static async Task WriteAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { error = message });
        await context.Response.WriteAsync(payload);
    }
}
