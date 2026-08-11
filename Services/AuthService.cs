using AnteraApp.Api.Models;
using AnteraApp.Api.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;

namespace AnteraApp.Api.Services
{
    public class AuthService
    {
        private const int LegacySha256HashSize = 32;

        private readonly IMongoCollection<User> _users;
        private readonly IPasswordHasher<User> _passwordHasher;
        private readonly JwtService _jwtService;

        public AuthService(
            IOptions<MongoDBSettings> dbSettings,
            IPasswordHasher<User> passwordHasher,
            JwtService jwtService)
        {
            var client = new MongoClient(dbSettings.Value.ConnectionString);
            var database = client.GetDatabase(dbSettings.Value.DatabaseName);
            _users = database.GetCollection<User>("Users");
            _passwordHasher = passwordHasher;
            _jwtService = jwtService;
        }

        public async Task<bool> RegisterAsync(
            RegisterRequest request,
            CancellationToken cancellationToken = default)
        {
            var existing = await _users
                .Find(u => u.Email == request.Email)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null) return false;

            var user = new User
            {
                Email = request.Email
            };

            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

            await _users.InsertOneAsync(user, cancellationToken: cancellationToken);
            return true;
        }

        public async Task<string?> LoginAsync(
            LoginRequest request,
            CancellationToken cancellationToken = default)
        {
            var user = await _users
                .Find(u => u.Email == request.Email)
                .FirstOrDefaultAsync(cancellationToken);

            if (user == null) return null;

            var verificationResult = VerifyPassword(user, request.Password);
            if (verificationResult == PasswordVerificationResult.Failed) return null;

            if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

                await _users.UpdateOneAsync(
                    candidate => candidate.Id == user.Id,
                    Builders<User>.Update.Set(candidate => candidate.PasswordHash, user.PasswordHash),
                    cancellationToken: cancellationToken);
            }

            return _jwtService.GenerateToken(user);
        }

        private PasswordVerificationResult VerifyPassword(User user, string password)
        {
            if (TryDecodeLegacyHash(user.PasswordHash, out var legacyHash))
            {
                var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                return CryptographicOperations.FixedTimeEquals(candidateHash, legacyHash)
                    ? PasswordVerificationResult.SuccessRehashNeeded
                    : PasswordVerificationResult.Failed;
            }

            return _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        }

        private static bool TryDecodeLegacyHash(string passwordHash, out byte[] decodedHash)
        {
            try
            {
                decodedHash = Convert.FromBase64String(passwordHash);
                return decodedHash.Length == LegacySha256HashSize;
            }
            catch (FormatException)
            {
                decodedHash = [];
                return false;
            }
        }
    }
}
