namespace Acme.WebApi.Middleware;

/// <summary>
/// Headers on every API response (security review SEC-09).
/// </summary>
/// <remarks>
/// <para>
/// The API returns JSON, PDFs and images — never a page meant to run script.
/// So the policy is the strictest there is: <c>default-src 'none'</c>. If a
/// response is ever opened directly in a browser tab — a pharmacy's uploaded
/// SVG logo, say — nothing in it can run or load anything, and nothing can
/// frame it.
/// </para>
/// <para>
/// Swagger UI (Development only) is the one page the API serves, and it needs
/// its scripts, so its paths are left without a CSP.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Policy = "default-src 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            // PDFs are opened in the browser's own viewer, which a strict
            // policy can block; a PDF cannot run page script anyway.
            var isPdf = context.Response.ContentType?.StartsWith("application/pdf",
                StringComparison.OrdinalIgnoreCase) == true;
            if (!isPdf && !context.Request.Path.StartsWithSegments("/swagger"))
                headers.ContentSecurityPolicy = Policy;
            return Task.CompletedTask;
        });
        return next(context);
    }
}
