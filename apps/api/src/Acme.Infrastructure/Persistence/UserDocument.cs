using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

internal sealed class UserDocument
{
    [BsonId]
    public string Id { get; set; } = default!;

    [BsonElement("email")]
    public string Email { get; set; } = default!;

    [BsonElement("displayName")]
    public string DisplayName { get; set; } = default!;

    [BsonElement("passwordHash")]
    public string PasswordHash { get; set; } = default!;

    [BsonElement("tier")]
    [BsonDefaultValue(0)]
    public int Tier { get; set; }

    // Defaulted so the millions of documents written before roles existed
    // deserialise as active pharmacy users rather than throwing.
    [BsonElement("role")]
    [BsonDefaultValue(0)]
    public int Role { get; set; }

    [BsonElement("managedByAdminId")]
    public string? ManagedByAdminId { get; set; }

    // Defaulted so every account written before invitations existed
    // deserialises as Active — i.e. exactly as it behaved before.
    [BsonElement("accountStatus")]
    [BsonDefaultValue(0)]
    public int AccountStatus { get; set; }

    [BsonElement("parentPharmacyId")] public string? ParentPharmacyId { get; set; }

    [BsonElement("isActive")]
    [BsonDefaultValue(true)]
    public bool IsActive { get; set; } = true;

    [BsonElement("lastLoginAtUtc")]
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>
    /// When the address was confirmed. Absent on every account that predates
    /// verification, which deserialises to null — correctly meaning
    /// "not verified" rather than failing to load.
    /// </summary>
    [BsonElement("emailVerifiedAtUtc")]
    public DateTime? EmailVerifiedAtUtc { get; set; }

    /// <summary>Tokens issued before this are refused (R2-09). Absent on older rows: nothing refused.</summary>
    [BsonElement("sessionsValidAfterUtc")]
    [BsonIgnoreIfNull]
    public DateTime? SessionsValidAfterUtc { get; set; }

    /// <summary>
    /// When this pharmacy was granted head-office status, or absent.
    /// </summary>
    /// <remarks>
    /// Absent on every account that predates the grant, which deserialises to
    /// null — correctly meaning "not a head office". Existing groups are given
    /// the status by a backfill rather than by this default, because a pharmacy
    /// that already runs branches must not silently lose the ability to run them.
    /// </remarks>
    [BsonElement("headOfficeGrantedAtUtc")]
    public DateTime? HeadOfficeGrantedAtUtc { get; set; }

    [BsonElement("profile")]
    public PharmacyProfileDocument Profile { get; set; } = new();

    // Nullable + defaulted so documents written before billing existed
    // deserialise cleanly instead of throwing on a missing element.
    [BsonElement("billing")]
    [BsonIgnoreIfNull]
    public BillingProfileDocument? Billing { get; set; }

    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [BsonElement("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class BillingProfileDocument
{
    [BsonElement("stripeCustomerId")] public string? StripeCustomerId { get; set; }
    [BsonElement("stripeSubscriptionId")] public string? StripeSubscriptionId { get; set; }
    [BsonElement("status")] [BsonDefaultValue(0)] public int Status { get; set; }
    [BsonElement("currentPeriodEndUtc")] public DateTime? CurrentPeriodEndUtc { get; set; }
    [BsonElement("cancelAtPeriodEnd")] public bool CancelAtPeriodEnd { get; set; }
    [BsonElement("videoCredits")] [BsonDefaultValue(0)] public int VideoCredits { get; set; }
}

internal sealed class PharmacyProfileDocument
{
    [BsonElement("pharmacyName")] public string PharmacyName { get; set; } = string.Empty;
    [BsonElement("address")] public string Address { get; set; } = string.Empty;
    [BsonElement("postCode")] public string PostCode { get; set; } = string.Empty;
    [BsonElement("description")] public string Description { get; set; } = string.Empty;
    [BsonElement("contactName")] public string ContactName { get; set; } = string.Empty;
    [BsonElement("contactPhone")] public string ContactPhone { get; set; } = string.Empty;
    [BsonElement("sonarFCode")] public string? SonarFCode { get; set; }
    [BsonElement("onboardingCompleted")] public bool OnboardingCompleted { get; set; }

    // Absent on every profile saved before booking links existed, which is why
    // it is nullable rather than defaulted: no link is a real state, not a
    // missing value to be filled in.
    [BsonElement("bookingUrl")] public string? BookingUrl { get; set; }
}
