using System.Text.RegularExpressions;
using Acme.Domain.Common;

namespace Acme.Domain.Users;

public readonly record struct Email
{
    private static readonly Regex Pattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Value { get; }

    private Email(string value) => Value = value;

    public static Email Create(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new DomainException("Email is required.");
        var normalized = raw.Trim().ToLowerInvariant();
        if (!Pattern.IsMatch(normalized))
            throw new DomainException("Email format is invalid.");
        return new Email(normalized);
    }

    public override string ToString() => Value;

    public static implicit operator string(Email e) => e.Value;
}
