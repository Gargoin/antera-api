using AnteraApp.Api.Models;
using AnteraApp.Api.Settings;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace AnteraApp.Api.Services;

public sealed class PollenPreferencesService
{
    private readonly IMongoCollection<User> _users;

    public PollenPreferencesService(IOptions<MongoDBSettings> dbSettings)
    {
        var client = new MongoClient(dbSettings.Value.ConnectionString);
        var database = client.GetDatabase(dbSettings.Value.DatabaseName);
        _users = database.GetCollection<User>("Users");
    }

    public async Task<PollenPreferencesResponse?> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _users
            .Find(candidate => candidate.Id == userId)
            .FirstOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new PollenPreferencesResponse(PollenCatalog.FromIds(user.FavoritePollenTypes));
    }

    public async Task<PollenPreferencesResponse?> UpdateAsync(
        string userId,
        IReadOnlyList<string> pollenTypeIds,
        CancellationToken cancellationToken = default)
    {
        var result = await _users.UpdateOneAsync(
            candidate => candidate.Id == userId,
            Builders<User>.Update.Set(candidate => candidate.FavoritePollenTypes, pollenTypeIds.ToList()),
            cancellationToken: cancellationToken);

        return result.MatchedCount == 0
            ? null
            : new PollenPreferencesResponse(PollenCatalog.FromIds(pollenTypeIds));
    }
}
