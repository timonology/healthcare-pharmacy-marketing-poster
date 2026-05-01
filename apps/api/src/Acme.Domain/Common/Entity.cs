namespace Acme.Domain.Common;

/// <summary>
/// Base class for entities identified by a stable id.
/// </summary>
public abstract class Entity
{
    public string Id { get; protected set; } = default!;

    public DateTime CreatedAtUtc { get; protected set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; protected set; } = DateTime.UtcNow;

    protected void Touch() => UpdatedAtUtc = DateTime.UtcNow;

    public override bool Equals(object? obj) =>
        obj is Entity other && other.GetType() == GetType() && other.Id == Id;

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
