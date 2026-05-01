namespace Acme.Infrastructure.Options;

public sealed class AzureBlobOptions
{
    public const string SectionName = "AzureBlob";

    public string ConnectionString { get; set; } = default!;
    public string Container { get; set; } = "canvas-assets";
}
