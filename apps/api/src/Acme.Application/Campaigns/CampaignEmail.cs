using System.Net;
using System.Text;

namespace Acme.Application.Campaigns;

/// <param name="Url">Signed, time-limited link to the video.</param>
/// <param name="ThumbnailUrl">
/// The still's <c>src</c>: normally a signed link to
/// <c>/api/videos/{id}/public-download?…&amp;thumb=true</c>, a <c>data:</c> URL
/// only when this deployment has no address a mail provider's fetcher can
/// reach, and null when the video has no still. Chosen by
/// <c>CampaignService.ChooseThumbnailSrcAsync</c>.
///
/// <b>It was "a data URL" unconditionally, and Gmail strips those</b> — the
/// poster rendered and the space above the watch button did not. Stated here
/// because this doc line is what the next person will read.
/// </param>
public sealed record VideoEmailLink(string Name, string Url, string? ThumbnailUrl);

/// <summary>
/// The campaign email a patient actually receives.
/// </summary>
/// <remarks>
/// <para>
/// The poster is the message. It used to travel as a PDF attachment, which
/// meant the email itself said almost nothing and the design was only seen by
/// recipients who chose to open a file — a poor showing on a phone, where most
/// of these are read. It is now drawn in the body, full width, and the PDF is
/// gone.
/// </para>
/// <para>
/// Written as table-based HTML with inline styles because that is what mail
/// clients support. Outlook renders through Word, which ignores most of
/// <c>float</c>, <c>flex</c>, and any stylesheet; Gmail strips <c>&lt;style&gt;</c>
/// blocks entirely on some clients. Anything structural has to be a table with
/// <c>align</c> attributes, and every rule has to sit on the element.
/// </para>
/// </remarks>
public static class CampaignEmail
{
    /// <summary>
    /// The cid: an inlined poster was referenced by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No longer used by campaign email.</b> The poster is linked now, not
    /// embedded: Mailjet's Send v3.1 copies an inline attachment into every
    /// recipient's personalisation, so the design travelled once per recipient —
    /// 244 KB each, measured, which is a quarter of a gigabyte for a campaign of
    /// a thousand and eighty seconds of uploading for a campaign of four.
    /// </para>
    /// <para>
    /// Kept because the sender still understands <c>InlineImages</c>, and
    /// because a message that genuinely needs an embedded image — one going to a
    /// single known recipient, where the size does not multiply — should use
    /// this rather than invent a second constant.
    /// </para>
    /// </remarks>
    public const string PosterContentId = "campaign-poster";

    /// <summary>Body width. 600px is the long-standing safe maximum.</summary>
    private const int BodyWidth = 600;

    /// <summary>
    /// The design, and how it reaches the reader.
    /// </summary>
    /// <param name="Src">
    /// What goes in the <c>img src</c>: an <c>https</c> address when the poster
    /// is fetched, or <c>cid:campaign-poster</c> when it travels in the message.
    /// </param>
    /// <param name="LinkUrl">
    /// Where the image and the "Not showing?" line point, or <see langword="null"/>
    /// when the image is embedded and there is nothing to open.
    /// </param>
    /// <remarks>
    /// Two fields rather than a string and a boolean, so the two cannot be
    /// passed in the wrong order or drift apart — an <c>https</c> src with no
    /// link is a reader with no way to recover a blocked image, and a
    /// <c>cid:</c> src with a link is a link that goes nowhere.
    /// </remarks>
    public sealed record CampaignPosterImage(string Src, string? LinkUrl);

    /// <param name="posterImage">
    /// The design, or null when there is no render to show.
    /// </param>
    public static string BuildHtml(
        string campaignName,
        string posterName,
        string pharmacy,
        CampaignPosterImage? posterImage,
        IReadOnlyList<VideoEmailLink> videos,
        string? unsubscribeUrl = null)
    {
        var campaign = Enc(campaignName);
        var poster = Enc(posterName);
        var from = Enc(pharmacy);

        // Pulled into locals before the branches. A nullable *property* stops
        // being provably non-null the moment anything else could have run, so
        // `posterImage.LinkUrl` inside the arm that just tested it is a
        // CS8604 waiting to happen — and this project builds warnings as errors.
        var posterSrc = posterImage is null ? null : Enc(posterImage.Src);
        var posterHref = posterImage?.LinkUrl is { } href ? Enc(href) : null;

        // `display:block` on the image kills the few pixels of descender space
        // an inline image gets, which otherwise shows as a pale stripe under
        // the design. `width:100%;height:auto` is what makes it scale on a
        // phone; the width attribute is the fallback for clients that ignore
        // the style.
        //
        // Three shapes, not two, and the difference is whether the image has to
        // be fetched.
        //
        // A fetched image gets wrapped in its own link and a text link under it.
        // That is the cost of not embedding: a client that blocks remote images
        // shows the alt text and nothing else, and the poster *is* the message.
        // Gmail, Apple Mail and Outlook.com load remote images by default;
        // Outlook desktop does not for an unknown sender. One line of link means
        // that reader is one click from the design instead of looking at an
        // empty box wondering what they were sent.
        //
        // An embedded image gets neither. It is already in the message, so there
        // is nothing to fetch and nothing to fail — and its src is a `cid:`,
        // which is not an address a browser can open. Linking to it would give
        // the reader a "Not showing?" link that does nothing, which is worse
        // than no link at all.
        var posterBlock = posterSrc is null
            ? $"""
                       <tr>
                         <td align="center" style="padding:32px 24px;background:#f1f5f9;color:#475569;font-size:14px;">
                           {poster}
                         </td>
                       </tr>
               """
            : posterHref is null
            ? $"""
                       <tr>
                         <td align="center" style="padding:0;">
                           <img src="{posterSrc}"
                                alt="{poster}"
                                width="{BodyWidth}"
                                style="width:100%;max-width:{BodyWidth}px;height:auto;display:block;border:0;outline:none;text-decoration:none;">
                         </td>
                       </tr>
               """
            : $"""
                       <tr>
                         <td align="center" style="padding:0;">
                           <a href="{posterHref}" style="display:block;text-decoration:none;border:0;">
                             <img src="{posterSrc}"
                                  alt="{poster}"
                                  width="{BodyWidth}"
                                  style="width:100%;max-width:{BodyWidth}px;height:auto;display:block;border:0;outline:none;text-decoration:none;">
                           </a>
                         </td>
                       </tr>
                       <tr>
                         <td align="center" style="padding:10px 24px 0;font-family:Helvetica,Arial,sans-serif;">
                           <a href="{posterHref}"
                              style="font-size:12px;color:#64748b;text-decoration:underline;">
                             Not showing? View {poster}
                           </a>
                         </td>
                       </tr>
               """;

        var videoBlock = BuildVideoBlock(videos);

        return $"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <title>{campaign}</title>
              </head>
              <body style="margin:0;padding:0;background:#f1f5f9;">
                <!-- Full-width wrapper. Centring a fixed-width table inside a
                     100% table is the only centring Outlook honours. -->
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0"
                       style="background:#f1f5f9;margin:0;padding:0;">
                  <tr>
                    <td align="center" style="padding:24px 12px;">

                      <table role="presentation" width="{BodyWidth}" cellpadding="0" cellspacing="0" border="0"
                             style="width:100%;max-width:{BodyWidth}px;background:#ffffff;border-radius:12px;overflow:hidden;">

                        <tr>
                          <td style="padding:24px 24px 16px;font-family:Helvetica,Arial,sans-serif;">
                            <p style="margin:0 0 4px;font-size:18px;font-weight:600;color:#0f172a;">{campaign}</p>
                            <p style="margin:0;font-size:13px;color:#64748b;">from {from}</p>
                          </td>
                        </tr>

            {posterBlock}
            {videoBlock}

                        <tr>
                          <td style="padding:20px 24px 24px;font-family:Helvetica,Arial,sans-serif;
                                     border-top:1px solid #e2e8f0;">
                            <p style="margin:0;font-size:11px;line-height:16px;color:#94a3b8;">
                              Sent by {from}. If this reached you in error, please ignore it.{UnsubscribeHtml(unsubscribeUrl, from)}
                            </p>
                          </td>
                        </tr>

                      </table>

                    </td>
                  </tr>
                </table>
              </body>
            </html>
            """;
    }

    /// <summary>
    /// The videos, below the poster.
    /// </summary>
    /// <remarks>
    /// A centred thumbnail with the play affordance drawn into the caption,
    /// matching the poster's alignment rather than hugging the left edge as it
    /// did before. The thumbnail is deliberately narrower than the poster:
    /// equal width would read as a second poster rather than as a clip.
    ///
    /// No <c>&lt;video&gt;</c>: no mail client plays it. What works everywhere
    /// is a still that links out, which is what this is.
    /// </remarks>
    private static string BuildVideoBlock(IReadOnlyList<VideoEmailLink> videos)
    {
        if (videos.Count == 0) return string.Empty;

        var items = new StringBuilder();
        foreach (var v in videos)
        {
            var name = Enc(v.Name);
            var url = Enc(v.Url);

            var thumb = v.ThumbnailUrl is null
                ? string.Empty
                : $"""
                             <a href="{url}" style="text-decoration:none;">
                               <img src="{Enc(v.ThumbnailUrl)}" alt="{name}" width="280"
                                    style="width:100%;max-width:280px;height:auto;display:block;
                                           margin:0 auto 10px;border-radius:8px;border:0;">
                             </a>
                   """;

            items.Append($"""
                            <tr>
                              <td align="center" style="padding:0 24px 20px;font-family:Helvetica,Arial,sans-serif;">
                    {thumb}
                                <a href="{url}"
                                   style="display:inline-block;padding:10px 20px;background:#0e7490;color:#ffffff;
                                          font-size:14px;font-weight:600;border-radius:6px;text-decoration:none;">
                                  ▶&nbsp; Watch {name}
                                </a>
                              </td>
                            </tr>
                """);
        }

        var plural = videos.Count == 1 ? "" : "s";
        return $"""
                        <tr>
                          <td style="padding:24px 24px 12px;font-family:Helvetica,Arial,sans-serif;">
                            <p style="margin:0;font-size:13px;font-weight:600;color:#0f172a;">
                              {videos.Count} video{plural} included
                            </p>
                          </td>
                        </tr>
            {items}
                        <tr>
                          <td align="center" style="padding:0 24px 8px;font-family:Helvetica,Arial,sans-serif;">
                            <p style="margin:0;font-size:11px;color:#94a3b8;">
                              Video links expire in 48 hours.
                            </p>
                          </td>
                        </tr>
            """;
    }

    /// <summary>
    /// The plain-text alternative.
    /// </summary>
    /// <remarks>
    /// Not decoration. A message with no text part is markedly more likely to
    /// be filtered as spam, and the poster is an image — so without this, a
    /// text-only reader gets nothing at all.
    /// </remarks>
    /// <param name="posterImageUrl">
    /// Where the design is served from, or null when there is no render.
    /// </param>
    public static string BuildText(
        string campaignName,
        string posterName,
        string pharmacy,
        string? posterImageUrl,
        IReadOnlyList<VideoEmailLink> videos,
        string? unsubscribeUrl = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(campaignName);
        sb.AppendLine($"from {pharmacy}");
        sb.AppendLine();
        sb.AppendLine($"This email contains a poster: {posterName}");

        // The address, not just the advice to turn images on. A plain-text
        // reader has no images to turn on, and since the poster stopped being
        // embedded this line is the only way they can reach it at all.
        if (!string.IsNullOrWhiteSpace(posterImageUrl))
        {
            sb.AppendLine($"  {posterImageUrl}");
            sb.AppendLine("If it does not appear in your email app, open the link above.");
        }
        else
        {
            sb.AppendLine("If you cannot see it, turn on images in your email app.");
        }

        if (videos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{videos.Count} video{(videos.Count == 1 ? "" : "s")} included:");
            foreach (var v in videos)
            {
                sb.AppendLine($"  {v.Name}");
                sb.AppendLine($"  {v.Url}");
            }
            sb.AppendLine();
            sb.AppendLine("Video links expire in 48 hours.");
        }

        sb.AppendLine();
        sb.AppendLine($"Sent by {pharmacy}. If this reached you in error, please ignore it.");
        if (!string.IsNullOrWhiteSpace(unsubscribeUrl))
            sb.AppendLine($"Stop emails from {pharmacy}: {unsubscribeUrl}");
        return sb.ToString();
    }

    /// <summary>
    /// The footer's way out (security review R2-06). Every marketing email
    /// carries one; <paramref name="url"/> is normally
    /// <see cref="Messaging.UnsubscribeLinks.Placeholder"/>, replaced per
    /// recipient as the message is sent.
    /// </summary>
    private static string UnsubscribeHtml(string? url, string encodedPharmacy) =>
        string.IsNullOrWhiteSpace(url)
            ? string.Empty
            : $"""
               <br>
                              <a href="{Enc(url)}" style="color:#64748b;text-decoration:underline;">Unsubscribe</a>
                              from emails sent by {encodedPharmacy}.
               """;

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
