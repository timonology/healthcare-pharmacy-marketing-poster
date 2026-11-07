// The renderer interfaces (IPosterAnimationRenderer, IAiVideoProvider,
// PosterAnimationRequest, RenderedVideo) moved to the Python ai-service.
// The .NET API now owns only the Video entity's business rules; the
// Python worker in apps/ai-service polls the Mongo `videos` collection
// and mutates the row to Rendering → Ready.
namespace Acme.Application.Videos;
