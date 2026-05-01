namespace Acme.Application.Canvas;

/// <summary>
/// Mirror of packages/shared-types/src/canvas.ts. Keep in sync.
/// </summary>
public sealed record CanvasDocumentDto(
    int SchemaVersion,
    string Id,
    string OwnerId,
    string Name,
    int Width,
    int Height,
    string Background,
    CanvasViewportDto Viewport,
    IReadOnlyList<ShapeDto> Shapes,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CanvasViewportDto(Vector2Dto Pan, double Zoom);

public sealed record Vector2Dto(double X, double Y);

/// <summary>
/// Discriminator: "rect" | "circle" | "line" | "text" | "image" | "group".
/// Concrete shape fields are carried in <see cref="ExtraProperties"/> until
/// you decide to model each subtype as a record.
/// </summary>
public sealed record ShapeDto(
    string Id,
    string Kind,
    Vector2Dto Position,
    double Rotation,
    Vector2Dto Scale,
    double Opacity,
    bool Draggable,
    int ZIndex,
    IDictionary<string, object?> ExtraProperties);
