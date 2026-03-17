using System;

namespace TIP.Core.Engines;

/// <summary>
/// Net exposure calculation engine for risk monitoring.
///
/// Computes aggregate exposure at three levels:
/// - By symbol: Net lots across all accounts for a given instrument.
/// - By group: Net exposure for an MT5 group (e.g., all "real\standard" accounts).
/// - By book: Net exposure for A-Book vs B-Book routed positions.
///
/// Key algorithms:
/// - Netting: Long and short positions on the same symbol cancel out.
/// - Notional conversion: Lots × ContractSize × Price → USD notional value.
/// - Concentration detection: Flags when a single account holds &gt;X% of total symbol exposure.
///
/// Performance targets:
/// - Full recalculation across all positions in under 100ms.
/// - Incremental update on each deal in under 1ms.
/// </summary>
public class ExposureEngine
{
    /// <summary>
    /// Initializes the exposure engine.
    /// </summary>
    public ExposureEngine()
    {
        // TODO: Phase 3, Task 10 — Accept position and tick data dependencies
    }

    // TODO: Phase 3, Task 10 — GetSymbolExposure(string symbol) → ExposureSummary
    // TODO: Phase 3, Task 10 — GetGroupExposure(string group) → ExposureSummary
    // TODO: Phase 3, Task 10 — GetBookExposure(BookRouting routing) → ExposureSummary
    // TODO: Phase 3, Task 10 — OnDealProcessed(DealRecord deal) — incremental update
}
