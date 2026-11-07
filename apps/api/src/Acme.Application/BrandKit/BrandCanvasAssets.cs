using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acme.Domain.Users;

// The namespace and the type share a name; every other file that needs both
// aliases it rather than relying on innermost-first lookup.
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Application.BrandKit;

/// <summary>
/// How a pharmacy's logo is referenced inside a canvas, and how that reference
/// becomes bytes at render time.
/// </summary>
/// <remarks>
/// <para>
/// The brand kit hands out a fifteen-minute signed URL so the brand-kit page can
/// show the logo. That is correct for a page open right now and wrong for
/// anything saved: a canvas is stored and re-read for years, so a signed URL
/// written into one is a picture that works until the signature lapses and then
/// vanishes from every poster, PDF and email that used it — with no error,
/// because a missing image is not an exception.
/// </para>
/// <para>
/// So a canvas holds <see cref="SrcPath"/>, a stable path with no credentials
/// and no expiry, and each renderer resolves it when it draws. The editor
/// fetches it over the session-authenticated route. The exporter cannot: it
/// would be the API making an HTTP request to itself, over a URL taken from a
/// document that may have been authored against an entirely different host —
/// which inside a container network usually cannot be reached at all. Instead it
/// rewrites the reference to the owner's blob key and reads storage directly,
/// exactly as template assets already work.
/// </para>
/// </remarks>
public static class BrandCanvasAssets
{
    /// <summary>What a canvas stores. Must match BRAND_LOGO_SRC in shared-types.</summary>
    public const string SrcPath = "/api/brand-kit/logo";

    /// <summary>
    /// What a canvas stores for a booking QR. Must match BRAND_BOOKING_QR_SRC.
    /// </summary>
    public const string BookingQrSrcPath = "/api/brand-kit/booking-qr";

    /// <summary>True when this image source means "a QR for the owner's booking link".</summary>
    public static bool IsBookingQr(string? src) => MatchesBrandPath(src, BookingQrSrcPath);

    /// <summary>
    /// True when this image source means "the owner's logo".
    /// </summary>
    /// <remarks>
    /// Absolute forms are accepted because a canvas may have been authored
    /// against a different host — the same allowance template assets make, and
    /// for the same reason: the document outlives the deployment it was made on.
    /// </remarks>
    public static bool IsBrandLogo(string? src) => MatchesBrandPath(src, SrcPath);

    /// <summary>
    /// Whether an image source points at one of our own brand routes.
    /// </summary>
    /// <remarks>
    /// Shared by the logo and the booking QR so the two cannot drift. The
    /// absolute-URL allowance is the point of it: a canvas may have been
    /// authored against a different host, and the document outlives the
    /// deployment it was made on.
    /// </remarks>
    private static bool MatchesBrandPath(string? src, string path)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;

        var value = src.Trim();
        if (value.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return true;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The id <c>apply-pharmacy.ts</c> gives a placed logo. Kept in step with it.</summary>
    public const string LogoShapeId = "pharmacy-logo";

    /// <summary>
    /// Puts the pharmacy's logo on a new design that has no slot for one.
    /// </summary>
    /// <remarks>
    /// The same place and size the editor's "Apply pharmacy info" uses — 16% of
    /// the page wide, half as tall, 80px in from the right and 90px up from the
    /// bottom — which the house style keeps clear for exactly this. The shape
    /// holds <see cref="SrcPath"/>, not a blob key, so it follows the brand kit:
    /// change the logo and every poster changes with it. A design that already
    /// has a logo slot keeps it, wherever its author put it.
    /// </remarks>
    public static string PlaceLogo(string canvasJson)
    {
        if (JsonNode.Parse(canvasJson) is not JsonObject doc
            || doc["shapes"] is not JsonArray shapes)
            return canvasJson;

        foreach (var s in shapes.OfType<JsonObject>())
        {
            var id = s["id"]?.GetValue<string>();
            var src = s["blobKey"] is JsonValue v && v.TryGetValue<string>(out var k) ? k : null;
            if (id == LogoShapeId || IsBrandLogo(src)) return canvasJson;
        }

        var width = ReadNumber(doc, "width", 1240);
        var height = ReadNumber(doc, "height", 1754);
        var w = Math.Round(width * 0.16);
        var h = Math.Round(w * 0.5);
        var top = shapes.OfType<JsonObject>()
            .Select(s => ReadNumber(s, "zIndex", 0)).DefaultIfEmpty(0).Max();

        shapes.Add(new JsonObject
        {
            ["id"] = LogoShapeId,
            ["kind"] = "image",
            ["position"] = new JsonObject { ["x"] = width - w - 80, ["y"] = height - h - 90 },
            ["rotation"] = 0,
            ["scale"] = new JsonObject { ["x"] = 1, ["y"] = 1 },
            ["opacity"] = 1,
            ["draggable"] = true,
            ["zIndex"] = top + 1,
            ["blobKey"] = SrcPath,
            ["width"] = w,
            ["height"] = h,
        });
        return doc.ToJsonString();
    }

    private static double ReadNumber(JsonObject obj, string key, double fallback) =>
        obj[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    /// <summary>
    /// Fills the <c>{{address}}</c> style tokens a template carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The editor's Apply pharmacy info button cannot be relied on.</b> It is
    /// a button somebody presses, and the built-in templates now write their
    /// contact line as tokens. A pharmacist who opens a template, edits the
    /// headline and publishes without pressing it would otherwise print
    /// <c>{{address}}</c> on a poster and email it to patients.
    /// </para>
    /// <para>
    /// So the render resolves them too. The button remains worth pressing —
    /// it places the logo and shows the real details in the editor — but it is
    /// no longer the only thing standing between a template and a broken
    /// poster.
    /// </para>
    /// <para>
    /// An unknown or unset value resolves to an empty string rather than being
    /// left in place. A blank line on a poster is a gap; a literal
    /// <c>{{phone}}</c> is a mistake a patient can see.
    /// </para>
    /// <para>
    /// Exposed separately from the render-time resolution because the two happen
    /// for different reasons and one is not a fallback for the other. This one
    /// keeps tokens out of the editor; the render-time one keeps them out of
    /// print for any poster that got past this — a poster created before this
    /// existed, or one built from an admin template that gained tokens later.
    /// </para>
    /// <para>
    /// <b>The kit has no default.</b> It was added with one, and the effect was
    /// that the single call site kept compiling and silently stopped filling
    /// <c>{{booking_url}}</c> — a new poster made from a template would open in
    /// the editor with the booking line blank, and nothing anywhere would say
    /// why. Requiring it makes adding a source of pharmacy data a decision the
    /// compiler asks about at every call site, which is the whole reason
    /// <c>BrandAssetCallSiteTests</c> had to be written for the method below.
    /// </para>
    /// </remarks>
    public static string ResolveTokensForNewPoster(
        string canvasJson, PharmacyProfile? profile, DomainBrandKit? kit) =>
        string.IsNullOrWhiteSpace(canvasJson)
            ? canvasJson
            : ResolveTokens(canvasJson, profile, kit?.Pharmacy.BookingUrl, forPrint: false);

    /// <param name="forPrint">
    /// <para>
    /// What to do when the pharmacy has no profile — it signed up and skipped
    /// onboarding.
    /// </para>
    /// <para>
    /// <b>Creating a poster keeps the tokens</b> (<c>false</c>): the stored
    /// design still says <c>{{address}}</c>, so the moment the pharmacy
    /// completes onboarding every poster it already made fills in, exactly as a
    /// poster made afterwards would.
    /// </para>
    /// <para>
    /// <b>Rendering blanks them</b> (<c>true</c>): a PDF, PNG, email or screen
    /// must never show <c>{{address}}</c> to a patient. A blank line is a gap;
    /// the literal token is a mistake anyone can see. The editor does the same
    /// on screen (<c>display-tokens.ts</c>) without touching the stored text.
    /// </para>
    /// </param>
    private static string ResolveTokens(
        string canvasJson, PharmacyProfile? profile, string? bookingUrl, bool forPrint)
    {
        if (profile is null)
        {
            if (!forPrint) return canvasJson;
            return PharmacyToken.Replace(canvasJson, m =>
                m.Groups["token"].Value.ToLowerInvariant() is
                    "booking_url" or "bookingurl" or "booking url" or
                    "booking_link" or "bookinglink" or "booking link"
                    ? JsonEscape(bookingUrl?.Trim() ?? string.Empty)
                    : string.Empty);
        }

        // JSON-escaped, because these land inside a "text":"..." value and a
        // pharmacy name containing a quote would otherwise break the canvas.
        string V(string? s) => JsonEscape(s?.Trim() ?? string.Empty);

        var address = string.Join(", ", new[] { profile.Address, profile.PostCode }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim()));

        return PharmacyToken.Replace(canvasJson, m => m.Groups["token"].Value.ToLowerInvariant() switch
        {
            "pharmacy_name" or "pharmacyname" or "pharmacy name" => V(profile.PharmacyName),
            "address"      => JsonEscape(address),
            "post_code" or "postcode" or "post code" => V(profile.PostCode),
            "contact_name" or "contactname" or "contact name" => V(profile.ContactName),
            "phone"        => V(profile.ContactPhone),
            // Every spelling the regex admits, and that is the point: a form
            // the pattern matches but this switch does not falls to `_` below
            // and the literal token is printed on a poster.
            //
            // `{{booking link}}` and `{{bookinglink}}` did exactly that on the
            // first version of this. Enumerating the variants by hand next to a
            // pattern that generates them is a standing trap — the test walks
            // every combination the regex can produce and checks it resolves.
            //
            // Empty when the pharmacy has not set one, like every other unset
            // value here. A gap on a poster is a gap; the words "booking_url"
            // in front of a patient is a mistake they can see.
            "booking_url" or "bookingurl" or "booking url" or
            "booking_link" or "bookinglink" or "booking link"
                           => V(bookingUrl),
            _              => m.Value,
        });
    }

    /// <summary>
    /// Mirrors the token list in <c>apps/web/src/lib/canvas/apply-pharmacy.ts</c>.
    /// </summary>
    /// <remarks>
    /// Two lists that have to agree, and nothing makes them. The editor's button
    /// uses the web one and the renderer uses this one, so a token added to only
    /// one side resolves in the editor and prints literally, or the reverse —
    /// and the pharmacist sees the right thing on screen either way. Adding a
    /// token means editing both.
    /// </remarks>
    private static readonly Regex PharmacyToken = new(
        @"\{\{\s*(?<token>pharmacy[_ ]?name|address|post[_ ]?code|contact[_ ]?name|phone|booking[_ ]?(?:url|link))\s*\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves a canvas's brand references — the logo and the booking QR — and
    /// its pharmacy tokens, ready for a renderer.
    /// </summary>
    /// <param name="canvasJson">The stored document. Returned unchanged when empty.</param>
    /// <param name="kit">
    /// The owner's brand kit, or null when they have not made one.
    /// </param>
    /// <param name="profile">The onboarding profile, for the name and address tokens.</param>
    /// <param name="qr">
    /// Encoder for the booking QR, or null to leave QR references unresolved.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Optional on purpose.</b> Null drops a QR reference exactly as a
    /// missing logo drops the logo, so the many callers that do not care — the
    /// admin preview, most tests — carry on unchanged and a forgotten wiring is
    /// a poster with no QR rather than a crash.
    /// </para>
    /// <para>
    /// The encoded PNG lands in the canvas as a data URI, and the resolved
    /// canvas is hashed into the render cache key. That is deliberate and it is
    /// what makes "change your booking link, every poster updates" work — but it
    /// also means the encoder must be deterministic, or every render would miss
    /// the cache and re-rasterise. See <c>IQrCodeGenerator</c>.
    /// </para>
    /// <para>
    /// <b>The second parameter is the whole kit, not a logo key.</b> It was a
    /// <c>string? logoBlobKey</c> until the booking link moved onto the kit, at
    /// which point the choice was a fifth loose parameter or this. A fifth would
    /// have been a fifth thing a call site can forget, and the optional ones
    /// already here fail silently — which is why <c>BrandAssetCallSiteTests</c>
    /// exists. Passing the record means the logo and the link cannot arrive
    /// separately, because they are one record, and every call site was already
    /// loading the kit to read a single field off it.
    /// </para>
    /// </remarks>
    public static string ResolveForRender(
        string canvasJson,
        DomainBrandKit? kit,
        PharmacyProfile? profile = null,
        IQrCodeGenerator? qr = null)
    {
        if (string.IsNullOrWhiteSpace(canvasJson)) return canvasJson;

        // First, and before the logo is swapped in: any image source a poster
        // may not draw — another host, a raw storage key — is blanked, so the
        // renderer never fetches or reads it (security review SEC-01, SEC-02).
        // Saving already refuses these; this covers anything stored earlier.
        canvasJson = Posters.CanvasImageSources.StripDisallowed(canvasJson);

        var logoBlobKey = kit?.LogoBlobKey;
        canvasJson = ResolveTokens(canvasJson, profile, kit?.Pharmacy.BookingUrl, forPrint: true);

        // Encoded once per render, and only when this canvas actually asks for
        // one.
        //
        // Once per render rather than once per reference: a poster with the code
        // on the front and again on a tear-off strip would otherwise pay for it
        // twice and, worse, could embed two different images if the encoder ever
        // stopped being deterministic.
        //
        // And guarded by a string scan, because most posters carry no code at
        // all. Without this every render for every pharmacy with a booking link
        // would encode one and throw it away — cheap each time, paid on every
        // export, every screen refresh and every campaign send. The scan matches
        // the absolute form too, since the path appears inside it.
        var qrDataUri = canvasJson.Contains(BookingQrSrcPath, StringComparison.OrdinalIgnoreCase)
            ? BuildBookingQrDataUri(kit?.Pharmacy.BookingUrl, qr)
            : null;

        // A string replace rather than a parse-and-rewrite. The canvas schema is
        // the web app's to own, and round-tripping it through a partial C# model
        // here would quietly drop any shape property the API does not know about
        // — which is how an editor feature disappears from exports one release
        // after it ships.
        return BlobKeyValue.Replace(canvasJson, m =>
        {
            var src = m.Groups["src"].Value;

            if (IsBrandLogo(src))
            {
                var replacement = string.IsNullOrWhiteSpace(logoBlobKey) ? "" : logoBlobKey;
                return $"\"blobKey\":\"{JsonEscape(replacement)}\"";
            }

            if (IsBookingQr(src))
            {
                // Empty when there is no booking link, or no encoder, or the
                // link would not encode. Every renderer skips an empty source,
                // so the poster comes out with a gap rather than a square that
                // scans to nothing — which is the worse outcome, because a
                // patient cannot tell a dead QR from a working one until they
                // have already pointed a phone at it.
                return $"\"blobKey\":\"{JsonEscape(qrDataUri ?? "")}\"";
            }

            return m.Value;
        });
    }

    /// <summary>The booking QR as a data URI, or null when there should not be one.</summary>
    private static string? BuildBookingQrDataUri(string? bookingUrl, IQrCodeGenerator? qr)
    {
        if (qr is null || string.IsNullOrWhiteSpace(bookingUrl)) return null;

        var png = qr.ToPng(bookingUrl);
        return png is null or { Length: 0 }
            ? null
            : "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static readonly Regex BlobKeyValue = new(
        "\"blobKey\"\\s*:\\s*\"(?<src>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
