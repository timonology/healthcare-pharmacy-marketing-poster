namespace Acme.WebApi;

/// <summary>
/// Loads the JWT settings from the repo-root <c>.env</c> during development.
/// </summary>
/// <remarks>
/// <para>
/// The API and ai-service must sign and verify with the same secret, but in
/// local development they read it from different places: ai-service loads
/// <c>.env</c>, while the API layers appsettings, then user secrets, then
/// environment variables. A stale <c>dotnet user-secrets set Jwt:Secret</c>
/// silently outranks appsettings and there is no signal — every ai-service call
/// returns 401, which reads like a login bug rather than a config one.
/// </para>
/// <para>
/// Rather than asking every developer to keep two files in step by hand, this
/// makes <c>.env</c> authoritative for exactly the keys that must agree,
/// matching what docker-compose already does (it passes <c>Jwt__Secret</c> from
/// the same file).
/// </para>
/// <para>
/// Deliberately narrow. It maps three keys, only when they have a non-empty
/// value, and only in Development. It cannot clobber Stripe or any other
/// user-secret, and production is untouched — there the secret comes from the
/// environment or a key vault.
/// </para>
/// </remarks>
public static class DevEnvFile
{
    /// <summary>.env name to configuration key. Only cross-service settings.</summary>
    private static readonly (string EnvKey, string ConfigKey)[] Mappings =
    {
        ("JWT_SECRET", "Jwt:Secret"),
        ("JWT_ISSUER", "Jwt:Issuer"),
        ("JWT_AUDIENCE", "Jwt:Audience"),
    };

    /// <summary>
    /// Adds the mapped values as the highest-precedence source, so they beat
    /// user secrets. Returns the file used, or null when none was found.
    /// </summary>
    public static string? AddJwtSettings(
        IConfigurationBuilder configuration, string contentRootPath)
    {
        var file = FindEnvFile(contentRootPath);
        if (file is null) return null;

        Dictionary<string, string> values;
        try
        {
            values = Parse(File.ReadAllLines(file));
        }
        catch (IOException)
        {
            // A misread .env must not stop the API booting — the existing
            // configuration sources still apply.
            return null;
        }

        var overrides = new Dictionary<string, string?>();
        foreach (var (envKey, configKey) in Mappings)
        {
            if (values.TryGetValue(envKey, out var value) && !string.IsNullOrWhiteSpace(value))
                overrides[configKey] = value;
        }

        if (overrides.Count == 0) return null;

        configuration.AddInMemoryCollection(overrides);
        return file;
    }

    /// <summary>
    /// Walks up from the content root looking for a <c>.env</c> beside
    /// <c>docker-compose.yml</c>, so we can't pick up an unrelated file.
    /// </summary>
    private static string? FindEnvFile(string startPath)
    {
        var dir = new DirectoryInfo(startPath);

        // The API sits at apps/api/src/Acme.WebApi — four levels down. Ten is
        // ample headroom without walking to the filesystem root.
        for (var depth = 0; dir is not null && depth < 10; depth++, dir = dir.Parent)
        {
            var env = Path.Combine(dir.FullName, ".env");
            var marker = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(env) && File.Exists(marker)) return env;
        }
        return null;
    }

    /// <summary>
    /// Minimal KEY=VALUE parser: skips blanks and #comments, trims one layer of
    /// matching quotes. Enough for the handful of keys above; this is not a
    /// general-purpose dotenv implementation.
    /// </summary>
    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"')
                    || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }
}
