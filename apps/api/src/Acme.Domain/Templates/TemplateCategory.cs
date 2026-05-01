namespace Acme.Domain.Templates;

/// <summary>
/// Curated list of template categories. Adding a new value is a deliberate
/// schema change so we don't end up with category drift.
/// </summary>
public enum TemplateCategory
{
    Vaccination,
    Promotion,
    Awareness,
    Safety,
    Seasonal,
    General,
}
