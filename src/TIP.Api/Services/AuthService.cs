using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using TIP.Data.Auth;

namespace TIP.Api.Services;

/// <summary>
/// Handles authentication operations: login, JWT generation, refresh token rotation,
/// password changes, and logout.
///
/// Design rationale:
/// - JWT access tokens are short-lived (15 min) for security.
/// - Refresh tokens are long-lived (7 days) with rotation (old token invalidated on use).
/// - Refresh tokens are SHA256-hashed before DB storage (never stored plain).
/// - BCrypt with work factor 12 for password hashing.
/// - Account lockout after 10 failed login attempts (30-minute lock).
/// - All auth events logged via Serilog for audit trail.
/// </summary>
public sealed class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly UserRepository _userRepo;
    private readonly RefreshTokenRepository _tokenRepo;
    private readonly IConfiguration _config;

    private const int MaxFailedAttempts = 10;
    private const int LockoutMinutes = 30;
    private const int BcryptWorkFactor = 12;

    /// <summary>
    /// Initializes the auth service.
    /// </summary>
    /// <param name="logger">Logger for auth events.</param>
    /// <param name="userRepo">User repository.</param>
    /// <param name="tokenRepo">Refresh token repository.</param>
    /// <param name="config">Application configuration (JWT settings).</param>
    public AuthService(
        ILogger<AuthService> logger,
        UserRepository userRepo,
        RefreshTokenRepository tokenRepo,
        IConfiguration config)
    {
        _logger = logger;
        _userRepo = userRepo;
        _tokenRepo = tokenRepo;
        _config = config;
    }

    /// <summary>
    /// Authenticates a user and returns JWT + refresh token.
    /// </summary>
    /// <param name="username">Login username.</param>
    /// <param name="password">Plain text password.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Login result with tokens and user info, or error message.</returns>
    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByUsernameAsync(username, ct).ConfigureAwait(false);

        if (user == null)
        {
            _logger.LogWarning("Login failed: user '{Username}' not found", username);
            return LoginResult.Fail("Invalid username or password");
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login failed: user '{Username}' is deactivated", username);
            return LoginResult.Fail("Account is deactivated");
        }

        // Check lockout
        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
        {
            var remaining = (user.LockedUntil.Value - DateTime.UtcNow).TotalMinutes;
            _logger.LogWarning("Login failed: user '{Username}' is locked for {Minutes:F0} more minutes", username, remaining);
            return LoginResult.Fail($"Account is locked. Try again in {remaining:F0} minutes.");
        }

        // Verify password
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            var attempts = await _userRepo.IncrementFailedAttemptsAsync(user.Id, ct).ConfigureAwait(false);
            _logger.LogWarning("Login failed: invalid password for '{Username}' (attempt {Attempts})", username, attempts);

            if (attempts >= MaxFailedAttempts)
            {
                var lockUntil = DateTimeOffset.UtcNow.AddMinutes(LockoutMinutes);
                await _userRepo.LockAccountAsync(user.Id, lockUntil, ct).ConfigureAwait(false);
                _logger.LogWarning("Account '{Username}' locked until {LockUntil}", username, lockUntil);
                return LoginResult.Fail($"Account locked for {LockoutMinutes} minutes due to too many failed attempts");
            }

            return LoginResult.Fail("Invalid username or password");
        }

        // Success — record login, generate tokens
        await _userRepo.RecordLoginAsync(user.Id, ct).ConfigureAwait(false);

        var serverIds = await _userRepo.GetUserServerIdsAsync(user.Id, ct).ConfigureAwait(false);
        var accessToken = GenerateAccessToken(user, serverIds);
        var refreshToken = GenerateRefreshToken();
        var refreshTokenHash = HashToken(refreshToken);

        var refreshDays = _config.GetValue("Jwt:RefreshTokenDays", 7);
        await _tokenRepo.CreateAsync(user.Id, refreshTokenHash, DateTimeOffset.UtcNow.AddDays(refreshDays), ct).ConfigureAwait(false);

        _logger.LogInformation("User '{Username}' logged in successfully", username);

        return LoginResult.Success(accessToken, refreshToken, new UserInfo
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Role = user.RoleName,
            Permissions = ParsePermissions(user.PermissionsJson),
            ServerIds = serverIds.ToArray(),
            MustChangePassword = user.MustChangePassword
        });
    }

    /// <summary>
    /// Refreshes tokens using a valid refresh token. Implements token rotation.
    /// </summary>
    /// <param name="refreshToken">The refresh token to exchange.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New access token and refresh token, or error.</returns>
    public async Task<LoginResult> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(refreshToken);
        var storedToken = await _tokenRepo.GetByHashAsync(tokenHash, ct).ConfigureAwait(false);

        if (storedToken == null || !storedToken.IsValid)
        {
            _logger.LogWarning("Refresh token invalid or expired");
            return LoginResult.Fail("Invalid or expired refresh token");
        }

        // Revoke the old token (rotation)
        await _tokenRepo.RevokeAsync(tokenHash, ct).ConfigureAwait(false);

        // Load user
        var user = await _userRepo.GetByIdAsync(storedToken.UserId, ct).ConfigureAwait(false);
        if (user == null || !user.IsActive)
        {
            _logger.LogWarning("Refresh failed: user ID {UserId} not found or deactivated", storedToken.UserId);
            return LoginResult.Fail("User not found or deactivated");
        }

        var serverIds = await _userRepo.GetUserServerIdsAsync(user.Id, ct).ConfigureAwait(false);
        var newAccessToken = GenerateAccessToken(user, serverIds);
        var newRefreshToken = GenerateRefreshToken();
        var newRefreshHash = HashToken(newRefreshToken);

        var refreshDays = _config.GetValue("Jwt:RefreshTokenDays", 7);
        await _tokenRepo.CreateAsync(user.Id, newRefreshHash, DateTimeOffset.UtcNow.AddDays(refreshDays), ct).ConfigureAwait(false);

        _logger.LogDebug("Tokens refreshed for user '{Username}'", user.Username);

        return LoginResult.Success(newAccessToken, newRefreshToken, new UserInfo
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Role = user.RoleName,
            Permissions = ParsePermissions(user.PermissionsJson),
            ServerIds = serverIds.ToArray(),
            MustChangePassword = user.MustChangePassword
        });
    }

    /// <summary>
    /// Changes a user's password.
    /// </summary>
    /// <param name="userId">User ID.</param>
    /// <param name="oldPassword">Current password.</param>
    /// <param name="newPassword">New password.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if successful, error message if failed.</returns>
    public async Task<(bool Ok, string Error)> ChangePasswordAsync(int userId, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user == null) return (false, "User not found");

        if (!BCrypt.Net.BCrypt.Verify(oldPassword, user.PasswordHash))
        {
            _logger.LogWarning("Password change failed for user '{Username}': incorrect current password", user.Username);
            return (false, "Current password is incorrect");
        }

        if (newPassword.Length < 8)
        {
            return (false, "New password must be at least 8 characters");
        }

        var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor);
        await _userRepo.UpdatePasswordAsync(userId, newHash, ct).ConfigureAwait(false);

        // Revoke all existing refresh tokens for security
        await _tokenRepo.RevokeAllForUserAsync(userId, ct).ConfigureAwait(false);

        _logger.LogInformation("Password changed for user '{Username}'", user.Username);
        return (true, "");
    }

    /// <summary>
    /// Revokes a refresh token (logout).
    /// </summary>
    /// <param name="refreshToken">The refresh token to revoke.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(refreshToken);
        await _tokenRepo.RevokeAsync(tokenHash, ct).ConfigureAwait(false);
        _logger.LogDebug("Refresh token revoked (logout)");
    }

    /// <summary>
    /// Gets current user info from a user ID (for /api/auth/me).
    /// </summary>
    /// <param name="userId">User ID from JWT claims.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>User info, or null if not found.</returns>
    public async Task<UserInfo?> GetCurrentUserAsync(int userId, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user == null) return null;

        var serverIds = await _userRepo.GetUserServerIdsAsync(user.Id, ct).ConfigureAwait(false);

        return new UserInfo
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Role = user.RoleName,
            Permissions = ParsePermissions(user.PermissionsJson),
            ServerIds = serverIds.ToArray(),
            MustChangePassword = user.MustChangePassword
        };
    }

    /// <summary>
    /// Generates a JWT access token.
    /// </summary>
    private string GenerateAccessToken(UserRecord user, IReadOnlyList<int> serverIds)
    {
        var secret = _config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret not configured");
        var issuer = _config.GetValue("Jwt:Issuer", "TIP");
        var audience = _config.GetValue("Jwt:Audience", "TIP-Web");
        var minutes = _config.GetValue("Jwt:AccessTokenMinutes", 15);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var permissions = ParsePermissions(user.PermissionsJson);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("username", user.Username),
            new("role", user.RoleName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // Add permissions as individual claims
        foreach (var perm in permissions)
        {
            claims.Add(new Claim("permission", perm));
        }

        // Add server IDs as individual claims
        foreach (var sid in serverIds)
        {
            claims.Add(new Claim("server_id", sid.ToString()));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(minutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generates a cryptographically random refresh token.
    /// </summary>
    private static string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Hashes a refresh token with SHA256 for DB storage.
    /// </summary>
    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Parses a JSONB permissions array string into a string array.
    /// </summary>
    private static string[] ParsePermissions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Result of a login or token refresh operation.
/// </summary>
public sealed class LoginResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>JWT access token (null on failure).</summary>
    public string? AccessToken { get; init; }

    /// <summary>Refresh token (null on failure).</summary>
    public string? RefreshToken { get; init; }

    /// <summary>User information (null on failure).</summary>
    public UserInfo? User { get; init; }

    /// <summary>Error message (null on success).</summary>
    public string? Error { get; init; }

    /// <summary>Creates a successful login result.</summary>
    public static LoginResult Success(string accessToken, string refreshToken, UserInfo user) =>
        new() { IsSuccess = true, AccessToken = accessToken, RefreshToken = refreshToken, User = user };

    /// <summary>Creates a failed login result.</summary>
    public static LoginResult Fail(string error) =>
        new() { IsSuccess = false, Error = error };
}

/// <summary>
/// User information returned after authentication.
/// </summary>
public sealed class UserInfo
{
    /// <summary>User ID.</summary>
    public int Id { get; set; }

    /// <summary>Login username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Display name for UI.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>User email.</summary>
    public string Email { get; set; } = "";

    /// <summary>Role name (admin, dealer, compliance).</summary>
    public string Role { get; set; } = "";

    /// <summary>Array of permission strings.</summary>
    public string[] Permissions { get; set; } = Array.Empty<string>();

    /// <summary>Server IDs the user can access.</summary>
    public int[] ServerIds { get; set; } = Array.Empty<int>();

    /// <summary>Whether user must change password on next login.</summary>
    public bool MustChangePassword { get; set; }
}
