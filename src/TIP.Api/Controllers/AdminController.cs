using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TIP.Api.Middleware;
using TIP.Api.Services;

namespace TIP.Api.Controllers;

/// <summary>
/// Admin-only endpoints for user management, MT5 server management, and role viewing.
///
/// Design rationale:
/// - All endpoints require [Authorize] + [RequirePermission] for admin role.
/// - User CRUD includes server access assignment.
/// - MT5 server management includes test connection before saving.
/// - Passwords never returned in API responses.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public sealed class AdminController : ControllerBase
{
    private readonly ILogger<AdminController> _logger;
    private readonly UserManagementService _userService;
    private readonly MT5ServerManagementService _serverService;

    /// <summary>
    /// Initializes the admin controller.
    /// </summary>
    public AdminController(
        ILogger<AdminController> logger,
        UserManagementService userService,
        MT5ServerManagementService serverService)
    {
        _logger = logger;
        _userService = userService;
        _serverService = serverService;
    }

    // ── User Management ─────────────────────────────────────────────────────

    /// <summary>Gets all users.</summary>
    [HttpGet("users")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var users = await _userService.GetAllUsersAsync(ct).ConfigureAwait(false);
        return Ok(users);
    }

    /// <summary>Gets a user by ID.</summary>
    [HttpGet("users/{id}")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> GetUser(int id, CancellationToken ct)
    {
        var user = await _userService.GetUserByIdAsync(id, ct).ConfigureAwait(false);
        return user != null ? Ok(user) : NotFound(new { error = "User not found" });
    }

    /// <summary>Creates a new user. Returns temporary password.</summary>
    [HttpPost("users")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var (id, tempPassword, error) = await _userService.CreateUserAsync(request, ct).ConfigureAwait(false);
        if (error != null) return BadRequest(new { error });
        return Ok(new { id, tempPassword, message = "User created. Share the temporary password securely." });
    }

    /// <summary>Updates a user's profile.</summary>
    [HttpPut("users/{id}")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var error = await _userService.UpdateUserAsync(id, request, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "User updated" });
    }

    /// <summary>Deactivates a user (soft delete).</summary>
    [HttpDelete("users/{id}")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> DeactivateUser(int id, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null) return Unauthorized();

        var error = await _userService.DeactivateUserAsync(id, currentUserId.Value, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "User deactivated" });
    }

    /// <summary>Resets a user's password (generates temporary one).</summary>
    [HttpPost("users/{id}/reset-password")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> ResetPassword(int id, CancellationToken ct)
    {
        var (tempPassword, error) = await _userService.ResetPasswordAsync(id, ct).ConfigureAwait(false);
        if (error != null) return BadRequest(new { error });
        return Ok(new { tempPassword, message = "Password reset. Share the temporary password securely." });
    }

    /// <summary>Sets server access for a user.</summary>
    [HttpPut("users/{id}/servers")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> SetUserServers(int id, [FromBody] int[] serverIds, CancellationToken ct)
    {
        var error = await _userService.SetUserServersAsync(id, serverIds, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "Server access updated" });
    }

    // ── Role Management ─────────────────────────────────────────────────────

    /// <summary>Gets all roles with permissions.</summary>
    [HttpGet("roles")]
    [RequirePermission("admin.users")]
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        var roles = await _userService.GetAllRolesAsync(ct).ConfigureAwait(false);
        return Ok(roles);
    }

    // ── MT5 Server Management ───────────────────────────────────────────────

    /// <summary>Gets all MT5 servers with connection status.</summary>
    [HttpGet("servers")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> GetServers(CancellationToken ct)
    {
        var servers = await _serverService.GetAllServersAsync(ct).ConfigureAwait(false);
        return Ok(servers);
    }

    /// <summary>Gets an MT5 server by ID.</summary>
    [HttpGet("servers/{id}")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> GetServer(int id, CancellationToken ct)
    {
        var server = await _serverService.GetServerByIdAsync(id, ct).ConfigureAwait(false);
        return server != null ? Ok(server) : NotFound(new { error = "Server not found" });
    }

    /// <summary>Adds a new MT5 server.</summary>
    [HttpPost("servers")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> AddServer([FromBody] AddServerRequest request, CancellationToken ct)
    {
        var (id, error) = await _serverService.AddServerAsync(request, ct).ConfigureAwait(false);
        if (error != null) return BadRequest(new { error });
        return Ok(new { id, message = "Server added" });
    }

    /// <summary>Updates an MT5 server config.</summary>
    [HttpPut("servers/{id}")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> UpdateServer(int id, [FromBody] UpdateServerRequest request, CancellationToken ct)
    {
        var error = await _serverService.UpdateServerAsync(id, request, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "Server updated" });
    }

    /// <summary>Deletes an MT5 server config.</summary>
    [HttpDelete("servers/{id}")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> DeleteServer(int id, CancellationToken ct)
    {
        var error = await _serverService.DeleteServerAsync(id, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "Server deleted" });
    }

    /// <summary>Enables an MT5 server (starts connection).</summary>
    [HttpPost("servers/{id}/enable")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> EnableServer(int id, CancellationToken ct)
    {
        var error = await _serverService.EnableServerAsync(id, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "Server enabled" });
    }

    /// <summary>Disables an MT5 server (stops connection).</summary>
    [HttpPost("servers/{id}/disable")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> DisableServer(int id, CancellationToken ct)
    {
        var error = await _serverService.DisableServerAsync(id, ct).ConfigureAwait(false);
        return error != null ? BadRequest(new { error }) : Ok(new { message = "Server disabled" });
    }

    /// <summary>Tests connectivity to an MT5 server without saving.</summary>
    [HttpPost("servers/{id}/test")]
    [RequirePermission("admin.servers")]
    public async Task<IActionResult> TestServer(int id, CancellationToken ct)
    {
        // For now, just verify the server config exists and password decrypts
        var server = await _serverService.GetServerByIdAsync(id, ct).ConfigureAwait(false);
        if (server == null) return NotFound(new { error = "Server not found" });

        var password = await _serverService.GetDecryptedPasswordAsync(id, ct).ConfigureAwait(false);
        if (password == null) return BadRequest(new { error = "Failed to decrypt password" });

        // Actual MT5 connection test would go here in future
        return Ok(new { success = true, message = "Server config is valid. Connection test available after MT5 integration." });
    }

    /// <summary>
    /// Gets the current user ID from JWT claims.
    /// </summary>
    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return claim != null && int.TryParse(claim, out var id) ? id : null;
    }
}
