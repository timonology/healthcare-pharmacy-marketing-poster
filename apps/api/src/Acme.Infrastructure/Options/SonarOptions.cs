namespace Acme.Infrastructure.Options;

public sealed class SonarOptions
{
    public const string SectionName = "Sonar";

    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 5;
    public bool UseMock { get; set; } = true;
}
