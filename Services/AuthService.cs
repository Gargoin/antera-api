using AnteraApp.Api.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AnteraApp.Api.Services
{
    public class AuthService
    {
        private readonly IMongoCollection<User> _users;
        private readonly string _jwtSecret;

        public AuthService(IOptions<MongoDBSettings> dbSettings, IConfiguration config)
        {
            var client = new MongoClient(dbSettings.Value.ConnectionString);
            var database = client.GetDatabase(dbSettings.Value.DatabaseName);
            _users = database.GetCollection<User>("Users");

            _jwtSecret = config["Jwt:Secret"]!;
        }

        public async Task<bool> RegisterAsync(RegisterRequest request)
        {
            var existing = await _users.Find(u => u.Email == request.Email).FirstOrDefaultAsync();
            if (existing != null) return false;

            var user = new User
            {
                Email = request.Email,
                PasswordHash = HashPassword(request.Password)
            };

            await _users.InsertOneAsync(user);
            return true;
        }

        public async Task<string?> LoginAsync(LoginRequest request)
        {
            var user = await _users.Find(u => u.Email == request.Email).FirstOrDefaultAsync();
            if (user == null || !VerifyPassword(request.Password, user.PasswordHash)) return null;

            return GenerateJwtToken(user);
        }

        private string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(password);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        private bool VerifyPassword(string password, string hash)
        {
            return HashPassword(password) == hash;
        }

        private string GenerateJwtToken(User user)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_jwtSecret);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, user.Id!),
                    new Claim(ClaimTypes.Email, user.Email)
                }),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature
                )
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}
