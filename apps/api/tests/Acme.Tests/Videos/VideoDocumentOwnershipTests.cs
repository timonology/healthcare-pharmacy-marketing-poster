using System.Reflection;
using Acme.Domain.Videos;
using Acme.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// Two processes write one document, and each has to leave the other's half alone.
/// </summary>
/// <remarks>
/// <para>
/// The .NET API owns the entity — name, status, ownership, the lease. The Python
/// render worker owns what the pipeline produced: the script, the generated
/// title and description, the tags, the segment metadata, the imagery cost
/// counters, and the artifacts a refine resumes from.
/// </para>
/// <para>
/// <b>The .NET side used to erase all of it on every write.</b> The repository
/// did a whole-document replace built from <c>VideoMapper.ToDocument</c>, which
/// maps the fields the domain knows about and no others. So renaming a video
/// destroyed its script and the record of what it cost to make — silently, with
/// the rename appearing to work perfectly.
/// </para>
/// <para>
/// This is a source test rather than a behavioural one on purpose. The fault is
/// a field quietly missing from a mapping, and the moment that fails is a
/// database write nobody is watching. Catching it needs something that reads the
/// mapping itself.
/// </para>
/// </remarks>
public class VideoDocumentOwnershipTests
{
    private static readonly string InfrastructureDir = FindInfrastructureDir();

    private static string Read(string file) =>
        File.ReadAllText(Path.Combine(InfrastructureDir, "Persistence", file));

    /// <summary>
    /// Fields written by the Python worker, which the .NET side must never
    /// include in an update.
    /// </summary>
    /// <remarks>
    /// Adding a field here is the visible, arguable step it should be — the same
    /// shape as the anonymous-endpoint list in the authorization matrix. If the
    /// worker starts writing something new, it goes in this list and the test
    /// below proves the API is not trampling it.
    /// </remarks>
    private static readonly string[] WorkerOwned =
    {
        "GeneratedTitle",
        "Script",
        "GeneratedDescription",
        "Tags",
        "MetadataJson",
        "AiImageCostUsd",
        "AiImagesGenerated",
        "AudioBlobKey",
        "ClipSources",
        // Which moment was on screen when, and where each moment's still is
        // kept — written by the worker after every render, read by the API only
        // to build the shot picker. An update from this side that wrote it
        // would erase the tiles, or point them at the previous render's stills.
        "ShotTimeline",
        "StillBlobKeys",
        // What the render cost us. The measurements the refine charge policy is
        // waiting on, so an update from this side that wiped them would set that
        // decision back to zero without anybody noticing.
        "Usage",
        "UsageAttempts",
        "UsageTotals",
        // The sources the script was written from. A refine that rewrites the
        // wording resumes from these, so an update from this side that cleared
        // them would turn the next rewrite into fresh research — and on health
        // copy that is a change of claim, not a change of tone.
        "Facts",
        // Why the last render failed, technically — the operator's half of the
        // pair whose other half is `note`. Written by the worker on failure and
        // unset by it when the next attempt claims the job.
        //
        // <b>It is the first worker-owned field that also exists on the domain
        // entity</b>, because the API has to serve it to the smoke test. That
        // combination is what this list is for: without the entry, the test
        // below reads it as a domain field and demands the repository write it,
        // which would have the API blank the worker's explanation on the next
        // rename.
        "Diagnostic",
    };

    /// <summary>Facts fixed when the row is inserted.</summary>
    private static readonly string[] Immutable = { "Id", "OwnerId", "CreatedAtUtc" };

    [Fact]
    public void The_api_update_touches_nothing_the_worker_owns()
    {
        var repo = Read("MongoVideoRepository.cs");
        var written = Written(repo);

        foreach (var field in WorkerOwned)
        {
            written.Should().NotContain(field,
                "'{0}' is written by the render worker. An update from this side "
                + "that sets it overwrites what the pipeline produced — and a "
                + "rename is not a request to discard a video's script", field);
        }
    }

    [Fact]
    public void The_api_update_cannot_move_a_video_between_pharmacies()
    {
        var written = Written(Read("MongoVideoRepository.cs"));

        foreach (var field in Immutable)
        {
            written.Should().NotContain(field,
                "'{0}' is fixed at insert. An update that can write ownerId is an "
                + "update that can hand one pharmacy's video to another", field);
        }
    }

    [Fact]
    public void Every_field_the_domain_owns_is_actually_written()
    {
        // The other direction, and the one a field-scoped update gets wrong:
        // adding a property to the entity and forgetting to write it, so the
        // value changes in memory, the save reports success, and the change is
        // gone on the next read.
        var written = Written(Read("MongoVideoRepository.cs"));

        var documentFields = System.Text.RegularExpressions.Regex
            .Matches(VideoDocumentBody(), @"public\s+[\w<>?\[\]]+\s+(\w+)\s*\{\s*get;\s*set;")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var shouldBeWritten = documentFields
            .Except(WorkerOwned)
            .Except(Immutable)
            .ToList();

        shouldBeWritten.Should().NotBeEmpty("the reflection must be finding fields");

        foreach (var field in shouldBeWritten)
        {
            written.Should().Contain(field,
                "'{0}' belongs to the domain, so an update that leaves it out means "
                + "a change the user made is accepted and then silently lost", field);
        }
    }

    /// <summary>
    /// Just the <c>VideoDocument</c> class body, not the whole file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file also declares the small document types nested inside a video —
    /// the stock-footage references, and the stamped pharmacy brand. Their
    /// properties are not fields of <c>videos</c> and there is nothing for the
    /// repository to <c>.Set</c> them to, so scanning the whole file demanded
    /// writes that cannot exist.
    /// </para>
    /// <para>
    /// This used to be a hand-maintained list of names to ignore, which is the
    /// same trap the test is about: adding a nested type meant remembering to
    /// extend a list somewhere else, and forgetting broke an unrelated test with
    /// a baffling message. Scoping the scan removes the obligation instead of
    /// documenting it.
    /// </para>
    /// <para>
    /// <c>VideoDocument</c> is declared last in the file, so its body runs to the
    /// end. If that stops being true this throws rather than silently scanning
    /// the wrong thing.
    /// </para>
    /// </remarks>
    private static string VideoDocumentBody()
    {
        var file = Read("VideoDocument.cs");
        var at = file.IndexOf("class VideoDocument", StringComparison.Ordinal);

        if (at < 0)
            throw new InvalidOperationException(
                "VideoDocument.cs no longer declares a class called VideoDocument. "
                + "This test reads that class to find the fields the repository "
                + "must write, and cannot do so.");

        var after = file[(at + "class VideoDocument".Length)..];

        if (after.Contains("class ", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "VideoDocument is no longer the last class in VideoDocument.cs. "
                + "This test assumes its body runs to the end of the file; move "
                + "the new class above it, or teach this method to find the "
                + "closing brace.");

        return after;
    }

    private static HashSet<string> Written(string repositorySource) =>
        System.Text.RegularExpressions.Regex
            .Matches(repositorySource, @"\.Set\(x => x\.(\w+),")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

    /// <summary>Walks up from the test binary to the Infrastructure sources.</summary>
    private static string FindInfrastructureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Acme.Infrastructure");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find apps/api/src/Acme.Infrastructure from the test output "
            + "directory. This test reads the repository source, so it needs the "
            + "tree rather than just the compiled assembly.");
    }

    [Fact]
    public void Every_worker_owned_field_is_ignored_when_null()
    {
        // <b>Not writing a field includes not writing it as null.</b>
        //
        // UpdateAsync correctly never sets these — but AddAsync inserts the
        // whole document, so a nullable worker-owned property lands in Mongo as
        // an explicit null. `$push` refuses a null field ("must be an array but
        // is of type null") while creating the array happily when it is absent,
        // so the worker's first usage write failed on every new video. That
        // write is on the failure path too, so a render that failed could not
        // record the failure: the video stayed Rendering, was re-claimed, and
        // never moved.
        //
        // Read off the document type rather than a list, so a worker-owned
        // field added later cannot miss the attribute quietly.
        var doc = typeof(MongoVideoRepository).Assembly
            .GetType("Acme.Infrastructure.Persistence.VideoDocument")!;

        var missing = new List<string>();
        foreach (var name in WorkerOwned)
        {
            var prop = doc.GetProperty(
                name,
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);

            if (prop is null) continue;   // the name check above owns this case

            // Value types cannot be null, so the hazard does not apply to them.
            var nullable = !prop.PropertyType.IsValueType
                || Nullable.GetUnderlyingType(prop.PropertyType) is not null;
            if (!nullable) continue;

            var ignored = prop.GetCustomAttributes(
                typeof(MongoDB.Bson.Serialization.Attributes.BsonIgnoreIfNullAttribute),
                inherit: true).Length > 0;

            if (!ignored) missing.Add(name);
        }

        missing.Should().BeEmpty(
            "these worker-owned fields are nullable and would be inserted as "
            + "explicit nulls, which $push and $inc refuse. Add "
            + "[BsonIgnoreIfNull] so a new video carries the field absent "
            + "rather than null: {0}", string.Join(", ", missing));
    }

}
