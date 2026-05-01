namespace Acme.Domain.Common;

/// <summary>Domain rule violation. Mapped to HTTP 400 by the API layer.</summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}
