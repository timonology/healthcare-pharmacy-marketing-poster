using MongoDB.Bson;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Bridge between the JSON string the domain holds and the BsonDocument
/// Mongo / Cosmos uses on the wire. Round-trips are lossless for objects.
/// </summary>
internal static class CanvasJsonConverter
{
    public static BsonDocument? ToBson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return BsonDocument.Parse(json);
    }

    public static string FromBson(BsonDocument? doc) =>
        doc is null
            ? string.Empty
            : doc.ToJson(new MongoDB.Bson.IO.JsonWriterSettings
            {
                OutputMode = MongoDB.Bson.IO.JsonOutputMode.RelaxedExtendedJson,
            });
}
