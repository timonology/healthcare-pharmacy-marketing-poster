namespace Acme.Application.Templates;

/// <summary>
/// Implemented in Infrastructure (or hosted in WebApi). Called once at
/// startup to populate the curated template set if the collection is empty.
/// </summary>
public interface ITemplateSeeder
{
    Task SeedIfEmptyAsync(CancellationToken ct);
}
