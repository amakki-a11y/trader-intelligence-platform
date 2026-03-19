using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Orchestrates account scoring by combining RuleEngine metrics, CorrelationEngine ring data,
/// and BotFingerprinter analysis. Called after every deal to re-score the affected account.
///
/// Design rationale:
/// - Maintains an in-memory AccountAnalysis cache for all known accounts.
/// - Updates metrics incrementally per deal — never re-scans full deal history.
/// - Risk levels: Critical &gt;= 70, High &gt;= 50, Medium &gt;= 30, Low &lt; 30.
/// - Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class AccountScorer
{
    private readonly RuleEngine _ruleEngine;
    private readonly CorrelationEngine _correlationEngine;
    private readonly ILogger<AccountScorer> _logger;

    /// <summary>In-memory cache of all account analyses.</summary>
    private readonly ConcurrentDictionary<ulong, AccountAnalysis> _accounts = new();

    /// <summary>Per-account trade timestamps for timing entropy calculation.</summary>
    private readonly ConcurrentDictionary<ulong, List<long>> _tradeTimestamps = new();

    /// <summary>Per-account ExpertID set for ratio calculation.</summary>
    private readonly ConcurrentDictionary<ulong, HashSet<ulong>> _expertIds = new();

    /// <summary>Per-account expert trade count.</summary>
    private readonly ConcurrentDictionary<ulong, int> _expertTradeCount = new();

    /// <summary>
    /// Initializes the account scorer.
    /// </summary>
    /// <param name="ruleEngine">Rule engine for scoring.</param>
    /// <param name="correlationEngine">Correlation engine for ring detection.</param>
    /// <param name="logger">Logger for scoring events.</param>
    public AccountScorer(RuleEngine ruleEngine, CorrelationEngine correlationEngine, ILogger<AccountScorer> logger)
    {
        _ruleEngine = ruleEngine;
        _correlationEngine = correlationEngine;
        _logger = logger;
    }

    /// <summary>
    /// Process a new deal and update the account's score.
    /// Returns the updated AccountAnalysis with new score and risk level.
    /// </summary>
    /// <param name="dealId">Deal ticket.</param>
    /// <param name="login">Account login.</param>
    /// <param name="action">Deal action code.</param>
    /// <param name="volume">Deal volume in lots.</param>
    /// <param name="profit">Realized P&amp;L.</param>
    /// <param name="commission">Commission charged.</param>
    /// <param name="swap">Swap charged.</param>
    /// <param name="expertId">Expert Advisor ID.</param>
    /// <param name="reason">Deal reason code.</param>
    /// <param name="timeMsc">Deal time in milliseconds.</param>
    /// <param name="symbol">Trading symbol.</param>
    /// <param name="positionId">Position ticket.</param>
    /// <returns>Updated account analysis.</returns>
    public AccountAnalysis ProcessDeal(
        ulong dealId, ulong login, int action, double volume, double profit,
        double commission, double swap, ulong expertId, int reason,
        long timeMsc, string symbol, ulong positionId)
    {
        var account = _accounts.GetOrAdd(login, l => new AccountAnalysis { Login = l });

        // Update metrics based on deal type
        switch (action)
        {
            case 0: // BUY
            case 1: // SELL
                account.TotalTrades++;
                account.TotalVolume += volume;
                account.TotalCommission += Math.Abs(commission);
                account.TotalProfit += profit;

                // Track timestamps for timing entropy
                var timestamps = _tradeTimestamps.GetOrAdd(login, _ => new List<long>());
                lock (timestamps) { timestamps.Add(timeMsc); }

                // Track ExpertID
                if (expertId != 0)
                {
                    var expertCount = _expertTradeCount.AddOrUpdate(login, 1, (_, c) => c + 1);
                    var expertSet = _expertIds.GetOrAdd(login, _ => new HashSet<ulong>());
                    lock (expertSet) { expertSet.Add(expertId); }
                }

                // Check correlations
                var fingerprint = new TradeFingerprint(dealId, login, timeMsc, symbol, action, volume, expertId);
                var matches = _correlationEngine.CheckDeal(fingerprint);
                if (matches.Count > 0)
                {
                    account.IsRingMember = true;
                    account.RingCorrelationCount += matches.Count;
                    foreach (var match in matches)
                    {
                        var otherLogin = match.LoginA == login ? match.LoginB : match.LoginA;
                        if (!account.LinkedLogins.Contains(otherLogin))
                            account.LinkedLogins.Add(otherLogin);
                    }
                }
                break;

            case 2: // BALANCE
                if (profit > 0)
                {
                    account.TotalDeposits += profit;
                    account.DepositCount++;
                }
                else if (profit < 0)
                {
                    account.TotalWithdrawals += Math.Abs(profit);
                }
                break;

            case 6: // BONUS
                account.TotalBonuses += Math.Abs(profit);
                break;

            case 19: // SO_COMPENSATION
                account.SOCompensationCount++;
                break;
        }

        // Recalculate derived metrics
        RecalculateDerivedMetrics(account, login);

        // Score the account
        var previousScore = account.AbuseScore;
        account.PreviousScore = previousScore;
        account.AbuseScore = _ruleEngine.Score(account);
        account.RiskLevel = ClassifyRisk(account.AbuseScore);
        account.LastScored = DateTimeOffset.UtcNow;

        return account;
    }

    /// <summary>
    /// Run full scoring on all accounts (after historical backfill).
    /// </summary>
    public void ScoreAllAccounts()
    {
        var scored = 0;
        foreach (var kvp in _accounts)
        {
            var account = kvp.Value;
            RecalculateDerivedMetrics(account, kvp.Key);
            account.AbuseScore = _ruleEngine.Score(account);
            account.RiskLevel = ClassifyRisk(account.AbuseScore);
            account.LastScored = DateTimeOffset.UtcNow;
            scored++;
        }

        _logger.LogInformation("Scored {Count} accounts: {Critical} CRITICAL, {High} HIGH, {Medium} MEDIUM, {Low} LOW",
            scored,
            _accounts.Values.Count(a => a.RiskLevel == RiskLevel.Critical),
            _accounts.Values.Count(a => a.RiskLevel == RiskLevel.High),
            _accounts.Values.Count(a => a.RiskLevel == RiskLevel.Medium),
            _accounts.Values.Count(a => a.RiskLevel == RiskLevel.Low));
    }

    /// <summary>
    /// Get current analysis for a single account.
    /// </summary>
    /// <param name="login">Account login.</param>
    /// <returns>Account analysis, or null if not tracked.</returns>
    public AccountAnalysis? GetAccount(ulong login)
    {
        return _accounts.TryGetValue(login, out var account) ? account : null;
    }

    /// <summary>
    /// Get all accounts sorted by score descending.
    /// </summary>
    /// <returns>Sorted list of account analyses.</returns>
    public IReadOnlyList<AccountAnalysis> GetAllAccountsSorted()
    {
        return _accounts.Values
            .OrderByDescending(a => a.AbuseScore)
            .ToList();
    }

    /// <summary>
    /// Get only accounts at a specific risk level.
    /// </summary>
    /// <param name="level">Risk level filter.</param>
    /// <returns>Accounts at the specified risk level.</returns>
    public IReadOnlyList<AccountAnalysis> GetAccountsByRisk(RiskLevel level)
    {
        return _accounts.Values
            .Where(a => a.RiskLevel == level)
            .OrderByDescending(a => a.AbuseScore)
            .ToList();
    }

    /// <summary>
    /// Get accounts that are ring members.
    /// </summary>
    /// <returns>Ring member accounts.</returns>
    public IReadOnlyList<AccountAnalysis> GetRingMembers()
    {
        return _accounts.Values
            .Where(a => a.IsRingMember)
            .OrderByDescending(a => a.RingCorrelationCount)
            .ToList();
    }

    /// <summary>
    /// Gets the total number of scored accounts.
    /// </summary>
    public int AccountCount => _accounts.Count;

    /// <summary>
    /// Gets count of accounts by risk level.
    /// </summary>
    public (int Critical, int High, int Medium, int Low) GetRiskCounts()
    {
        var values = _accounts.Values;
        return (
            values.Count(a => a.RiskLevel == RiskLevel.Critical),
            values.Count(a => a.RiskLevel == RiskLevel.High),
            values.Count(a => a.RiskLevel == RiskLevel.Medium),
            values.Count(a => a.RiskLevel == RiskLevel.Low));
    }

    /// <summary>
    /// Clears all scored accounts and associated tracking data.
    /// Used before a full rescan to prevent duplicate accumulation.
    /// </summary>
    public void Reset()
    {
        _accounts.Clear();
        _tradeTimestamps.Clear();
        _expertIds.Clear();
        _expertTradeCount.Clear();
    }

    /// <summary>
    /// Recalculates derived ratio metrics from raw counters.
    /// </summary>
    private void RecalculateDerivedMetrics(AccountAnalysis account, ulong login)
    {
        if (account.TotalVolume > 0)
            account.CommissionToVolumeRatio = account.TotalCommission / account.TotalVolume;

        if (account.TotalCommission > 0)
            account.ProfitToCommissionRatio = account.TotalProfit / account.TotalCommission;

        if (account.TotalDeposits > 0)
            account.BonusToDepositRatio = account.TotalBonuses / account.TotalDeposits;

        if (account.TotalTrades > 0)
            account.AvgVolumeLots = account.TotalVolume / account.TotalTrades;

        // Expert trade ratio
        if (account.TotalTrades > 0 && _expertTradeCount.TryGetValue(login, out var expertCount))
            account.ExpertTradeRatio = (double)expertCount / account.TotalTrades;

        // Unique ExpertIDs
        if (_expertIds.TryGetValue(login, out var ids))
        {
            lock (ids) { account.UniqueExpertIds = ids.Count(id => id != 0); }
        }

        // Timing entropy CV
        if (_tradeTimestamps.TryGetValue(login, out var timestamps))
        {
            lock (timestamps) { account.TimingEntropyCV = CalculateTimingEntropy(timestamps); }
        }

        // Trades per hour
        if (_tradeTimestamps.TryGetValue(login, out var ts) && ts.Count >= 2)
        {
            lock (ts)
            {
                var spanMs = ts[^1] - ts[0];
                if (spanMs > 0)
                    account.TradesPerHour = account.TotalTrades / (spanMs / 3600000.0);
            }
        }
    }

    /// <summary>
    /// Calculate the coefficient of variation of inter-trade intervals.
    /// Humans: CV &gt; 0.3 (random gaps). Bots: CV &lt; 0.1 (constant intervals).
    /// </summary>
    private static double CalculateTimingEntropy(List<long> tradeTimestampsMs)
    {
        if (tradeTimestampsMs.Count < 3) return 1.0;

        var intervals = new List<double>();
        for (var i = 1; i < tradeTimestampsMs.Count; i++)
        {
            intervals.Add(tradeTimestampsMs[i] - tradeTimestampsMs[i - 1]);
        }

        var mean = intervals.Average();
        if (mean == 0) return 0;

        var variance = intervals.Average(x => (x - mean) * (x - mean));
        var stdDev = Math.Sqrt(variance);
        return stdDev / mean;
    }

    /// <summary>
    /// Classifies risk level based on abuse score.
    /// </summary>
    private static RiskLevel ClassifyRisk(double score) => score switch
    {
        >= 70 => RiskLevel.Critical,
        >= 50 => RiskLevel.High,
        >= 30 => RiskLevel.Medium,
        _ => RiskLevel.Low
    };
}
