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
        private readonly JwtSettings _jwtSettings;

        public AuthService(
            IOptions<MongoDBSettings> dbSettings,
            IPasswordHasher<User> passwordHasher,
            JwtService jwtService,
            IOptions<JwtSettings> jwtSettings)
        {
            var client = new MongoClient(dbSettings.Value.ConnectionString);
            var database = client.GetDatabase(dbSettings.Value.DatabaseName);
            _users = database.GetCollection<User>("Users");
            _passwordHasher = passwordHasher;
            _jwtService = jwtService;
            _jwtSettings = jwtSettings.Value;
        }

        public async Task<string?> RegisterAsync(
            RegisterRequest request,
            CancellationToken cancellationToken = default)
        {
            var existing = await _users
                .Find(u => u.Email == request.Email)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null) return null;

            var user = new User
            {
                Email = request.Email,
                Name = request.Name
            };

            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

            await _users.InsertOneAsync(user, cancellationToken: cancellationToken);
            return _jwtService.GenerateToken(user);
        }

        public async Task<AuthSession?> LoginAsync(
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

            return await CreateSessionAsync(user, request.RememberMe, cancellationToken);
        }

        public async Task<AuthSession?> RefreshAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var tokenHash = HashToken(refreshToken);
            var newRefreshToken = CreateRefreshToken();
            var newSession = new RefreshTokenSession
            {
                TokenHash = HashToken(newRefreshToken),
                ExpiresAt = now.AddDays(GetRefreshTokenExpirationDays())
            };

            var filter = Builders<User>.Filter.And(
                Builders<User>.Filter.ElemMatch(
                    user => user.RefreshTokens,
                    session => session.TokenHash == tokenHash && session.ExpiresAt > now));
            var options = new FindOneAndUpdateOptions<User>
            {
                ReturnDocument = ReturnDocument.After
            };

            var user = await _users.FindOneAndUpdateAsync(
                filter,
                Builders<User>.Update.PullFilter(
                    candidate => candidate.RefreshTokens,
                    session => session.TokenHash == tokenHash),
                options,
                cancellationToken);
            if (user is null) return null;

            await _users.UpdateOneAsync(
                candidate => candidate.Id == user.Id,
                Builders<User>.Update.Push(candidate => candidate.RefreshTokens, newSession),
                cancellationToken: cancellationToken);

            return new AuthSession
            {
                AccessToken = _jwtService.GenerateToken(user),
                RefreshToken = newRefreshToken
            };
        }

        public async Task RevokeRefreshTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            var tokenHash = HashToken(refreshToken);
            var filter = Builders<User>.Filter.ElemMatch(
                user => user.RefreshTokens,
                session => session.TokenHash == tokenHash);
            var update = Builders<User>.Update.PullFilter(
                user => user.RefreshTokens,
                session => session.TokenHash == tokenHash);

            await _users.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        }

        public async Task<UserProfileResponse?> GetProfileAsync(string userId, CancellationToken cancellationToken)
        {
            var user = await _users.Find(candidate => candidate.Id == userId).FirstOrDefaultAsync(cancellationToken);
            return user is null ? null : ToUserProfileResponse(user);
        }

        public async Task<UserProfileResponse?> UpdateNameAsync(string userId, string name, CancellationToken cancellationToken)
        {
            var result = await _users.FindOneAndUpdateAsync(
                candidate => candidate.Id == userId,
                Builders<User>.Update.Set(candidate => candidate.Name, name),
                new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After },
                cancellationToken);

            return result is null ? null : ToUserProfileResponse(result);
        }

        public async Task<UserProfileResponse?> UpdateEmailAsync(
            string userId,
            string email,
            string currentPassword,
            CancellationToken cancellationToken)
        {
            var user = await _users.Find(candidate => candidate.Id == userId).FirstOrDefaultAsync(cancellationToken);
            if (user is null || VerifyPassword(user, currentPassword) == PasswordVerificationResult.Failed) return null;

            var existing = await _users.Find(candidate => candidate.Email == email && candidate.Id != userId)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null) return null;

            var result = await _users.FindOneAndUpdateAsync(
                candidate => candidate.Id == userId,
                Builders<User>.Update.Set(candidate => candidate.Email, email),
                new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After },
                cancellationToken);

            return result is null ? null : ToUserProfileResponse(result);
        }

        public async Task<bool?> UpdatePasswordAsync(
            string userId,
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken)
        {
            var user = await _users.Find(candidate => candidate.Id == userId).FirstOrDefaultAsync(cancellationToken);
            if (user is null) return null;
            if (VerifyPassword(user, currentPassword) == PasswordVerificationResult.Failed) return false;

            user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
            await _users.UpdateOneAsync(
                candidate => candidate.Id == userId,
                Builders<User>.Update.Set(candidate => candidate.PasswordHash, user.PasswordHash),
                cancellationToken: cancellationToken);
            return true;
        }

        private async Task<AuthSession> CreateSessionAsync(
            User user,
            bool rememberMe,
            CancellationToken cancellationToken)
        {
            if (!rememberMe)
            {
                return new AuthSession { AccessToken = _jwtService.GenerateToken(user) };
            }

            var now = DateTime.UtcNow;
            var refreshToken = CreateRefreshToken();
            var refreshSession = new RefreshTokenSession
            {
                TokenHash = HashToken(refreshToken),
                ExpiresAt = now.AddDays(GetRefreshTokenExpirationDays())
            };
            await _users.UpdateOneAsync(
                candidate => candidate.Id == user.Id,
                Builders<User>.Update.PullFilter(
                    candidate => candidate.RefreshTokens,
                    session => session.ExpiresAt <= now),
                cancellationToken: cancellationToken);

            await _users.UpdateOneAsync(
                candidate => candidate.Id == user.Id,
                Builders<User>.Update.Push(candidate => candidate.RefreshTokens, refreshSession),
                cancellationToken: cancellationToken);

            return new AuthSession
            {
                AccessToken = _jwtService.GenerateToken(user),
                RefreshToken = refreshToken
            };
        }

        private int GetRefreshTokenExpirationDays() =>
            _jwtSettings.RefreshTokenExpirationDays;

        private static string CreateRefreshToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        private static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

        private static UserProfileResponse ToUserProfileResponse(User user)
        {
            var name = string.IsNullOrWhiteSpace(user.Name)
                ? user.Email.Split('@', 2)[0]
                : user.Name;
            return new UserProfileResponse(name, user.Email);
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
