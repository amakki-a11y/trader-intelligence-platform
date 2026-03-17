using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Connector;

namespace TIP.Tests.Connector;

/// <summary>
/// Tests for SyncStateTracker checkpoint tracking.
/// Uses in-memory mode (no DB connection) to verify core checkpoint logic.
/// </summary>
[TestClass]
public class SyncStateTrackerTests
{
    [TestMethod]
    public void NoCheckpoint_ReturnsNull()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);

        var result = tracker.GetLastSyncTimestamp("deal_login", "50001");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void AfterUpdateCheckpoint_ReturnsTimestamp()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var now = DateTimeOffset.UtcNow;

        tracker.UpdateCheckpoint("deal_login", "50001", now);

        var result = tracker.GetLastSyncTimestamp("deal_login", "50001");
        Assert.IsNotNull(result);
        Assert.AreEqual(now, result.Value);
    }

    [TestMethod]
    public void MultipleEntities_TrackedIndependently()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var time1 = DateTimeOffset.UtcNow.AddHours(-2);
        var time2 = DateTimeOffset.UtcNow.AddHours(-1);

        tracker.UpdateCheckpoint("deal_login", "50001", time1);
        tracker.UpdateCheckpoint("deal_login", "50002", time2);

        Assert.AreEqual(time1, tracker.GetLastSyncTimestamp("deal_login", "50001"));
        Assert.AreEqual(time2, tracker.GetLastSyncTimestamp("deal_login", "50002"));
    }

    [TestMethod]
    public void ServerParameter_DifferentiatesCheckpoints()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var time1 = DateTimeOffset.UtcNow.AddHours(-2);
        var time2 = DateTimeOffset.UtcNow.AddHours(-1);

        tracker.UpdateCheckpoint("deal_login", "50001", "server-A", time1);
        tracker.UpdateCheckpoint("deal_login", "50001", "server-B", time2);

        Assert.AreEqual(time1, tracker.GetLastSyncTimestamp("deal_login", "50001", "server-A"));
        Assert.AreEqual(time2, tracker.GetLastSyncTimestamp("deal_login", "50001", "server-B"));
    }

    [TestMethod]
    public void UpdateCheckpoint_OverwritesPreviousValue()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var time1 = DateTimeOffset.UtcNow.AddHours(-2);
        var time2 = DateTimeOffset.UtcNow;

        tracker.UpdateCheckpoint("tick_symbol", "EURUSD", time1);
        tracker.UpdateCheckpoint("tick_symbol", "EURUSD", time2);

        Assert.AreEqual(time2, tracker.GetLastSyncTimestamp("tick_symbol", "EURUSD"));
    }

    [TestMethod]
    public void DifferentEntityTypes_TrackedSeparately()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var time1 = DateTimeOffset.UtcNow.AddHours(-2);
        var time2 = DateTimeOffset.UtcNow.AddHours(-1);

        tracker.UpdateCheckpoint("deal_login", "50001", time1);
        tracker.UpdateCheckpoint("tick_symbol", "50001", time2);

        Assert.AreEqual(time1, tracker.GetLastSyncTimestamp("deal_login", "50001"));
        Assert.AreEqual(time2, tracker.GetLastSyncTimestamp("tick_symbol", "50001"));
    }

    [TestMethod]
    public void CheckpointCount_TracksCorrectly()
    {
        var tracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);

        Assert.AreEqual(0, tracker.CheckpointCount);

        tracker.UpdateCheckpoint("deal_login", "50001", DateTimeOffset.UtcNow);
        Assert.AreEqual(1, tracker.CheckpointCount);

        tracker.UpdateCheckpoint("tick_symbol", "EURUSD", DateTimeOffset.UtcNow);
        Assert.AreEqual(2, tracker.CheckpointCount);
    }
}
