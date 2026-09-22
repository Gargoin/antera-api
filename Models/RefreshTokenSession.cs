using MongoDB.Bson.Serialization.Attributes;

namespace AnteraApp.Api.Models
{
    public class RefreshTokenSession
    {
        [BsonElement("tokenHash")]
        public string TokenHash { get; set; } = null!;

        [BsonElement("expiresAt")]
        public DateTime ExpiresAt { get; set; }
    }
}
