namespace Acme.Domain.Social;

/// <summary>
/// The state of a publish request as a whole.
/// </summary>
/// <remarks>
/// Persisted; do not renumber.
/// </remarks>
public enum PublishStatus
{
    /// <summary>Composed but not yet committed to a time. Editable.</summary>
    Draft = 0,

    /// <summary>Due at a future instant, waiting for the worker.</summary>
    Scheduled = 1,

    /// <summary>Claimed by a worker and in flight.</summary>
    Publishing = 2,

    /// <summary>Every target succeeded.</summary>
    Published = 3,

    /// <summary>
    /// Some targets succeeded and some did not.
    /// </summary>
    /// <remarks>
    /// A distinct state rather than a flavour of Failed, because the two need
    /// opposite handling. Retrying a Failed post retries everything; retrying
    /// this one must skip what already went out, or the pharmacy's Facebook
    /// followers see the same poster twice.
    /// </remarks>
    PartiallyPublished = 4,

    /// <summary>Every target failed.</summary>
    Failed = 5,

    /// <summary>Cancelled before it ran. Terminal.</summary>
    Cancelled = 6,
}

/// <summary>The state of one platform within a publish request.</summary>
public enum PublishTargetStatus
{
    Pending = 0,
    Publishing = 1,
    Published = 2,
    Failed = 3,

    /// <summary>
    /// Not attempted, and not an error: the media does not suit the platform,
    /// for example a video aimed at Google Business Profile.
    /// </summary>
    Skipped = 4,

    /// <summary>
    /// The platform may have published this; we could not confirm it.
    /// </summary>
    /// <remarks>
    /// A timeout after sending, an interrupted worker, or an answer with no
    /// post id. Never retried automatically — a second copy on a pharmacy's
    /// feed cannot be taken back — so it waits for a person to check and
    /// press Retry.
    /// </remarks>
    Uncertain = 5,
}

/// <summary>What the pharmacy is publishing.</summary>
public enum PublishSourceKind
{
    Poster = 0,
    Video = 1,
}

/// <summary>
/// Google Business Profile post types.
/// </summary>
/// <remarks>
/// GBP calls these "local post topic types". Only the ones that make sense for
/// a pharmacy are offered — the API also has Alert, which is reserved for
/// genuine emergencies and is not something to expose behind a dropdown.
/// </remarks>
public enum GbpPostType
{
    /// <summary>A standard "what's new" post.</summary>
    Update = 0,

    /// <summary>A promotion. Supports coupon code, terms and a redeem link.</summary>
    Offer = 1,

    /// <summary>Something with a start and end, e.g. a flu jab clinic.</summary>
    Event = 2,
}
