using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AnteraApp.Api.Models
{
    public class User
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("email")]
        public string Email { get; set; } = null!;

        [BsonElement("name")]
        public string Name { get; set; } = null!;

        [BsonElement("passwordHash")]
        public string PasswordHash { get; set; } = null!;

        [BsonElement("favoritePollenTypes")]
        public List<string> FavoritePollenTypes { get; set; } = [];

        [BsonElement("refreshTokens")]
        public List<RefreshTokenSession> RefreshTokens { get; set; } = [];
    }
}
