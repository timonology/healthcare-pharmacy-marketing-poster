using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acme.Application.Ai;
using Acme.Application.Common;
using Acme.Application.Templates;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Templates;

/// <summary>
/// Calls the Python ai-service to design a poster template.
/// </summary>
/// <remarks>
/// Generation lives in ai-service because that is where the canvas schema
/// mirror, the design system and the LLM clients already are. This class is a
/// thin transport: it forwards the administrator's own bearer token so the
/// Python side can re-check the role, and it translates transport failures into
/// the <see cref="Result{T}"/> vocabulary the controllers already speak.
/// </remarks>
public sealed class AiTemplateGenerator(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    ITemplateAssetService assets,
    ILogger<AiTemplateGenerator> logger,
    // A pharmacy's private uploads (SEC-15). Optional so a test that only
    // exercises gallery assets need not supply one.
    IPharmacyPhotoStore? pharmacyPhotos = null) : ITemplateGenerator
{
    /// <summary>The largest upload read back for the designer. Matches the upload cap.</summary>
    public const long MaxPhotoBytes = 12L * 1024 * 1024;

    private readonly AiServiceOptions _options = options.Value;

    /// <summary>
    /// Names the setting AND how to set it. "Set AiService:BaseUrl" is only
    /// useful to someone who already knows the environment-variable form is
    /// double-underscored and what host to point at.
    /// </summary>
    /// <summary>
    /// Base path for the canvas endpoints.
    /// </summary>
    /// <remarks>
    /// The FastAPI router declares prefix="/canvas", but main.py mounts it with
    /// app.include_router(canvas.router, prefix="/api") — so the served path is
    /// /api/canvas/…, not /canvas/…. Reading only the router's own prefix gives
    /// the wrong answer and every call 404s.
    /// </remarks>
    private const string CanvasRoot = "/api/canvas";

    private const string NotConfiguredMessage =
        "The design service isn't configured. Set AiService__BaseUrl on the API " +
        "(http://ai-service:8000 under docker-compose, http://localhost:8000 when " +
        "running ai-service directly), then restart the API.";

    public async Task<Result<GeneratedTemplate>> GenerateAsync(
        GenerateTemplateRequest request,
        string bearerToken,
        CancellationToken ct,
        string? quotaAssertion = null)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return Result<GeneratedTemplate>.Invalid(NotConfiguredMessage);
        }

        var photo = await BuildPhotoAsync(request.Photo, request.OwnerId, ct);
        if (!photo.IsSuccess) return Result<GeneratedTemplate>.Invalid(photo.Error!);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{CanvasRoot}/generate-template")
        {
            Content = JsonContent.Create(new
            {
                prompt = request.Prompt,
                category = request.Category,
                archetype = request.Archetype,
                photo = photo.Value,
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        // Only present for non-staff callers. ai-service accepts staff on the
        // role alone, so adding it to the admin path would be a second thing
        // that has to keep agreeing for no gain.
        if (!string.IsNullOrWhiteSpace(quotaAssertion))
            message.Headers.Add(AiQuotaAssertion.HeaderName, quotaAssertion);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Template generation timed out after {Timeout}s", _options.TimeoutSeconds);
            return Result<GeneratedTemplate>.Invalid(
                "The designer took too long to respond. Try a simpler brief.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Template generation could not reach ai-service");
            return Result<GeneratedTemplate>.Invalid(
                "Could not reach the design service. Check that ai-service is running.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                // The model could not satisfy the brief within its repair
                // budget. Surface the actual design problems — "add fewer
                // elements" is actionable, "generation failed" is not.
                return Result<GeneratedTemplate>.Invalid(DescribeFailure(body));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogWarning(
                    "ai-service rejected the forwarded token: {Status}", response.StatusCode);
                return Result<GeneratedTemplate>.Forbidden(
                    "The design service rejected this account.");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Template generation failed: {Status} {Body}",
                    response.StatusCode, Truncate(body, 500));
                return Result<GeneratedTemplate>.Invalid("The design service returned an error.");
            }

            try
            {
                var node = JsonNode.Parse(body)?.AsObject()
                    ?? throw new JsonException("Empty response.");

                var canvas = node["canvas"]
                    ?? throw new JsonException("Response had no canvas.");

                var name = node["name"]?.GetValue<string>() ?? "Untitled template";

                // Explicitly typed, not `var`: a collection expression has no
                // type of its own, so `var x = ... ?? []` cannot be inferred.
                IReadOnlyList<string> notes = node["notes"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? string.Empty)
                    .Where(s => s.Length > 0)
                    .ToList() ?? [];

                // The baked photograph comes back as bytes: stored here, where
                // asset URLs are minted, and swapped in for its placeholder key.
                var canvasJson = canvas.ToJsonString();
                foreach (var asset in (node["assets"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var key = Str(asset, "key");
                    var data = Convert.FromBase64String(Str(asset, "data_base64", "dataBase64"));
                    var contentType = Str(asset, "content_type", "contentType");
                    if (key.Length == 0 || data.Length == 0) continue;

                    await using var stream = new MemoryStream(data);
                    var stored = await assets.UploadAsync(
                        stream, contentType.Length > 0 ? contentType : "image/jpeg", string.Empty, ct);
                    canvasJson = ReplaceImageKey(canvasJson, key, stored.Url);

                    logger.LogInformation(
                        "Stored baked {Source} photo for {Name}: {Kb} KB",
                        Str(asset, "source"), name, data.Length / 1024);
                }

                logger.LogInformation(
                    "Generated template {Name} ({Notes} note(s))", name, notes.Count);

                IReadOnlyList<string> photoNotes = node["photo_notes"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? string.Empty)
                    .Where(s => s.Length > 0)
                    .ToList() ?? [];

                return Result<GeneratedTemplate>.Success(
                    new GeneratedTemplate(name, canvasJson, notes, photoNotes));
            }
            catch (FormatException ex)
            {
                logger.LogError(ex, "Template generation returned an unreadable photo");
                return Result<GeneratedTemplate>.Invalid(
                    "The design service returned a photo we could not read.");
            }
            catch (InvalidOperationException ex)
            {
                // TemplateAssetService refuses a content type it does not store.
                logger.LogError(ex, "Template generation returned a photo we cannot store");
                return Result<GeneratedTemplate>.Invalid(
                    "The design service returned a design we could not use.");
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Template generation returned unreadable JSON");
                return Result<GeneratedTemplate>.Invalid(
                    "The design service returned a response we could not read.");
            }
        }
    }

    /// <summary>
    /// The photo choice as ai-service wants it: an upload travels as bytes read
    /// from our own storage, so ai-service never fetches a URL anybody named.
    /// </summary>
    private async Task<Result<object?>> BuildPhotoAsync(PosterPhoto? photo, string? ownerId, CancellationToken ct)
    {
        if (photo is null || photo.Mode == PosterPhotoMode.None)
            return Result<object?>.Success(new { mode = "none" });

        var description = string.IsNullOrWhiteSpace(photo.Description) ? null : photo.Description.Trim();

        switch (photo.Mode)
        {
            case PosterPhotoMode.Stock:
                return Result<object?>.Success(new
                {
                    mode = "stock",
                    stock_photo_id = string.IsNullOrWhiteSpace(photo.StockPhotoId) ? null : photo.StockPhotoId,
                    description,
                });

            case PosterPhotoMode.Ai:
                return Result<object?>.Success(new { mode = "ai", description });

            case PosterPhotoMode.Upload when PharmacyPhotos.TryFileName(photo.AssetUrl, out var uploadName):
                // A pharmacy's private upload, read only from its own area: a
                // URL naming another pharmacy's file finds nothing here (SEC-15).
                if (ownerId is null || pharmacyPhotos is null)
                    return Result<object?>.Invalid("That photo is no longer available. Upload it again.");
                await using (var upload = await pharmacyPhotos.OpenAsync(ownerId, uploadName, ct))
                {
                    if (upload is null)
                        return Result<object?>.Invalid("That photo is no longer available. Upload it again.");
                    return await PhotoPayloadAsync(upload, PharmacyPhotos.ContentTypeFor(uploadName), description, ct);
                }

            case PosterPhotoMode.Upload:
                var blobKey = photo.AssetUrl is null ? null : assets.TryResolveBlobKey(photo.AssetUrl);
                var fileName = blobKey?[(blobKey.LastIndexOf('/') + 1)..];
                await using (var stream = fileName is null ? null : await assets.OpenAsync(fileName, ct))
                {
                    if (stream is null)
                        return Result<object?>.Invalid("That photo is no longer available. Upload it again.");
                    return await PhotoPayloadAsync(stream, TemplateAssetUrls.ContentTypeFor(fileName!), description, ct);
                }

            default:
                return Result<object?>.Invalid("Unknown photo option.");
        }
    }

    /// <summary>An upload as ai-service receives it: bytes, never a URL to fetch.</summary>
    private static async Task<Result<object?>> PhotoPayloadAsync(
        Stream stream, string contentType, string? description, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxPhotoBytes)
            return Result<object?>.Invalid("That photo is larger than 12MB.");

        return Result<object?>.Success(new
        {
            mode = "upload",
            data_base64 = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length),
            content_type = contentType,
            description,
        });
    }

    /// <summary>
    /// Swaps a placeholder image key for a stored URL, in image shapes only.
    /// </summary>
    /// <remarks>
    /// Done on the parsed document rather than with a string replace, so the
    /// placeholder appearing in a name or a text cannot be rewritten by accident.
    /// </remarks>
    public static string ReplaceImageKey(string canvasJson, string placeholder, string url)
    {
        if (JsonNode.Parse(canvasJson) is not JsonObject doc || doc["shapes"] is not JsonArray shapes)
            return canvasJson;

        foreach (var shape in shapes.OfType<JsonObject>())
        {
            if (shape["kind"]?.GetValue<string>() == "image"
                && shape["blobKey"] is JsonValue key
                && key.TryGetValue<string>(out var value)
                && value == placeholder)
            {
                shape["blobKey"] = url;
            }
        }
        return doc.ToJsonString();
    }

    public async Task<Result<ImageOptions>> GetImageOptionsAsync(string bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return Result<ImageOptions>.Success(new ImageOptions(false, false));

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{CanvasRoot}/image-options");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        try
        {
            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Image options unavailable: {Status}", response.StatusCode);
                // Unknown is "not offered": a mode shown and then failing is
                // worse than one held back until the service answers.
                return Result<ImageOptions>.Success(new ImageOptions(false, false));
            }
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
            return Result<ImageOptions>.Success(new ImageOptions(
                node?["ai"] is JsonValue a && a.TryGetValue<bool>(out var ai) && ai,
                node?["stock"] is JsonValue st && st.TryGetValue<bool>(out var stock) && stock));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Image options could not be read from ai-service");
            return Result<ImageOptions>.Success(new ImageOptions(false, false));
        }
    }

    public async Task<Result<GeneratedImage>> GenerateImageAsync(
        string description,
        string? aspect,
        int offset,
        string bearerToken,
        CancellationToken ct,
        string provider = "auto")
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return Result<GeneratedImage>.Invalid(NotConfiguredMessage);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{CanvasRoot}/generate-image")
        {
            Content = JsonContent.Create(new
            {
                description,
                aspect = string.IsNullOrWhiteSpace(aspect) ? "hero" : aspect,
                offset,
                provider,
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<GeneratedImage>.Invalid("Image generation timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Image generation could not reach ai-service");
            return Result<GeneratedImage>.Invalid("Could not reach the design service.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Image generation failed: {Status} {Body}",
                    response.StatusCode, Truncate(body, 300));

                return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? Result<GeneratedImage>.Forbidden("The design service rejected this account.")
                    : Result<GeneratedImage>.Invalid(DescribeImageFailure(body));
            }

            try
            {
                var node = JsonNode.Parse(body) as JsonObject
                    ?? throw new JsonException("Empty response.");

                var base64 = Str(node, "dataBase64", "data_base64");
                if (base64.Length == 0) throw new JsonException("Response had no image data.");

                var contentType = Str(node, "contentType", "content_type");
                var source = Str(node, "source");
                var data = Convert.FromBase64String(base64);

                logger.LogInformation(
                    "Poster image via {Source}: {Kb} KB",
                    source.Length > 0 ? source : "ai", data.Length / 1024);

                return Result<GeneratedImage>.Success(new GeneratedImage(
                    data,
                    contentType.Length > 0 ? contentType : "image/png",
                    Num(node, "width"),
                    Num(node, "height"),
                    Str(node, "description") is { Length: > 0 } d ? d : null,
                    source.Length > 0 ? source : "ai",
                    (int)Num(node, "remaining")));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                logger.LogError(ex, "Image generation returned unreadable data");
                return Result<GeneratedImage>.Invalid(
                    "The design service returned an image we could not read.");
            }
        }
    }

    public async Task<Result<IReadOnlyList<StockPhoto>>> SearchStockPhotosAsync(
        string query,
        string? aspect,
        string bearerToken,
        CancellationToken ct,
        string? quotaAssertion = null)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return Result<IReadOnlyList<StockPhoto>>.Invalid(NotConfiguredMessage);

        if (string.IsNullOrWhiteSpace(query))
            return Result<IReadOnlyList<StockPhoto>>.Invalid("Enter something to search for.");

        var url = $"{CanvasRoot}/stock-photos"
            + $"?query={Uri.EscapeDataString(query)}"
            + (string.IsNullOrWhiteSpace(aspect) ? "" : $"&aspect={Uri.EscapeDataString(aspect)}");

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (!string.IsNullOrWhiteSpace(quotaAssertion))
            message.Headers.Add(AiQuotaAssertion.HeaderName, quotaAssertion);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Stock photo search could not reach ai-service");
            return Result<IReadOnlyList<StockPhoto>>.Invalid(
                "Could not reach the photo service.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Stock photo search failed: {Status} {Body}",
                    response.StatusCode, Truncate(body, 300));
                return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? Result<IReadOnlyList<StockPhoto>>.Forbidden("The photo service rejected this account.")
                    : Result<IReadOnlyList<StockPhoto>>.Invalid(DescribeImageFailure(body));
            }

            try
            {
                var root = JsonNode.Parse(body) as JsonObject
                    ?? throw new JsonException("Empty response.");

                var photos = (root["photos"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(p => new StockPhoto(
                        Str(p, "id"),
                        Str(p, "thumbnailUrl", "thumbnail_url"),
                        Num(p, "width"),
                        Num(p, "height"),
                        Str(p, "photographer"),
                        Str(p, "alt")))
                    .Where(p => p.Id.Length > 0 && p.ThumbnailUrl.Length > 0)
                    .ToList();

                return Result<IReadOnlyList<StockPhoto>>.Success(photos);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Stock photo search returned unreadable JSON");
                return Result<IReadOnlyList<StockPhoto>>.Invalid(
                    "The photo service returned a response we could not read.");
            }
        }
    }

    public async Task<Result<GeneratedImage>> FetchStockPhotoAsync(
        string photoId,
        string bearerToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return Result<GeneratedImage>.Invalid(NotConfiguredMessage);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{CanvasRoot}/stock-photo")
        {
            Content = JsonContent.Create(new { photo_id = photoId }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Stock photo download could not reach ai-service");
            return Result<GeneratedImage>.Invalid("Could not reach the photo service.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Stock photo download failed: {Status} {Body}",
                    response.StatusCode, Truncate(body, 300));
                return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? Result<GeneratedImage>.Forbidden("The photo service rejected this account.")
                    : Result<GeneratedImage>.Invalid(DescribeImageFailure(body));
            }

            try
            {
                var root = JsonNode.Parse(body) as JsonObject
                    ?? throw new JsonException("Empty response.");

                var base64 = Str(root, "dataBase64", "data_base64");
                if (base64.Length == 0) throw new JsonException("Response had no image data.");

                var contentType = Str(root, "contentType", "content_type");
                var alt = Str(root, "alt");
                var photographer = Str(root, "photographer");

                return Result<GeneratedImage>.Success(new GeneratedImage(
                    Convert.FromBase64String(base64),
                    contentType.Length > 0 ? contentType : "image/jpeg",
                    Num(root, "width"),
                    Num(root, "height"),
                    BuildDescription(alt, photographer)));
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                logger.LogError(ex, "Stock photo download returned unreadable data");
                return Result<GeneratedImage>.Invalid(
                    "The photo service returned an image we could not read.");
            }
        }
    }

    /// <summary>
    /// What the designer is told the photo shows. Pexels' `alt` text is a real
    /// description ("pharmacist handing over a prescription"), which materially
    /// improves layout — the designer never sees the image itself.
    /// </summary>
    private static string? BuildDescription(string alt, string photographer)
    {
        if (alt.Length == 0) return null;
        return photographer.Length > 0 ? $"{alt} (photo by {photographer})" : alt;
    }

    /// <summary>
    /// Reads a string property, trying several names.
    /// </summary>
    /// <remarks>
    /// ai-service is FastAPI and serialises snake_case; ASP.NET emits camelCase.
    /// Accepting both spellings means a future casing change on either side
    /// degrades to a missing field rather than a silent null.
    /// </remarks>
    private static string Str(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
                return s;
        }
        return string.Empty;
    }

    private static double Num(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue v && v.TryGetValue<double>(out var d))
                return d;
        }
        return 0;
    }

    private static string DescribeImageFailure(string body)
    {
        const string fallback = "Could not generate that image.";
        var detail = TryReadDetail(body);

        // FastAPI's `detail` is a plain string for framework errors (404 ->
        // "Not Found") and an object only where we raise it as a dict. Both
        // shapes arrive here.
        if (detail is JsonValue value && value.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? fallback : text;

        if (detail is JsonObject obj
            && obj["error"] is JsonValue e
            && e.TryGetValue<string>(out var error)
            && !string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        return fallback;
    }

    /// <summary>
    /// The "detail" node of an error body, or null if the body isn't a JSON
    /// object at all.
    /// </summary>
    /// <remarks>
    /// Indexing a JsonNode that isn't an object throws InvalidOperationException
    /// rather than returning null — which is how a 404 body of
    /// {"detail":"Not Found"} previously turned a clean error path into an
    /// unhandled 500 that buried the real cause.
    /// </remarks>
    private static JsonNode? TryReadDetail(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject root ? root["detail"] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Turn ai-service's 422 payload into one readable sentence.</summary>
    public static string DescribeFailure(string body)
    {
        const string fallback =
            "The designer could not produce a valid poster for that brief. " +
            "Try describing the subject and tone more concretely.";

        var detail = TryReadDetail(body);

        // Same shape caveat as DescribeImageFailure: `detail` is a bare string
        // for framework errors and only an object when we raise it as a dict.
        // Indexing it unconditionally threw InvalidOperationException, which
        // escaped the JsonException catch entirely.
        if (detail is JsonValue value && value.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? fallback : text;

        if (detail is not JsonObject obj) return fallback;

        // A failure that is not about the design — "AI images are not set up",
        // "the chosen stock photo could not be used" — arrives as an error with
        // no problem list. Its own sentence is the useful one; the generic
        // "try rewording" would send somebody to fix the wrong thing.
        var error = obj["error"] is JsonValue ev && ev.TryGetValue<string>(out var e) ? e : null;
        if (obj["problems"] is not JsonArray array || array.Count == 0)
            return string.IsNullOrWhiteSpace(error) ? fallback : error;

        var problems = array
            .OfType<JsonValue>()
            .Select(p => p.TryGetValue<string>(out var s) ? s : string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(4)
            .ToList();

        return problems.Count > 0
            ? fallback + " Last issues: " + string.Join("; ", problems)
            : fallback;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
