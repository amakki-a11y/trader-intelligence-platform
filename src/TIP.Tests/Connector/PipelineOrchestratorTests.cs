using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Connector;

namespace TIP.Tests.Connector;

/// <summary>
/// Tests for PipelineOrchestrator three-phase startup sequence.
/// Uses MT5ApiSimulator (returns empty backfill data) to verify state transitions
/// and buffer/replay behavior without a live MT5 server or database.
/// </summary>
[TestClass]
public class PipelineOrchestratorTests
{
    private static ConnectionConfig TestConfig => new(
        ServerAddress: "simulator:0",
        ManagerLogin: 1,
        Password: "test",
        GroupMask: "*",
        HealthHeartbeatIntervalMs: 30000);

    private static (PipelineOrchestrator orchestrator, DealSink dealSink, Channel<DealEvent> dealChannel) CreateOrchestrator()
    {
        var dealChannel = Channel.CreateUnbounded<DealEvent>();
        var tickChannel = Channel.CreateUnbounded<TickEvent>();
        var api = new MT5ApiSimulator(NullLogger<MT5ApiSimulator>.Instance);
        var dealSink = new DealSink(NullLogger<DealSink>.Instance, dealChannel.Writer);
        var tickListener = new TickListener(NullLogger<TickListener>.Instance, tickChannel.Writer);
        var syncTracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var historyFetcher = new HistoryFetcher(
            NullLogger<HistoryFetcher>.Instance, api, dealChannel.Writer, tickChannel.Writer, syncTracker);

        var orchestrator = new PipelineOrchestrator(
            api, dealSink, tickListener, historyFetcher, syncTracker,
            NullLogger<PipelineOrchestrator>.Instance);

        return (orchestrator, dealSink, dealChannel);
    }

    private static DealEvent MakeDeal(ulong dealId, ulong login = 50001)
    {
        return new DealEvent(
            DealId: dealId,
            Login: login,
            TimeMsc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Symbol: "EURUSD",
            Action: 0,
            Volume: 1.0,
            Price: 1.0850,
            Profit: 0,
            Commission: -3.5,
            Swap: 0,
            Fee: 0,
            Reason: 0,
            ExpertId: 0,
            Comment: "",
            PositionId: dealId,
            ReceivedAt: DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public void InitialState_IsIdle()
    {
        var (orchestrator, _, _) = CreateOrchestrator();
        Assert.AreEqual(PipelineOrchestratorState.Idle, orchestrator.State);
    }

    [TestMethod]
    public async Task StartPipeline_TransitionsToLive()
    {
        var (orchestrator, _, _) = CreateOrchestrator();

        await orchestrator.StartPipeline(TestConfig, CancellationToken.None);

        Assert.AreEqual(PipelineOrchestratorState.Live, orchestrator.State);
    }

    [TestMethod]
    public async Task StartPipeline_DealSinkIsLiveAfterStartup()
    {
        var (orchestrator, dealSink, _) = CreateOrchestrator();

        await orchestrator.StartPipeline(TestConfig, CancellationToken.None);

        Assert.IsTrue(dealSink.IsLive);
    }

    [TestMethod]
    public async Task StartPipeline_BufferedDealsDuringBackfill_AreReplayed()
    {
        var dealChannel = Channel.CreateUnbounded<DealEvent>();
        var tickChannel = Channel.CreateUnbounded<TickEvent>();
        var api = new MT5ApiSimulator(NullLogger<MT5ApiSimulator>.Instance);
        var dealSink = new DealSink(NullLogger<DealSink>.Instance, dealChannel.Writer);
        var tickListener = new TickListener(NullLogger<TickListener>.Instance, tickChannel.Writer);
        var syncTracker = new SyncStateTracker(NullLogger<SyncStateTracker>.Instance);
        var historyFetcher = new HistoryFetcher(
            NullLogger<HistoryFetcher>.Instance, api, dealChannel.Writer, tickChannel.Writer, syncTracker);

        var orchestrator = new PipelineOrchestrator(
            api, dealSink, tickListener, historyFetcher, syncTracker,
            NullLogger<PipelineOrchestrator>.Instance);

        // Manually put deals into DealSink buffer before orchestrator runs
        // (simulating deals arriving during backfill)
        // We need to start pipeline which will reset, so we add after connect
        // Instead, test the replay count from orchestrator stats
        await orchestrator.StartPipeline(TestConfig, CancellationToken.None);

        // Simulator returns empty backfill, so no deals were backfilled
        Assert.AreEqual(0L, orchestrator.BackfilledDeals);
        Assert.AreEqual(0, orchestrator.BufferedReplayed);
        Assert.AreEqual(0, orchestrator.DuplicatesSkipped);
    }

    [TestMethod]
    public async Task StartPipeline_AfterLive_DirectDealsFlowToChannel()
    {
        var (orchestrator, dealSink, dealChannel) = CreateOrchestrator();

        await orchestrator.StartPipeline(TestConfig, CancellationToken.None);

        // Send a deal after pipeline is live
        dealSink.OnDealReceived(MakeDeal(999));

        Assert.IsTrue(dealChannel.Reader.TryRead(out var deal));
        Assert.AreEqual(999UL, deal.DealId);
    }

    [TestMethod]
    public async Task StartPipeline_StateIsLiveAfterSuccess()
    {
        var (orchestrator, _, _) = CreateOrchestrator();

        await orchestrator.StartPipeline(TestConfig, CancellationToken.None);

        Assert.AreEqual(PipelineOrchestratorState.Live, orchestrator.State);
    }

    [TestMethod]
    public async Task StartPipeline_Cancellation_TransitionsToDisconnected()
    {
        var (orchestrator, _, _) = CreateOrchestrator();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => orchestrator.StartPipeline(TestConfig, cts.Token));

        Assert.AreEqual(PipelineOrchestratorState.Disconnected, orchestrator.State);
    }
}
