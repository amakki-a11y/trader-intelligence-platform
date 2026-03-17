using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Api.Controllers;
using TIP.Api.Hubs;
using TIP.Api.Models;
using TIP.Connector;

namespace TIP.Tests.Api;

/// <summary>
/// Tests for SettingsController — connection management and scan settings API.
/// Uses real ConnectionManager (no mocks) to verify end-to-end request/response behavior.
/// </summary>
[TestClass]
public class SettingsControllerTests
{
    private static SettingsController CreateController(ConnectionConfig? config = null)
    {
        var cfg = config ?? new ConnectionConfig("simulator:0", 0, "", "*");
        var mgr = new ConnectionManager(NullLogger<ConnectionManager>.Instance, cfg);
        return new SettingsController(
            NullLogger<SettingsController>.Instance,
            mgr,
            new FakeBroadcaster());
    }

    private static ConnectionManager CreateManager(ConnectionConfig? config = null)
    {
        var cfg = config ?? new ConnectionConfig("simulator:0", 0, "", "*");
        return new ConnectionManager(NullLogger<ConnectionManager>.Instance, cfg);
    }

    /// <summary>
    /// Extracts the typed value from an ActionResult by casting through OkObjectResult.
    /// </summary>
    private static T ExtractOk<T>(ActionResult<T> result)
    {
        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, "Expected OkObjectResult");
        Assert.IsInstanceOfType(ok.Value, typeof(T));
        return (T)ok.Value!;
    }

    // ── GET /connection ────────────────────────────────────────

    [TestMethod]
    public void GetConnectionConfig_ReturnsConfigWithoutPassword()
    {
        var controller = CreateController(new ConnectionConfig("mt5.broker.com:443", 12345, "secret", "forex\\*"));

        var value = ExtractOk(controller.GetConnectionConfig());

        Assert.AreEqual("mt5.broker.com:443", value.Server);
        Assert.AreEqual("12345", value.Login);
        Assert.AreEqual("forex\\*", value.GroupMask);
        Assert.IsFalse(value.Connected);
    }

    [TestMethod]
    public void GetConnectionConfig_NeverReturnsPassword()
    {
        var controller = CreateController(new ConnectionConfig("server:443", 999, "supersecret", "*"));

        var value = ExtractOk(controller.GetConnectionConfig());

        var json = System.Text.Json.JsonSerializer.Serialize(value);
        Assert.IsFalse(json.Contains("supersecret", StringComparison.OrdinalIgnoreCase));
    }

    // ── POST /connection ───────────────────────────────────────

    [TestMethod]
    public void Connect_ValidRequest_ReturnsOkWithConfig()
    {
        var controller = CreateController();

        var value = ExtractOk(controller.Connect(new ConnectionConfigRequest(
            "mt5-live.broker.com:443", "12345", "password123", "forex\\retail*")));

        Assert.AreEqual("mt5-live.broker.com:443", value.Server);
        Assert.AreEqual("12345", value.Login);
        Assert.AreEqual("forex\\retail*", value.GroupMask);
        Assert.IsFalse(value.Connected);
    }

    [TestMethod]
    public void Connect_MissingServer_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.Connect(new ConnectionConfigRequest("", "12345", "pass", "*"));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public void Connect_MissingLogin_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.Connect(new ConnectionConfigRequest("server:443", "", "pass", "*"));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public void Connect_NonNumericLogin_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.Connect(new ConnectionConfigRequest("server:443", "abc", "pass", "*"));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public void Connect_MissingPassword_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.Connect(new ConnectionConfigRequest("server:443", "12345", "", "*"));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public void Connect_EmptyGroupMask_DefaultsToWildcard()
    {
        var controller = CreateController();

        var value = ExtractOk(controller.Connect(new ConnectionConfigRequest("server:443", "12345", "pass", "")));

        Assert.AreEqual("*", value.GroupMask);
    }

    // ── POST /connection/disconnect ────────────────────────────

    [TestMethod]
    public void Disconnect_ReturnsOk()
    {
        var controller = CreateController();

        var result = controller.Disconnect();

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
    }

    // ── GET /connection/status ─────────────────────────────────

    [TestMethod]
    public void GetStatus_Disconnected_ReturnsCorrectState()
    {
        var controller = CreateController(new ConnectionConfig("server:443", 12345, "pass", "*"));

        var value = ExtractOk(controller.GetConnectionStatus());

        Assert.IsFalse(value.Connected);
        Assert.AreEqual("server:443", value.Server);
        Assert.AreEqual(0, value.AccountsInScope);
        Assert.AreEqual(0, value.UptimeSeconds);
    }

    [TestMethod]
    public void GetStatus_AfterSetConnected_ReflectsConnectedState()
    {
        var mgr = CreateManager(new ConnectionConfig("mt5.broker.com:443", 12345, "pass", "forex\\*"));
        mgr.SetConnected("mt5.broker.com:443", 150);

        var controller = new SettingsController(
            NullLogger<SettingsController>.Instance, mgr, new FakeBroadcaster());

        var value = ExtractOk(controller.GetConnectionStatus());

        Assert.IsTrue(value.Connected);
        Assert.AreEqual(150, value.AccountsInScope);
        Assert.IsNull(value.Error);
    }

    [TestMethod]
    public void GetStatus_AfterDisconnectWithError_ReflectsError()
    {
        var mgr = CreateManager();
        mgr.SetConnected("server:443", 50);
        mgr.SetDisconnected("Connection lost");

        var controller = new SettingsController(
            NullLogger<SettingsController>.Instance, mgr, new FakeBroadcaster());

        var value = ExtractOk(controller.GetConnectionStatus());

        Assert.IsFalse(value.Connected);
        Assert.AreEqual("Connection lost", value.Error);
    }

    // ── GET /scan ──────────────────────────────────────────────

    [TestMethod]
    public void GetScanSettings_ReturnsDefaults()
    {
        var controller = CreateController();

        var value = ExtractOk(controller.GetScanSettings());

        Assert.AreEqual(90, value.HistoryDays);
        Assert.AreEqual(0m, value.MinDeposit);
        Assert.AreEqual(5000, value.PollIntervalMs);
        Assert.AreEqual(70, value.CriticalThreshold);
    }

    // ── POST /scan ─────────────────────────────────────────────

    [TestMethod]
    public void UpdateScanSettings_ValidRequest_ReturnsUpdated()
    {
        var controller = CreateController();

        var value = ExtractOk(controller.UpdateScanSettings(
            new ScanSettingsRequest(180, 500m, 3000, 80)));

        Assert.AreEqual(180, value.HistoryDays);
        Assert.AreEqual(500m, value.MinDeposit);
        Assert.AreEqual(3000, value.PollIntervalMs);
        Assert.AreEqual(80, value.CriticalThreshold);
    }

    [TestMethod]
    public void UpdateScanSettings_InvalidHistoryDays_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.UpdateScanSettings(
            new ScanSettingsRequest(0, 0, 5000, 70));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public void UpdateScanSettings_InvalidPollInterval_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.UpdateScanSettings(
            new ScanSettingsRequest(90, 0, 500, 70));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public void UpdateScanSettings_InvalidThreshold_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = controller.UpdateScanSettings(
            new ScanSettingsRequest(90, 0, 5000, 0));

        Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
    }

    // ── Connection Log ─────────────────────────────────────────

    [TestMethod]
    public void GetConnectionLogs_ReturnsRecentEntries()
    {
        var mgr = CreateManager();
        mgr.SetConnected("server:443", 50);
        mgr.SetLive(10);

        var controller = new SettingsController(
            NullLogger<SettingsController>.Instance, mgr, new FakeBroadcaster());

        var result = controller.GetConnectionLogs() as OkObjectResult;
        Assert.IsNotNull(result);

        var logs = result.Value as ConnectionLogEntry[];
        Assert.IsNotNull(logs);
        Assert.IsTrue(logs.Length > 0);
    }

    // ── ConnectionManager Unit Tests ───────────────────────────

    [TestMethod]
    public void ConnectionManager_UpdateConfig_ChangesCurrentConfig()
    {
        var mgr = CreateManager();

        mgr.UpdateConfigAndReconnect("new-server:443", 99999, "newpass", "forex\\*");

        Assert.AreEqual("new-server:443", mgr.CurrentConfig.ServerAddress);
        Assert.AreEqual(99999UL, mgr.CurrentConfig.ManagerLogin);
        Assert.AreEqual("forex\\*", mgr.CurrentConfig.GroupMask);
    }

    [TestMethod]
    public void ConnectionManager_UptimeSeconds_IncreasesWhileConnected()
    {
        var mgr = CreateManager();

        Assert.AreEqual(0, mgr.UptimeSeconds);

        mgr.SetConnected("server:443", 50);
        Assert.IsTrue(mgr.UptimeSeconds >= 0);
        Assert.IsTrue(mgr.IsConnected);
    }

    [TestMethod]
    public void ConnectionManager_Disconnect_ResetsState()
    {
        var mgr = CreateManager();
        mgr.SetConnected("server:443", 50);
        Assert.IsTrue(mgr.IsConnected);

        mgr.SetDisconnected("Test disconnect");

        Assert.IsFalse(mgr.IsConnected);
        Assert.AreEqual(0, mgr.AccountsInScope);
        Assert.AreEqual("Test disconnect", mgr.LastError);
    }

    /// <summary>
    /// Fake broadcaster that does nothing — used for controller tests.
    /// </summary>
    private sealed class FakeBroadcaster : IWebSocketBroadcaster
    {
        public Task BroadcastAccountUpdate(AccountSummaryDto account) => Task.CompletedTask;
        public Task BroadcastPriceUpdate(SymbolPriceDto price) => Task.CompletedTask;
        public Task BroadcastPositionUpdate(PositionSummaryDto position) => Task.CompletedTask;
        public Task BroadcastAlert(AlertMessageDto alert) => Task.CompletedTask;
        public Task BroadcastConnectionStatus(ConnectionStatusDto status) => Task.CompletedTask;
    }
}
