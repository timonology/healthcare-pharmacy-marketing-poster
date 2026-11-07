using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Subscriptions;
using Acme.Domain.Common;
using Acme.Domain.Videos;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Videos;

/// <summary>
/// Video-generation API surface. The .NET side owns the entity (tier
/// gating, ownership, monthly caps, deletion cleanup). The Python
/// ai-service worker owns the render pipeline — it polls Mongo for
/// Pending rows, runs the LangGraph agent, and mutates the same document
/// to Rendering → Ready.
/// </summary>
public sealed class VideoService(
    IVideoRepository videos,
    IUserRepository users,
    IVideoStorage storage,
    IBrandKitRepository brandKits,
    IRefineClassifier classifier,
    ILogger<VideoService> logger)
{
    public const string DefaultAspect = "9:16";
    public const int DefaultDurationSeconds = 15;

    /// <summary>
    /// The pharmacy identity to stamp on an attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The precedence rule is <see cref="Domain.BrandKit.PharmacyBrand.Resolve"/>
    /// and lives nowhere else — the same call the campaign sender name and the
    /// poster tokens make. Video previously had no branding at all, and the
    /// design note for it proposed the opposite precedence to the one campaign
    /// email ships, which would have named the same pharmacy two different
    /// things in one campaign.
    /// </para>
    /// <para>
    /// A brand kit that cannot be read is not worth failing a video over. The
    /// profile alone still produces a name, an address and a booking link;
    /// losing the logo and the colours degrades the end card rather than the
    /// render.
    /// </para>
    /// </remarks>
    private async Task<Domain.BrandKit.PharmacyBrand> ResolveBrandAsync(
        Domain.Users.User user, CancellationToken ct)
    {
        Domain.BrandKit.BrandKit? kit = null;
        try
        {
            kit = await brandKits.FindByOwnerAsync(user.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read the brand kit for {OwnerId} while branding a video. "
                + "Falling back to the pharmacy profile alone.", user.Id);
        }

        var brand = Domain.BrandKit.PharmacyBrand.Resolve(user.Profile, kit);

        if (!brand.IsUsable)
        {
            logger.LogInformation(
                "Video for {OwnerId} will render unbranded — neither the pharmacy "
                + "profile nor the brand kit has a name.", user.Id);
        }

        return brand;
    }

    public Task<IReadOnlyList<VideoDto>> ListAsync(string ownerId, CancellationToken ct) =>
        ListAsync(ownerId, ListWindow.Default, ct);

    /// <summary>The Videos page, sorted and ranged on creation date in the database.</summary>
    public async Task<IReadOnlyList<VideoDto>> ListAsync(string ownerId, ListWindow window, CancellationToken ct)
    {
        var items = await videos.ListAsync(ownerId, window, ct);
        var dtos = new List<VideoDto>(items.Count);
        foreach (var v in items) dtos.Add(await ToDtoAsync(v, ct));
        return dtos;
    }

    public async Task<Result<VideoDto>> GetAsync(string id, string ownerId, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<VideoDto>.NotFound("Video not found.");
        return Result<VideoDto>.Success(await ToDtoAsync(v, ct));
    }

    public async Task<Result<VideoDto>> CreateAsync(
        string ownerId,
        CreateVideoRequest request,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<VideoDto>.NotFound("User not found.");

        if (await EnforceVideoLimit(user, ownerId, ct) is { } quotaFailure)
            return quotaFailure;

        // Ai imagery is Pro-only. Keeps parity with the previous
        // aiVideoEnabled gate — the plan flag is the source of truth.
        var plan = Plans.For(user.Tier);
        if (request.VisualStyle == VideoVisualStyle.AiImagery && !plan.AiVideoEnabled)
            return Result<VideoDto>.Forbidden(
                $"AI imagery is a Pro feature. You're on {plan.DisplayName}. Upgrade to use AI-generated visuals.");

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? Shorten(request.Topic, 60)
            : request.Name!.Trim();

        // Checked here rather than only in the domain so the pharmacist gets the
        // sentence rather than the exception's. The domain enforces it too — see
        // Video.VideoImageLimit — because a rule that only exists at the edge is
        // a rule the next caller can walk around.
        if (VideoImagePolicy.WhyCountRefused(request.Images?.Count ?? 0) is { } tooMany)
            return Result<VideoDto>.Invalid(tooMany);

        // Every supplied blob key has to belong to this owner. See MapImages:
        // the key is a permission, not a label.
        var images = MapImages(request.Images, ownerId);
        if (images.Kind != ResultKind.Success)
            return Result<VideoDto>.Invalid(images.Error!);

        Video video;
        try
        {
            video = Video.Create(
                ownerId,
                name,
                request.Topic,
                request.ScriptSource,
                request.VisualStyle,
                request.Description,
                request.CustomScript,
                request.VoiceId,
                request.StyleTone,
                images.Value);
        }
        catch (DomainException ex) { return Result<VideoDto>.Invalid(ex.Message); }

        video.StampBrand(await ResolveBrandAsync(user, ct));

        await videos.AddAsync(video, ct);
        logger.LogInformation(
            "Video queued id={VideoId} owner={OwnerId} topic={Topic} scriptSource={ScriptSource} visualStyle={VisualStyle}",
            video.Id, video.OwnerId, video.Topic, video.ScriptSource, video.VisualStyle);
        return Result<VideoDto>.Success(await ToDtoAsync(video, ct));
    }

    /// <summary>
    /// Works out what a change would do, and records the plan without running it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things have to be true before a refine is offered, and they fail for
    /// different reasons: the instruction has to be routable, and the stage it
    /// routes to has to be open. Both come back as <c>CanRun: false</c> with a
    /// sentence saying which — "we could not tell what you wanted changed" and
    /// "that needs a part of the service we have not opened up" are different
    /// problems with different next steps.
    /// </para>
    /// <para>
    /// <b>Enablement is decided here, not by the classifier.</b> The Python side
    /// suggests a stage; <see cref="RefineStages"/> decides whether it may be
    /// entered. A classifier that answered "researcher" for every sentence
    /// could waste a model call and nothing else.
    /// </para>
    /// </remarks>
    public async Task<Result<RefinePlanDto>> PreviewRefineAsync(
        string id, string ownerId, string instruction, string bearerToken,
        CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<RefinePlanDto>.NotFound("Video not found.");

        if (v.Status != VideoStatus.Ready)
        {
            // Not an error — the pharmacist asked a reasonable question at an
            // awkward moment. Answered as a plan that cannot run, so the screen
            // shows a sentence rather than a failure.
            return Result<RefinePlanDto>.Success(new RefinePlanDto(
                CanRun: false, Stage: null, Summary: string.Empty,
                Reason: v.Status switch
                {
                    VideoStatus.Rendering =>
                        "This video is being made right now. You can ask for "
                        + "changes once it has finished.",
                    VideoStatus.Pending =>
                        "This video is waiting to be made. Give it a moment.",
                    _ =>
                        "This video did not finish, so there is nothing to "
                        + "change yet. Retry it first.",
                },
                Free: true));
        }

        // Before the classifier, not after. ai-service refuses an empty or
        // over-long instruction with a 422, and a 422 can only reach the
        // pharmacist as "we could not work out what to change" — which tells
        // them the product misunderstood them when in fact nobody looked.
        //
        // A plan that cannot run rather than a 400, for the same reason the
        // status check above is one: the pharmacist asked a fair question and
        // deserves a sentence, not a failure. `Stage: null` leaves it re-askable
        // in the browser, which is right — shortening the text is the fix.
        if (RefinePlan.Unplannable(instruction) is { } refusal)
        {
            return Result<RefinePlanDto>.Success(new RefinePlanDto(
                CanRun: false, Stage: null, Summary: string.Empty,
                Reason: refusal, Free: true));
        }

        // Trimmed once, here, and used for everything after.
        //
        // The check above measures the trimmed length; ai-service's
        // `Field(max_length=...)` measures whatever it is sent. Passing the raw
        // string on would reopen the hole the check just closed in a narrow
        // band — a 1000-character instruction with trailing whitespace passes
        // here and 422s there — and would also mean the classifier judged one
        // string while the stored plan recorded a slightly different one.
        var text = instruction.Trim();

        var routing = await classifier.ClassifyAsync(text, bearerToken, ct);

        if (routing.Stage is not { } stage)
        {
            return Result<RefinePlanDto>.Success(new RefinePlanDto(
                CanRun: false, Stage: null, Summary: string.Empty,
                Reason: routing.Reason, Free: true));
        }

        // <b>A request about the footage is a shot edit, not a refusal.</b>
        //
        // The classifier routes "remove the man at the end, put an elderly
        // couple in a park there" to Visuals, which `IsEnabled` calls closed —
        // and for an open-ended "find me different footage" on an AI-imagery
        // video that is right, because re-entering there regenerates every beat
        // through Replicate.
        //
        // On a stock-footage video it is not right. Re-filling the moments the
        // pharmacist named re-searches Pexels, which is free, and reuses the
        // stored script and narration, so nothing is spent and the video does
        // not change length. The earlier caution against opening this lane was
        // about opening it *by accident* while widening `Spends`; this opens it
        // deliberately, for the one operation that does not cost anything.
        var editsShots =
            stage == RefineStage.Visuals && RefineStages.ShotsMayBeRefilled(v.VisualStyle);

        if (!RefineStages.IsEnabled(stage) && !editsShots)
        {
            logger.LogInformation(
                "Refine for video {VideoId} routed to {Stage}, which is not open. "
                + "Instruction: {Instruction}", v.Id, stage, text);

            return Result<RefinePlanDto>.Success(new RefinePlanDto(
                CanRun: false, Stage: stage.ToString(), Summary: string.Empty,
                Reason: RefineStages.ExplainUnavailable(stage),
                Free: !RefineStages.Spends(stage)));
        }

        RefinePlan plan;
        try
        {
            plan = editsShots
                ? RefinePlan.ForShotEdits(text, DateTime.UtcNow)
                : RefinePlan.Create(
                    text, stage, RefineStages.Describe(stage), DateTime.UtcNow);
        }
        catch (DomainException ex) { return Result<RefinePlanDto>.Invalid(ex.Message); }

        // Stored now, so confirmation acts on the plan that was shown rather
        // than on a second classification that might answer differently.
        try { v.ProposeRefine(plan); }
        catch (DomainException ex) { return Result<RefinePlanDto>.Invalid(ex.Message); }

        await videos.UpdateAsync(v, ct);

        return Result<RefinePlanDto>.Success(new RefinePlanDto(
            CanRun: true, Stage: stage.ToString(), Summary: plan.Summary,
            // `Spends(Visuals)` is true, and for a shot edit it is the wrong
            // answer: this route calls no generation provider at all. Telling a
            // pharmacist a free change will cost them is how a feature goes
            // unused.
            Reason: routing.Reason, Free: editsShots || !RefineStages.Spends(stage)));
    }

    /// <summary>Runs the plan the pharmacist was shown and agreed to.</summary>
    /// <remarks>
    /// <para>
    /// The instruction is sent again and must match the stored plan. That is
    /// what makes the confirmation mean something: without it, a request could
    /// be assembled that carries one sentence past a plan made for another, and
    /// the difference between those two sentences is precisely the difference
    /// between a free re-cut and a billable rewrite.
    /// </para>
    /// <para>
    /// Every check is made again rather than trusted from the preview. The
    /// stage's enablement in particular, because a preview made before a lane
    /// was closed must not be confirmable after.
    /// </para>
    /// </remarks>
    public async Task<Result<VideoDto>> ConfirmRefineAsync(
        string id, string ownerId, string instruction, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<VideoDto>.NotFound("Video not found.");

        if (v.Refine is not { } plan)
            return Result<VideoDto>.Invalid(
                "Ask for the change again — we no longer have what it was going to do.");

        if (!plan.Describes(instruction))
            return Result<VideoDto>.Invalid(
                "That is not the change we described. Ask again so we can tell "
                + "you what it will do.");

        // An already-agreed plan cannot be agreed to again.
        //
        // Without this, the confirm request is replayable: the plan survives the
        // render that used it, so once the video is Ready again the same request
        // re-queues the same work with no fresh preview. Harmless while only the
        // free lane is open, and a way to re-run billable work on repeat the
        // moment one is not. Each run needs its own showing.
        if (plan.Confirmed)
            return Result<VideoDto>.Invalid(
                "That change has already been made. Ask for it again if you "
                + "would like another go.");

        if (plan.HasExpired(DateTime.UtcNow))
            return Result<VideoDto>.Invalid(
                "This has been sitting a while and the video may have moved on. "
                + "Ask for the change again.");

        // A shot-edit plan re-enters at Visuals, which `IsEnabled` calls closed.
        // It was allowed when the plan was made and shown; refusing it now would
        // mean offering a change and then declining to make it.
        var shotEdit = plan.EditsShots && RefineStages.ShotsMayBeRefilled(v.VisualStyle);
        if (!RefineStages.IsEnabled(plan.Stage) && !shotEdit)
            return Result<VideoDto>.Invalid(RefineStages.ExplainUnavailable(plan.Stage));

        try { v.StartRefine(plan); }
        catch (DomainException ex) { return Result<VideoDto>.Invalid(ex.Message); }

        // The brand is re-resolved for the same reason Retry does it: this is a
        // new attempt, and a pharmacy that corrected its name should see the
        // correction on the recut.
        var owner = await users.FindByIdAsync(ownerId, ct);
        if (owner is not null) v.StampBrand(await ResolveBrandAsync(owner, ct));

        await videos.UpdateAsync(v, ct);
        logger.LogInformation(
            "Refine queued id={VideoId} owner={OwnerId} stage={Stage}",
            v.Id, v.OwnerId, plan.Stage);

        return Result<VideoDto>.Success(await ToDtoAsync(v, ct));
    }

    /// <summary>
    /// Adds pictures to a finished video and re-cuts it with them in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own door, not a refine instruction.</b> The refine flow asks a
    /// model which stage a sentence means, shows the pharmacist what that would
    /// do, and waits for them to type it back. None of that applies here: a
    /// photograph names the stage by being a photograph, there is nothing to
    /// classify and nothing to mistake, and asking somebody to describe in
    /// words the picture they just attached is a step that buys nothing.
    /// </para>
    /// <para>
    /// So there is no preview and no confirm — and no model call either, which
    /// is part of why this lane is free.
    /// </para>
    /// </remarks>
    public async Task<Result<VideoDto>> AddPicturesAsync(
        string id, string ownerId, IReadOnlyList<VideoImageDto>? pictures, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<VideoDto>.NotFound("Video not found.");

        // Checked before the count, because a video that can never take
        // pictures should say so rather than quibble about how many.
        if (!RefineStages.PicturesMayBePinned(v.VisualStyle))
            return Result<VideoDto>.Invalid(RefineStages.WhyPicturesRefused(v.VisualStyle));

        // The same ownership check the create path makes, for the same reason:
        // the key comes from the request body, the worker downloads it, and
        // DeleteAsync deletes it. See MapImages.
        var mapped = MapImages(pictures, ownerId);
        if (mapped.Kind != ResultKind.Success)
            return Result<VideoDto>.Invalid(mapped.Error!);

        var incoming = mapped.Value ?? Array.Empty<VideoImage>();
        if (incoming.Count == 0)
            return Result<VideoDto>.Invalid("Choose at least one picture to add.");

        try { v.AddPictures(incoming, DateTime.UtcNow); }
        catch (DomainException ex) { return Result<VideoDto>.Invalid(ex.Message); }

        // Re-resolved as Retry and ConfirmRefine both do: this is a new attempt,
        // and a pharmacy that corrected its name should see the correction.
        var owner = await users.FindByIdAsync(ownerId, ct);
        if (owner is not null) v.StampBrand(await ResolveBrandAsync(owner, ct));

        await videos.UpdateAsync(v, ct);
        logger.LogInformation(
            "Pictures added id={VideoId} owner={OwnerId} added={Added} total={Total}",
            v.Id, v.OwnerId, incoming.Count, v.Images.Count);

        return Result<VideoDto>.Success(await ToDtoAsync(v, ct));
    }

    /// <summary>Every shot of a finished video, for the refine dialog's picker.</summary>
    public async Task<Result<IReadOnlyList<VideoShotDto>>> ListShotsAsync(
        string id, string ownerId, CancellationToken ct)
    {
        var source = await videos.FindShotSourceAsync(id, ct);
        if (source is null || source.OwnerId != ownerId)
            return Result<IReadOnlyList<VideoShotDto>>.NotFound("Video not found.");
        return Result<IReadOnlyList<VideoShotDto>>.Success(VideoShots.Build(source));
    }

    /// <summary>The still of one shot, as rendered.</summary>
    public async Task<Result<VideoBlobStream>> OpenShotThumbnailAsync(
        string id, string ownerId, int index, CancellationToken ct)
    {
        var source = await videos.FindShotSourceAsync(id, ct);
        if (source is null || source.OwnerId != ownerId)
            return Result<VideoBlobStream>.NotFound("Video not found.");

        var key = VideoShots.ThumbnailKey(source, id, index);
        if (key is null)
            return Result<VideoBlobStream>.NotFound("No still for that shot.");

        var stream = await storage.OpenReadAsync(key, ct);
        return stream is null
            ? Result<VideoBlobStream>.NotFound("No still for that shot.")
            : Result<VideoBlobStream>.Success(stream);
    }

    /// <summary>Re-cuts a finished video with new footage at the shots the pharmacist tapped.</summary>
    /// <remarks>
    /// <para>
    /// <b>The shot is theirs to choose, and this never second-guesses it.</b>
    /// Each index is checked against the video's own shot list and nothing
    /// else: not against what the shot is said to show. The typed route used to
    /// infer the shot from a sentence and refused real requests on the strength
    /// of a stock-library title.
    /// </para>
    /// <para>
    /// No preview and no confirm, as with <see cref="AddPicturesAsync"/>:
    /// nothing was interpreted, so there is nothing to check. Free: stock
    /// search plus the stored narration, the same lane as any shot edit.
    /// </para>
    /// </remarks>
    public async Task<Result<VideoDto>> ChangeShotsAsync(
        string id, string ownerId, IReadOnlyList<ShotChoiceDto>? shots, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<VideoDto>.NotFound("Video not found.");

        if (!RefineStages.ShotsMayBeRefilled(v.VisualStyle))
            return Result<VideoDto>.Invalid(
                "The shots in an AI imagery video are pictures your plan paid to "
                + "generate, so changing one means generating it again. Make a new "
                + "video to change them.");

        var source = await videos.FindShotSourceAsync(id, ct);
        // The moments the tiles show — checked by membership, since a moment the
        // editor skipped leaves a gap in the numbering (see ShotChoice.Create).
        var available = source is null
            ? new HashSet<int>()
            : VideoShots.Build(source).Select(s => s.Index).ToHashSet();
        if (available.Count == 0)
            return Result<VideoDto>.Invalid(
                "We do not have a record of this video's shots, so we cannot change "
                + "one. Creating a new video is the way to do it.");

        RefinePlan plan;
        try
        {
            var choices = (shots ?? Array.Empty<ShotChoiceDto>())
                .Select(s => ShotChoice.Create(s.Index, s.Show, available))
                .ToList();
            plan = RefinePlan.ForChosenShots(choices, DateTime.UtcNow);
            v.StartRefine(plan);
        }
        catch (DomainException ex) { return Result<VideoDto>.Invalid(ex.Message); }

        // Re-resolved as Retry, ConfirmRefine and AddPictures all do.
        var owner = await users.FindByIdAsync(ownerId, ct);
        if (owner is not null) v.StampBrand(await ResolveBrandAsync(owner, ct));

        await videos.UpdateAsync(v, ct);
        logger.LogInformation(
            "Shots chosen id={VideoId} owner={OwnerId} shots={Shots}",
            v.Id, v.OwnerId, string.Join(",", plan.ChosenShots.Select(c => c.Index)));

        return Result<VideoDto>.Success(await ToDtoAsync(v, ct));
    }

    public async Task<Result<VideoDto>> RetryAsync(string id, string ownerId, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<VideoDto>.NotFound("Video not found.");

        try { v.Retry(DateTime.UtcNow); }
        catch (DomainException ex) { return Result<VideoDto>.Invalid(ex.Message); }

        // Re-resolved rather than reused. Retry is the moment a pharmacy has
        // just fixed something and asked for another go, and a misspelt name is
        // one of the likelier reasons to press it. Keeping the old stamp would
        // reproduce the mistake and give them no way to correct it.
        var owner = await users.FindByIdAsync(ownerId, ct);
        if (owner is not null) v.StampBrand(await ResolveBrandAsync(owner, ct));

        await videos.UpdateAsync(v, ct);
        logger.LogInformation("Video retry queued id={VideoId} owner={OwnerId}", v.Id, v.OwnerId);
        return Result<VideoDto>.Success(await ToDtoAsync(v, ct));
    }

    /// <summary>Owner-authenticated streaming. Returns null if the video
    /// doesn't belong to the caller or hasn't rendered yet.</summary>
    public async Task<Result<VideoBlobStream>> OpenDownloadAsync(
        string id, string ownerId, bool thumbnail, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<VideoBlobStream>.NotFound("Video not found.");
        var key = thumbnail ? v.ThumbnailBlobKey : v.BlobKey;
        if (string.IsNullOrWhiteSpace(key))
            return Result<VideoBlobStream>.NotFound(
                thumbnail ? "Thumbnail not ready yet." : "Video not ready yet.");

        var stream = await storage.OpenReadAsync(key, ct);
        if (stream is null)
            return Result<VideoBlobStream>.NotFound(
                $"Blob '{key}' not found in storage. The render may have failed to upload.");
        return Result<VideoBlobStream>.Success(stream);
    }

    /// <summary>Public streaming with an HMAC-signed token. The .NET
    /// CampaignService embeds this URL in outgoing emails so recipients
    /// (no auth cookie) can still play the video. Token binds the video
    /// id + expiry so it can't be forwarded to view other people's videos
    /// or used after it expires.</summary>
    public async Task<Result<VideoBlobStream>> OpenPublicDownloadAsync(
        string id, string token, bool thumbnail, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null) return Result<VideoBlobStream>.NotFound("Video not found.");

        if (!VideoDownloadToken.TryValidate(token, id, thumbnail, out var reason))
            return Result<VideoBlobStream>.Forbidden(reason ?? "Invalid or expired link.");

        var key = thumbnail ? v.ThumbnailBlobKey : v.BlobKey;
        if (string.IsNullOrWhiteSpace(key))
            return Result<VideoBlobStream>.NotFound(
                thumbnail ? "Thumbnail not available." : "Video not available.");

        var stream = await storage.OpenReadAsync(key, ct);
        if (stream is null)
            return Result<VideoBlobStream>.NotFound(
                $"Blob '{key}' not found. The video may have been deleted.");

        // A strong validator, so a screen's browser can keep the file in its
        // HTTP cache. Browsers cache a video fetched in ranges only when the
        // response carries one; without it every loop of every screen pulls the
        // whole MP4 again. Moves whenever the video is updated — a re-cut
        // writes a new file under the same key, and a stale tag would serve
        // the old cut.
        var tag = $"\"{v.Id}-{v.UpdatedAtUtc.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture)}"
            + (thumbnail ? "-t\"" : "\"");
        return Result<VideoBlobStream>.Success(stream with { ETag = tag });
    }

    public static string BuildPublicToken(string videoId, bool thumbnail, TimeSpan validFor) =>
        VideoDownloadToken.Sign(videoId, thumbnail, DateTimeOffset.UtcNow.Add(validFor));

    public async Task<Result<bool>> DeleteAsync(string id, string ownerId, CancellationToken ct)
    {
        var v = await videos.FindByIdAsync(id, ct);
        if (v is null || v.OwnerId != ownerId)
            return Result<bool>.NotFound("Video not found.");

        try
        {
            // The pictures the pharmacist attached, which live outside the
            // video's own prefix.
            //
            // They are uploaded *before* the video exists, so they cannot be
            // stored under `videos/{ownerId}/{videoId}/` — there is no id yet.
            // That puts them outside the sweep below, which is exactly the
            // silent-leak shape that sweep was written to end, so they are named
            // here explicitly. One of the two had to know; better the one that
            // can see the record.
            //
            // Only the uploads. A brand logo or a published poster reused as
            // B-roll belongs to another feature and is still on screen there —
            // deleting a video that borrowed the logo must not take the
            // pharmacy's logo with it.
            foreach (var image in v.Images)
            {
                if (!VideoImagePolicy.IsOwnAttachment(image.BlobKey, v.OwnerId)) continue;

                try { await storage.DeleteAsync(image.BlobKey, ct); }
                catch (Exception ex)
                {
                    // A picture that will not delete is not a reason to leave
                    // the video undeleted. Logged so the leak is visible.
                    logger.LogWarning(ex,
                        "Could not delete attached image {BlobKey} for video {VideoId}.",
                        image.BlobKey, v.Id);
                }
            }

            // By prefix, not by the two keys this method happens to know about.
            //
            // The render pipeline keeps more than the MP4 and its thumbnail now
            // — the narration audio, and the stills on an AI-imagery job — and
            // it will keep more as refinement grows. Naming keys here means
            // every artifact added later leaks silently, for every video ever
            // made, until somebody notices the storage bill.
            //
            // `videos/{ownerId}/{videoId}` covers the video's own `.mp4` and
            // `.jpg` as well as everything beneath `.../{videoId}/`. A video id
            // is a 32-character hex GUID, so ids are all the same length and one
            // can never be a prefix of another.
            await storage.DeleteByPrefixAsync($"videos/{v.OwnerId}/{v.Id}", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete blobs for video {VideoId}", id);
        }

        await videos.DeleteAsync(id, ct);
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Monthly plan allowance first, then purchased credits.
    ///
    /// Order matters: spending a credit while the free monthly allowance still
    /// has room would quietly burn something the customer paid extra for.
    /// Credits are only touched once the plan quota is genuinely exhausted.
    ///
    /// Returns null when the video may proceed. When a credit is spent, the
    /// caller has already been debited by the time this returns — the user is
    /// saved here so a later failure in <c>CreateAsync</c> can't hand back a
    /// free video.
    /// </summary>
    private async Task<Result<VideoDto>?> EnforceVideoLimit(
        Acme.Domain.Users.User user, string ownerId, CancellationToken ct)
    {
        var plan = Plans.For(user.Tier);

        if (plan.MaxVideosPerMonth == Plans.Unlimited) return null;

        var used = plan.MaxVideosPerMonth == 0
            ? 0
            : await videos.CountByOwnerThisMonthAsync(ownerId, ct);

        var withinPlanAllowance = plan.MaxVideosPerMonth > 0 && used < plan.MaxVideosPerMonth;
        if (withinPlanAllowance) return null;

        // Allowance gone (or the plan has none at all) — fall back to credits.
        if (user.TryConsumeVideoCredit())
        {
            await users.UpdateAsync(user, ct);
            return null;
        }

        if (plan.MaxVideosPerMonth == 0)
            return Result<VideoDto>.Forbidden(
                $"Marketing videos aren't available on the {plan.DisplayName} plan. " +
                "Upgrade to Starter or Pro, or buy a video credit pack.");

        return Result<VideoDto>.Forbidden(
            $"You've used all {plan.MaxVideosPerMonth} videos on your {plan.DisplayName} plan this month. " +
            "Buy a credit pack to keep going, or upgrade your plan.");
    }

    private Task<VideoDto> ToDtoAsync(Video v, CancellationToken ct)
    {
        // Return API-relative URLs. The BFF's /api/videos/[id]/download and
        // .../thumbnail routes proxy the blob through the .NET side, which
        // reads it from Azurite/Blob directly. This avoids SAS URL fragility
        // (host mismatches, version headers, CORS) that broke in-app playback
        // in dev. The browser hits the BFF with its auth cookie, the BFF
        // proxies to the .NET API with a Bearer token, the .NET API streams
        // the blob bytes back.
        var downloadUrl = string.IsNullOrWhiteSpace(v.BlobKey)
            ? null
            : $"/api/videos/{v.Id}/download";
        var thumbnailUrl = string.IsNullOrWhiteSpace(v.ThumbnailBlobKey)
            ? null
            : $"/api/videos/{v.Id}/thumbnail";

        // Failed, or Rendering with a lapsed lease — the same rule Retry itself
        // applies, asked once here so the client does not have to reimplement it
        // from a lease it cannot see.
        var canRetry = v.Status == VideoStatus.Failed || v.IsStranded(DateTime.UtcNow);

        return Task.FromResult(new VideoDto(
            v.Id, v.OwnerId, v.Name, v.Topic,
            v.Status, v.ScriptSource, v.VisualStyle,
            v.Aspect, v.DurationSeconds,
            downloadUrl, thumbnailUrl,
            v.Note,
            v.Diagnostic,
            v.CreatedAtUtc, v.UpdatedAtUtc,
            canRetry,
            v.Images.Count));
    }

    /// <summary>
    /// Stores a picture a pharmacist attached, before any video exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed under the owner rather than under a video, because the upload
    /// happens while the dialog is open and the video has not been created — and
    /// may never be, if they change their mind. <see cref="DeleteAsync"/> removes
    /// the ones a video ended up using.
    /// </para>
    /// <para>
    /// <b>Known gap, and larger than it first reads.</b> A picture that never
    /// becomes a video is never referenced and never deleted. That is up to
    /// four orphans — 32MB — per abandoned dialog, plus one more every time
    /// somebody presses × on a thumbnail, since removing it from the dialog
    /// drops the reference and not the blob. Nothing sweeps
    /// <c>videos/{owner}/attachments/</c>, and any signed-in account can repeat
    /// it, including a Free one that cannot create a video at all.
    /// </para>
    /// <para>
    /// Left as a storage-lifecycle rule's job rather than a request handler's —
    /// an Azure lifecycle policy deleting blobs under that prefix older than a
    /// day costs nothing and catches the tab-close case that client-side
    /// cleanup never will. Noted here rather than solved silently, and the
    /// figure is written out because "one orphan of 8MB" is what this comment
    /// said before somebody counted.
    /// </para>
    /// </remarks>
    public async Task<Result<VideoImageDto>> UploadImageAsync(
        string ownerId,
        Stream content,
        string contentType,
        long lengthBytes,
        string? fileName,
        CancellationToken ct)
    {
        if (VideoImagePolicy.WhyFileRefused(contentType, lengthBytes) is { } refusal)
            return Result<VideoImageDto>.Invalid(refusal);

        // The extension comes from the content type, not from the file name.
        // A name is whatever the browser was handed; the type is what the
        // pipeline will actually try to decode.
        var extension = contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg",
        };

        var key = $"videos/{ownerId}/attachments/{Guid.NewGuid():N}{extension}";

        try
        {
            await storage.UploadAsync(key, content, contentType, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not store an attached image for {OwnerId}.", ownerId);
            return Result<VideoImageDto>.Invalid(
                "That image could not be saved. Try again in a moment.");
        }

        logger.LogInformation(
            "Stored attached image {BlobKey} for {OwnerId} ({Bytes} bytes, {ContentType}).",
            key, ownerId, lengthBytes, contentType);

        return Result<VideoImageDto>.Success(
            new VideoImageDto(key, "upload", SanitiseDescription(fileName)));
    }

    /// <summary>
    /// A file name, made safe to store and short enough to log.
    /// </summary>
    /// <remarks>
    /// Kept only as a label for the operator. It is never rendered to a patient
    /// and never used as a path — the key is a fresh GUID, so a name like
    /// <c>../../etc/passwd</c> decides nothing.
    /// </remarks>
    private static string? SanitiseDescription(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var trimmed = Path.GetFileNameWithoutExtension(fileName).Trim();
        return trimmed.Length == 0 ? null : trimmed[..Math.Min(trimmed.Length, 80)];
    }

    /// <summary>
    /// Turns the wire's pictures into domain ones, dropping anything unusable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unparseable source becomes <c>Upload</c> rather than a rejection: the
    /// source is a label for logging and for how the beat is described, not a
    /// permission, and refusing a whole video because one enum spelling drifted
    /// would trade a cosmetic problem for a functional one.
    /// </para>
    /// <para>
    /// <b>The blob key is a permission, and is checked as one.</b> It arrives in
    /// the request body, so it is whatever the caller typed — and a key is used
    /// twice: the worker downloads it, and <see cref="DeleteAsync"/> deletes it.
    /// A key belonging to somebody else would therefore render their picture
    /// into this pharmacy's video and destroy it when that video was deleted.
    /// Refused rather than dropped, because unlike a misspelled source there is
    /// no reading of this that is a harmless mistake worth continuing past.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The pictures, or empty. Empty rather than null because
    /// <see cref="Video.Create"/> and <c>VideoMapper.ToImageDocuments</c>
    /// already treat the two the same, and a nullable type argument on
    /// <see cref="Result{T}"/> buys nothing for it.
    /// </returns>
    private static Result<IReadOnlyList<VideoImage>> MapImages(
        IReadOnlyList<VideoImageDto>? images, string ownerId)
    {
        if (images is null || images.Count == 0)
            return Result<IReadOnlyList<VideoImage>>.Success(Array.Empty<VideoImage>());

        var mapped = new List<VideoImage>(images.Count);
        foreach (var i in images)
        {
            if (string.IsNullOrWhiteSpace(i.BlobKey)) continue;

            if (VideoImagePolicy.WhyKeyRefused(i.BlobKey, ownerId) is { } refusal)
                return Result<IReadOnlyList<VideoImage>>.Invalid(refusal);

            if (!Enum.TryParse<VideoImageSource>(i.Source, ignoreCase: true, out var source))
                source = VideoImageSource.Upload;

            mapped.Add(VideoImage.Create(
                i.BlobKey, source, i.Description, i.FocalX, i.FocalY,
                i.Zoom ?? VideoImage.ZoomDefault));
        }

        return Result<IReadOnlyList<VideoImage>>.Success(mapped);
    }

    private static string Shorten(string s, int max) =>
        string.IsNullOrWhiteSpace(s)
            ? "Untitled video"
            : (s.Length <= max ? s.Trim() : s[..max].Trim() + "…");
}

/// <summary>Storage adapter kept in Application layer so infrastructure can implement it.</summary>
public interface IVideoStorage
{
    Task DeleteAsync(string blobKey, CancellationToken ct);

    /// <summary>Stores a blob and returns its key.</summary>
    /// <remarks>
    /// <b>The .NET side uploads again, for one thing only.</b> Render artifacts
    /// are written by the Python worker and that has not changed — this is for
    /// the pictures a pharmacist attaches in the browser, which arrive here and
    /// nowhere near the worker. They are stored before the video exists so the
    /// dialog can show a thumbnail and let somebody remove one before a render
    /// is spent.
    /// </remarks>
    Task UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct);

    /// <summary>
    /// Removes every blob whose key starts with <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deleting by prefix so a new artifact cannot start leaking.</b> Delete
    /// used to name the two keys it knew about — the MP4 and the thumbnail — so
    /// anything the pipeline began storing later stayed in the container
    /// forever, for every video ever made, and nothing would say so. The render
    /// pipeline now keeps the narration audio and, on AI-imagery jobs, the
    /// stills, and it will keep more as refinement grows.
    /// </para>
    /// <para>
    /// Safe because a video id is a 32-character hex GUID: every id is the same
    /// length, so one can never be a prefix of another. The prefix
    /// <c>videos/{ownerId}/{videoId}</c> therefore matches that video's own
    /// <c>.mp4</c> and <c>.jpg</c> and everything beneath
    /// <c>videos/{ownerId}/{videoId}/</c>, and nothing belonging to anyone else.
    /// </para>
    /// </remarks>
    Task DeleteByPrefixAsync(string prefix, CancellationToken ct);
    Task<Uri?> GetReadUrlAsync(string blobKey, TimeSpan validFor, CancellationToken ct);

    /// <summary>Stream the blob directly. Used by the .NET API's
    /// /api/videos/{id}/download endpoint so browsers + emails never touch
    /// a SAS URL that might be blocked by CORS, wrong host, or Azurite
    /// version quirks in dev.</summary>
    Task<VideoBlobStream?> OpenReadAsync(string blobKey, CancellationToken ct);
}

/// <param name="ETag">
/// A strong validator for this exact file, quoted, or null when unknown. Lets
/// browsers cache ranged video and answer <c>If-Range</c> correctly.
/// </param>
public sealed record VideoBlobStream(Stream Content, string ContentType, long? Length, string? ETag = null);
