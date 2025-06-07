using AnteraApp.Api.Models;
using AnteraApp.Api.Settings;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace AnteraApp.Api.Services
{
    public class AnteraService
    {
        private readonly IMongoCollection<AnteraReading> _anteraCollection;

        public AnteraService(IOptions<MongoDBSettings> mongoSettings)
        {
            var mongoClient = new MongoClient(mongoSettings.Value.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(mongoSettings.Value.DatabaseName);
            _anteraCollection = mongoDatabase.GetCollection<AnteraReading>("AnteraReadings");
        }

        public async Task<List<AnteraReading>> GetAsync() =>
            await _anteraCollection.Find(_ => true).ToListAsync();

        public async Task<AnteraReading> CreateAsync(AnteraReading newReading)
        {
            await _anteraCollection.InsertOneAsync(newReading);
            return newReading;
        }
    }
}
