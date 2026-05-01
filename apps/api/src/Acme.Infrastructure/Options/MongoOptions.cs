namespace Acme.Infrastructure.Options;

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    public string ConnectionString { get; set; } = default!;
    public string Database { get; set; } = default!;
}
