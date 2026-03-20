using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TIP.Api.Services;

namespace TIP.Api.Controllers;

/// <summary>
/// Authentication endpoints for login, token refresh, password change, and logout.
///
/// Design rationale:
/// - Login and refresh are [AllowAnonymous] since they issue tokens.
/// - Change-password and /me require valid JWT.
/// - Refresh token sent as httpOnly cookie for XSS protection.
/// - Access token returned in JSON body (stored in memory on frontend).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ILogger<AuthController> _logger;
    private readonly AuthService _authService;

    /// <summary>
    /// Initializes the auth controller.
    /// </summary>
    /// <param name="logger">Logger for auth endpoints.</param>
    /// <param name="authService">Authentication service.</param>
    public AuthController(ILogger<AuthController> logger, AuthService authService)
    {
        _logger = logger;
        _authService = authService;
    }

    /// <summary>
    /// Authenticates a user with username and password.
    /// Returns JWT access token in body and refresh token as httpOnly cookie.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required" });
        }

        var result = await _authService.LoginAsync(request.Username, request.Password, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Unauthorized(new { error = result.Error });
        }

        // Set refresh token as httpOnly cookie
        SetRefreshTokenCookie(result.RefreshToken!);

        return Ok(new
        {
            accessToken = result.AccessToken,
            user = result.User
        });
    }

    /// <summary>
    /// Refreshes an expired access token using the refresh token from cookie.
    /// Implements token rotation: old refresh token is invalidated.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var refreshToken = Request.Cookies["refreshToken"];
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unauthorized(new { error = "No refresh token" });
        }

        var result = await _authService.RefreshTokenAsync(refreshToken, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // Clear the invalid cookie
            Response.Cookies.Delete("refreshToken");
            return Unauthorized(new { error = result.Error });
        }

        SetRefreshTokenCookie(result.RefreshToken!);

        return Ok(new
        {
            accessToken = result.AccessToken,
            user = result.User
        });
    }

    /// <summary>
    /// Logs out by revoking the refresh token.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var refreshToken = Request.Cookies["refreshToken"];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await _authService.LogoutAsync(refreshToken, ct).ConfigureAwait(false);
        }

        Response.Cookies.Delete("refreshToken");
        return Ok(new { message = "Logged out" });
    }

    /// <summary>
    /// Changes the authenticated user's password.
    /// </summary>
    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid token" });
        }

        if (string.IsNullOrWhiteSpace(request.OldPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { error = "Old and new passwords are required" });
        }

        var (ok, error) = await _authService.ChangePasswordAsync(userId, request.OldPassword, request.NewPassword, ct).ConfigureAwait(false);

        if (!ok)
        {
            return BadRequest(new { error });
        }

        // Clear refresh token cookie since all tokens were revoked
        Response.Cookies.Delete("refreshToken");

        return Ok(new { message = "Password changed successfully. Please log in again." });
    }

    /// <summary>
    /// Returns the current authenticated user's info.
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid token" });
        }

        var user = await _authService.GetCurrentUserAsync(userId, ct).ConfigureAwait(false);
        if (user == null)
        {
            return NotFound(new { error = "User not found" });
        }

        return Ok(user);
    }

    /// <summary>
    /// Sets the refresh token as an httpOnly secure cookie.
    /// </summary>
    private void SetRefreshTokenCookie(string refreshToken)
    {
        Response.Cookies.Append("refreshToken", refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = false, // Set to true in production with HTTPS
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromDays(7),
            Path = "/api/auth"
        });
    }
}

/// <summary>
/// Login request payload.
/// </summary>
public sealed class LoginRequest
{
    /// <summary>Username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Password.</summary>
    public string Password { get; set; } = "";
}

/// <summary>
/// Change password request payload.
/// </summary>
public sealed class ChangePasswordRequest
{
    /// <summary>Current password.</summary>
    public string OldPassword { get; set; } = "";

    /// <summary>New password.</summary>
    public string NewPassword { get; set; } = "";
}
