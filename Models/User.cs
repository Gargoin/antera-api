using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AnteraApp.Api.Models
{
    [BsonIgnoreExtraElements]
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

        [BsonElement("sessionVersion")]
        public int SessionVersion { get; set; }

        [BsonElement("emailConfirmedAt")]
        public DateTime? EmailConfirmedAt { get; set; }

        [BsonElement("emailConfirmationTokenHash")]
        public string? EmailConfirmationTokenHash { get; set; }

        [BsonElement("emailConfirmationTokenExpiresAt")]
        public DateTime? EmailConfirmationTokenExpiresAt { get; set; }

        [BsonElement("emailConfirmationLastSentAt")]
        public DateTime? EmailConfirmationLastSentAt { get; set; }

        [BsonElement("passwordResetTokenHash")]
        public string? PasswordResetTokenHash { get; set; }

        [BsonElement("passwordResetTokenExpiresAt")]
        public DateTime? PasswordResetTokenExpiresAt { get; set; }

        [BsonElement("passwordResetLastSentAt")]
        public DateTime? PasswordResetLastSentAt { get; set; }

        [BsonElement("pendingEmail")]
        public string? PendingEmail { get; set; }

        [BsonElement("pendingEmailConfirmationTokenHash")]
        public string? PendingEmailConfirmationTokenHash { get; set; }

        [BsonElement("pendingEmailConfirmationTokenExpiresAt")]
        public DateTime? PendingEmailConfirmationTokenExpiresAt { get; set; }

        [BsonElement("pendingEmailConfirmationLastSentAt")]
        public DateTime? PendingEmailConfirmationLastSentAt { get; set; }
    }
}
