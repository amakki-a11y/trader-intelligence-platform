using System;

namespace TIP.Core.Engines;

/// <summary>
/// Real-time unrealized P&amp;L calculation engine.
///
/// Computes unrealized profit/loss by multiplying current tick prices against open positions.
/// For each open position: unrealized P&amp;L = (CurrentPrice - EntryPrice) * Volume * ContractSize * Direction.
///
/// Key algorithms:
/// - Position aggregation: Groups open deals by (Login, Symbol) to compute net position.
/// - Tick multiplication: Uses latest bid/ask from TickListener's price cache.
/// - Currency conversion: Applies deposit currency conversion for cross-currency pairs.
///
/// Performance targets:
/// - Calculate unrealized P&amp;L for 10,000 positions in under 50ms.
/// - Update on every tick for subscribed symbols without blocking the tick pipeline.
/// </summary>
public class PnLEngine
{
    /// <summary>
    /// Initializes the P&amp;L engine.
    /// </summary>
    public PnLEngine()
    {
        // TODO: Phase 3, Task 9 — Accept TickListener dependency for price cache access
        // TODO: Phase 3, Task 9 — Accept position snapshot provider
    }

    // TODO: Phase 3, Task 9 — CalculateUnrealizedPnL(ulong login) → decimal
    // TODO: Phase 3, Task 9 — CalculateAllPositions() → Dictionary<ulong, decimal>
    // TODO: Phase 3, Task 9 — OnTickUpdate(TickEvent tick) — recalculate affected positions
}
