using Acme.Application.Common;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoIndexInitializer(MongoContext ctx) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await CreateUserIndexes(ct);
        await CreateRefreshTokenIndexes(ct);
        await CreateBrandKitIndexes(ct);
        await CreateTemplateIndexes(ct);
        await CreatePosterIndexes(ct);
        await CreateCampaignIndexes(ct);
        await CreatePatientIndexes(ct);
        await CreatePatientGroupIndexes(ct);
    }

    private async Task CreatePatientIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PatientDocument>("patients");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.FullName),
                new CreateIndexOptions { Name = "by_owner_name" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.GroupIds),
                new CreateIndexOptions { Name = "by_owner_group" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Text(x => x.FullName)
                    .Text(x => x.Email)
                    .Text(x => x.Phone)
                    .Text(x => x.Notes),
                new CreateIndexOptions { Name = "text_search" }),
            cancellationToken: ct);
    }

    private Task CreatePatientGroupIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PatientGroupDocument>("patient_groups");
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientGroupDocument>(
                Builders<PatientGroupDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.Name),
                new CreateIndexOptions { Name = "by_owner_name" }),
            cancellationToken: ct);
    }

    private Task CreateCampaignIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<CampaignDocument>("campaigns");
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<CampaignDocument>(
                Builders<CampaignDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_owner_created" }),
            cancellationToken: ct);
    }

    private Task CreateUserIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<UserDocument>("users");
        var keys = Builders<UserDocument>.IndexKeys.Ascending(u => u.Email);
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                keys,
                new CreateIndexOptions { Unique = true, Name = "uniq_email" }),
            cancellationToken: ct);
    }

    private async Task CreateRefreshTokenIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<RefreshTokenDocument>("refresh_tokens");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshTokenDocument>(
                Builders<RefreshTokenDocument>.IndexKeys.Ascending(t => t.TokenHash),
                new CreateIndexOptions { Unique = true, Name = "uniq_tokenHash" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshTokenDocument>(
                Builders<RefreshTokenDocument>.IndexKeys.Ascending(t => t.UserId),
                new CreateIndexOptions { Name = "by_userId" }),
            cancellationToken: ct);
    }

    private Task CreateBrandKitIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<BrandKitDocument>("brand_kits");
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<BrandKitDocument>(
                Builders<BrandKitDocument>.IndexKeys.Ascending(x => x.OwnerId),
                new CreateIndexOptions { Unique = true, Name = "uniq_owner" }),
            cancellationToken: ct);
    }

    private async Task CreateTemplateIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<TemplateDocument>("templates");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<TemplateDocument>(
                Builders<TemplateDocument>.IndexKeys.Ascending(x => x.Category),
                new CreateIndexOptions { Name = "by_category" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<TemplateDocument>(
                Builders<TemplateDocument>.IndexKeys
                    .Text(x => x.Name)
                    .Text(x => x.Description)
                    .Text(x => x.Tags),
                new CreateIndexOptions { Name = "text_search" }),
            cancellationToken: ct);
    }

    private async Task CreatePosterIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PosterDocument>("posters");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PosterDocument>(
                Builders<PosterDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.UpdatedAtUtc),
                new CreateIndexOptions { Name = "by_owner_updated" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PosterDocument>(
                Builders<PosterDocument>.IndexKeys.Text(x => x.Name),
                new CreateIndexOptions { Name = "text_search" }),
            cancellationToken: ct);
    }
}
