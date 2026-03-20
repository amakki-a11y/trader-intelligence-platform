using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TIP.Data.Auth;

namespace TIP.Api.Services;

/// <summary>
/// Service for user CRUD operations (admin only).
///
/// Design rationale:
/// - Wraps UserRepository with business logic (password hashing, validation).
/// - Admin cannot deactivate themselves.
/// - Password reset generates a temporary password and sets must_change_pwd = true.
/// </summary>
public sealed class UserManagementService
{
    private readonly ILogger<UserManagementService> _logger;
    private readonly UserRepository _userRepo;
    private readonly RoleRepository _roleRepo;

    private const int BcryptWorkFactor = 12;

    /// <summary>
    /// Initializes the user management service.
    /// </summary>
    public UserManagementService(ILogger<UserManagementService> logger, UserRepository userRepo, RoleRepository roleRepo)
    {
        _logger = logger;
        _userRepo = userRepo;
        _roleRepo = roleRepo;
    }

    /// <summary>
    /// Gets all users with their role info.
    /// </summary>
    public async Task<IReadOnlyList<UserDto>> GetAllUsersAsync(CancellationToken ct = default)
    {
        var users = await _userRepo.GetAllAsync(ct).ConfigureAwait(false);
        var result = new List<UserDto>();

        foreach (var u in users)
        {
            var serverIds = await _userRepo.GetUserServerIdsAsync(u.Id, ct).ConfigureAwait(false);
            result.Add(MapToDto(u, serverIds));
        }

        return result;
    }

    /// <summary>
    /// Gets a user by ID.
    /// </summary>
    public async Task<UserDto?> GetUserByIdAsync(int id, CancellationToken ct = default)
    {
        var u = await _userRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (u == null) return null;

        var serverIds = await _userRepo.GetUserServerIdsAsync(u.Id, ct).ConfigureAwait(false);
        return MapToDto(u, serverIds);
    }

    /// <summary>
    /// Creates a new user with a temporary password.
    /// </summary>
    public async Task<(int Id, string TempPassword, string? Error)> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return (0, "", "Username is required");
        if (string.IsNullOrWhiteSpace(request.Email))
            return (0, "", "Email is required");

        // Check role exists
        var role = await _roleRepo.GetByIdAsync(request.RoleId, ct).ConfigureAwait(false);
        if (role == null) return (0, "", "Invalid role ID");

        // Generate temp password
        var tempPassword = GenerateTempPassword();
        var hash = BCrypt.Net.BCrypt.HashPassword(tempPassword, BcryptWorkFactor);

        var user = new UserRecord
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = hash,
            DisplayName = request.DisplayName ?? request.Username,
            RoleId = request.RoleId,
            MustChangePassword = true
        };

        try
        {
            var id = await _userRepo.CreateAsync(user, ct).ConfigureAwait(false);

            // Assign server access if provided
            if (request.ServerIds is { Length: > 0 })
            {
                await _userRepo.SetUserServersAsync(id, request.ServerIds, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Created user '{Username}' with role '{Role}'", request.Username, role.Name);
            return (id, tempPassword, null);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") // unique violation
        {
            return (0, "", "Username or email already exists");
        }
    }

    /// <summary>
    /// Updates user profile.
    /// </summary>
    public async Task<string?> UpdateUserAsync(int id, UpdateUserRequest request, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user == null) return "User not found";

        if (!string.IsNullOrWhiteSpace(request.Email)) user.Email = request.Email;
        if (!string.IsNullOrWhiteSpace(request.DisplayName)) user.DisplayName = request.DisplayName;
        if (request.RoleId.HasValue) user.RoleId = request.RoleId.Value;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;

        await _userRepo.UpdateAsync(user, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Deactivates a user (soft delete). Admin cannot deactivate themselves.
    /// </summary>
    public async Task<string?> DeactivateUserAsync(int id, int currentUserId, CancellationToken ct = default)
    {
        if (id == currentUserId) return "Cannot deactivate your own account";

        var user = await _userRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user == null) return "User not found";

        await _userRepo.DeactivateAsync(id, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Resets a user's password to a temporary one.
    /// </summary>
    public async Task<(string? TempPassword, string? Error)> ResetPasswordAsync(int id, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user == null) return (null, "User not found");

        var tempPassword = GenerateTempPassword();
        var hash = BCrypt.Net.BCrypt.HashPassword(tempPassword, BcryptWorkFactor);
        await _userRepo.ResetPasswordAsync(id, hash, ct).ConfigureAwait(false);

        _logger.LogInformation("Password reset for user '{Username}' (admin action)", user.Username);
        return (tempPassword, null);
    }

    /// <summary>
    /// Sets server access for a user.
    /// </summary>
    public async Task<string?> SetUserServersAsync(int userId, int[] serverIds, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(userId, ct).ConfigureAwait(false);
        if (user == null) return "User not found";

        await _userRepo.SetUserServersAsync(userId, serverIds, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Gets all roles.
    /// </summary>
    public async Task<IReadOnlyList<RoleDto>> GetAllRolesAsync(CancellationToken ct = default)
    {
        var roles = await _roleRepo.GetAllAsync(ct).ConfigureAwait(false);
        return roles.Select(r => new RoleDto
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            Permissions = ParsePermissions(r.PermissionsJson)
        }).ToList();
    }

    /// <summary>
    /// Generates a random temporary password.
    /// </summary>
    private static string GenerateTempPassword()
    {
        const string chars = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$";
        var random = new Random();
        var password = new char[12];
        for (int i = 0; i < password.Length; i++)
        {
            password[i] = chars[random.Next(chars.Length)];
        }
        return new string(password);
    }

    private static UserDto MapToDto(UserRecord u, IReadOnlyList<int> serverIds)
    {
        return new UserDto
        {
            Id = u.Id,
            Username = u.Username,
            Email = u.Email,
            DisplayName = u.DisplayName,
            RoleId = u.RoleId,
            RoleName = u.RoleName,
            IsActive = u.IsActive,
            MustChangePassword = u.MustChangePassword,
            LastLogin = u.LastLogin,
            CreatedAt = u.CreatedAt,
            ServerIds = serverIds.ToArray()
        };
    }

    private static string[] ParsePermissions(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}

/// <summary>User DTO for API responses (no password hash).</summary>
public sealed class UserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int RoleId { get; set; }
    public string RoleName { get; set; } = "";
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime CreatedAt { get; set; }
    public int[] ServerIds { get; set; } = Array.Empty<int>();
}

/// <summary>Request to create a new user.</summary>
public sealed class CreateUserRequest
{
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public int RoleId { get; set; }
    public int[]? ServerIds { get; set; }
}

/// <summary>Request to update a user.</summary>
public sealed class UpdateUserRequest
{
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public int? RoleId { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>Role DTO for API responses.</summary>
public sealed class RoleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Permissions { get; set; } = Array.Empty<string>();
}
