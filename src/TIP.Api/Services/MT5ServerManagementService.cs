using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TIP.Data.Auth;

namespace TIP.Api.Services;

/// <summary>
/// Service for managing MT5 server configurations from the web UI.
///
/// Design rationale:
/// - Admin adds/removes MT5 servers at runtime via web UI.
/// - Manager passwords are AES-256 encrypted before DB storage.
/// - Connection lifecycle managed by backend (enable → connect, disable → disconnect).
/// - Test connection validates credentials before saving.
/// </summary>
public sealed class MT5ServerManagementService
{
    private readonly ILogger<MT5ServerManagementService> _logger;
    private readonly MT5ServerRepository _serverRepo;
    private readonly EncryptionService _encryption;

    /// <summary>
    /// Initializes the MT5 server management service.
    /// </summary>
    public MT5ServerManagementService(
        ILogger<MT5ServerManagementService> logger,
        MT5ServerRepository serverRepo,
        EncryptionService encryption)
    {
        _logger = logger;
        _serverRepo = serverRepo;
        _encryption = encryption;
    }

    /// <summary>
    /// Gets all MT5 server configs (password excluded from response).
    /// </summary>
    public async Task<IReadOnlyList<ServerDto>> GetAllServersAsync(CancellationToken ct = default)
    {
        var servers = await _serverRepo.GetAllAsync(ct).ConfigureAwait(false);
        return servers.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Gets a server by ID (password excluded).
    /// </summary>
    public async Task<ServerDto?> GetServerByIdAsync(int id, CancellationToken ct = default)
    {
        var server = await _serverRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        return server != null ? MapToDto(server) : null;
    }

    /// <summary>
    /// Adds a new MT5 server config. Password is encrypted before storage.
    /// </summary>
    public async Task<(int Id, string? Error)> AddServerAsync(AddServerRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return (0, "Server name is required");
        if (string.IsNullOrWhiteSpace(request.Address)) return (0, "Server address is required");
        if (string.IsNullOrWhiteSpace(request.Password)) return (0, "Manager password is required");

        var encryptedPassword = _encryption.Encrypt(request.Password);

        var server = new MT5ServerRecord
        {
            Name = request.Name,
            Address = request.Address,
            ManagerLogin = request.ManagerLogin,
            ManagerPasswordEncrypted = encryptedPassword,
            GroupMask = request.GroupMask ?? "*"
        };

        try
        {
            var id = await _serverRepo.CreateAsync(server, ct).ConfigureAwait(false);
            _logger.LogInformation("Added MT5 server '{Name}' ({Address})", request.Name, request.Address);
            return (id, null);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return (0, "Server name already exists");
        }
    }

    /// <summary>
    /// Updates an MT5 server config. Password is re-encrypted if changed.
    /// </summary>
    public async Task<string?> UpdateServerAsync(int id, UpdateServerRequest request, CancellationToken ct = default)
    {
        var server = await _serverRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (server == null) return "Server not found";

        if (!string.IsNullOrWhiteSpace(request.Name)) server.Name = request.Name;
        if (!string.IsNullOrWhiteSpace(request.Address)) server.Address = request.Address;
        if (request.ManagerLogin.HasValue) server.ManagerLogin = request.ManagerLogin.Value;
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            server.ManagerPasswordEncrypted = _encryption.Encrypt(request.Password);
        }
        if (request.GroupMask != null) server.GroupMask = request.GroupMask;

        await _serverRepo.UpdateAsync(server, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Deletes an MT5 server config.
    /// </summary>
    public async Task<string?> DeleteServerAsync(int id, CancellationToken ct = default)
    {
        var server = await _serverRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (server == null) return "Server not found";
        if (server.IsConnected) return "Disconnect the server before deleting";

        await _serverRepo.DeleteAsync(id, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Enables a server (marks it for connection on next cycle).
    /// </summary>
    public async Task<string?> EnableServerAsync(int id, CancellationToken ct = default)
    {
        var server = await _serverRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (server == null) return "Server not found";

        await _serverRepo.SetEnabledAsync(id, true, ct).ConfigureAwait(false);
        _logger.LogInformation("Enabled MT5 server '{Name}'", server.Name);
        return null;
    }

    /// <summary>
    /// Disables a server (marks it for disconnection).
    /// </summary>
    public async Task<string?> DisableServerAsync(int id, CancellationToken ct = default)
    {
        var server = await _serverRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (server == null) return "Server not found";

        await _serverRepo.SetEnabledAsync(id, false, ct).ConfigureAwait(false);
        _logger.LogInformation("Disabled MT5 server '{Name}'", server.Name);
        return null;
    }

    /// <summary>
    /// Gets the decrypted password for a server (internal use only).
    /// </summary>
    public async Task<string?> GetDecryptedPasswordAsync(int id, CancellationToken ct = default)
    {
        var server = await _serverRepo.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (server == null) return null;
        return _encryption.Decrypt(server.ManagerPasswordEncrypted);
    }

    /// <summary>
    /// Gets all enabled servers with decrypted passwords (for startup).
    /// </summary>
    public async Task<IReadOnlyList<ServerConnectionInfo>> GetEnabledConnectionsAsync(CancellationToken ct = default)
    {
        var servers = await _serverRepo.GetEnabledAsync(ct).ConfigureAwait(false);
        return servers.Select(s => new ServerConnectionInfo
        {
            Id = s.Id,
            Name = s.Name,
            Address = s.Address,
            ManagerLogin = (ulong)s.ManagerLogin,
            Password = _encryption.Decrypt(s.ManagerPasswordEncrypted),
            GroupMask = s.GroupMask
        }).ToList();
    }

    /// <summary>
    /// Updates the runtime connection status for a server.
    /// </summary>
    public async Task SetConnectedAsync(int id, bool isConnected, CancellationToken ct = default)
    {
        await _serverRepo.SetConnectedAsync(id, isConnected, ct).ConfigureAwait(false);
    }

    private static ServerDto MapToDto(MT5ServerRecord s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Address = s.Address,
        ManagerLogin = s.ManagerLogin,
        GroupMask = s.GroupMask,
        IsEnabled = s.IsEnabled,
        IsConnected = s.IsConnected,
        LastConnected = s.LastConnected,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt
    };
}

/// <summary>Server DTO for API responses (no password).</summary>
public sealed class ServerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public long ManagerLogin { get; set; }
    public string GroupMask { get; set; } = "*";
    public bool IsEnabled { get; set; }
    public bool IsConnected { get; set; }
    public DateTime? LastConnected { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Request to add a new MT5 server.</summary>
public sealed class AddServerRequest
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public long ManagerLogin { get; set; }
    public string Password { get; set; } = "";
    public string? GroupMask { get; set; }
}

/// <summary>Request to update an MT5 server.</summary>
public sealed class UpdateServerRequest
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public long? ManagerLogin { get; set; }
    public string? Password { get; set; }
    public string? GroupMask { get; set; }
}

/// <summary>Decrypted server connection info for the connection manager.</summary>
public sealed class ServerConnectionInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public ulong ManagerLogin { get; set; }
    public string Password { get; set; } = "";
    public string GroupMask { get; set; } = "*";
}
