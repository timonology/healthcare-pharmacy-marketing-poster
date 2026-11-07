using System.Text.RegularExpressions;
using Acme.Domain.Common;

namespace Acme.Domain.Patients;

public sealed class Patient : Entity
{
    private static readonly Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string OwnerId { get; private set; } = default!;
    public string FullName { get; private set; } = default!;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string Notes { get; private set; } = string.Empty;
    public IReadOnlyList<string> GroupIds { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// When the pharmacy recorded this person's agreement to marketing email,
    /// and how (security review R2-06). Null when none is recorded.
    /// </summary>
    public DateTime? MarketingConsentAtUtc { get; private set; }

    public string? MarketingConsentSource { get; private set; }

    /// <summary>
    /// Records or clears consent. Recording again keeps the first date: the
    /// question a regulator asks is when it was given, not when the form was
    /// last saved.
    /// </summary>
    public void RecordMarketingConsent(bool given, string source)
    {
        if (given)
        {
            if (MarketingConsentAtUtc is not null) return;
            MarketingConsentAtUtc = DateTime.UtcNow;
            MarketingConsentSource = string.IsNullOrWhiteSpace(source) ? null : source.Trim()[..Math.Min(source.Trim().Length, 200)];
        }
        else
        {
            if (MarketingConsentAtUtc is null) return;
            MarketingConsentAtUtc = null;
            MarketingConsentSource = null;
        }
        Touch();
    }

    private Patient() { }

    public static Patient Create(
        string ownerId,
        string fullName,
        string? email,
        string? phone,
        string? notes,
        IEnumerable<string>? groupIds = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");

        ValidateFullName(fullName);
        var normalizedEmail = NormalizeEmail(email);
        var normalizedPhone = NormalizePhone(phone);
        ValidateContact(normalizedEmail, normalizedPhone);

        return new Patient
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            FullName = fullName.Trim(),
            Email = normalizedEmail,
            Phone = normalizedPhone,
            Notes = (notes ?? string.Empty).Trim(),
            GroupIds = NormalizeGroups(groupIds),
        };
    }

    public void Update(
        string fullName,
        string? email,
        string? phone,
        string? notes)
    {
        ValidateFullName(fullName);
        var normalizedEmail = NormalizeEmail(email);
        var normalizedPhone = NormalizePhone(phone);
        ValidateContact(normalizedEmail, normalizedPhone);

        FullName = fullName.Trim();
        Email = normalizedEmail;
        Phone = normalizedPhone;
        Notes = (notes ?? string.Empty).Trim();
        Touch();
    }

    public void AddToGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            throw new DomainException("Group id is required.");
        if (GroupIds.Contains(groupId)) return;
        GroupIds = GroupIds.Append(groupId).ToList();
        Touch();
    }

    public void RemoveFromGroup(string groupId)
    {
        if (!GroupIds.Contains(groupId)) return;
        GroupIds = GroupIds.Where(g => g != groupId).ToList();
        Touch();
    }

    public void ReplaceGroups(IEnumerable<string> groupIds)
    {
        GroupIds = NormalizeGroups(groupIds);
        Touch();
    }

    private static void ValidateFullName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Full name is required.");
        if (fullName.Length > 200)
            throw new DomainException("Full name must be 200 chars or fewer.");
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var trimmed = email.Trim().ToLowerInvariant();
        if (!EmailPattern.IsMatch(trimmed))
            throw new DomainException("Email is not a valid format.");
        return trimmed;
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var cleaned = new string(phone.Where(c => c == '+' || char.IsDigit(c)).ToArray());
        if (cleaned.Length < 7)
            throw new DomainException("Phone is too short.");
        return cleaned;
    }

    private static void ValidateContact(string? email, string? phone)
    {
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
            throw new DomainException("Patient must have at least an email or a phone.");
    }

    private static IReadOnlyList<string> NormalizeGroups(IEnumerable<string>? groupIds) =>
        (groupIds ?? Array.Empty<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct()
            .ToList();
}
