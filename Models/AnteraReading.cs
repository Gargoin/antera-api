using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AnteraApp.Api.Models
{
    public class AnteraReading
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; } = null!;

        public DateTime Date { get; set; }

        public string Location { get; set; } = null!;

        public int Level { get; set; }
    }
}
