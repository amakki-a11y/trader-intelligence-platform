using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Fake IMT5Api implementation that generates simulated tick and deal data.
/// Used for development and testing WITHOUT a live MT5 server.
///
/// Design rationale:
/// - Generates realistic-looking price walks for 5 symbols at ~200ms intervals.
/// - Generates random deals every 1-5 seconds across 20 simulated accounts.
/// - All callbacks fire on background threads, matching real MT5 callback behavior.
/// - Enables full end-to-end pipeline testing (connector → channel → API → dashboard).
/// </summary>
public sealed class MT5ApiSimulator : IMT5Api
{
    private readonly ILogger<MT5ApiSimulator> _logger;
    private readonly Random _random = new();
    private Timer? _tickTimer;
    private Timer? _dealTimer;
    private bool _connected;
    private bool _disposed;
    private ulong _nextDealId = 1000000;

    private static readonly SimulatedSymbol[] Symbols =
    {
        new("EURUSD",  1.0850,  0.0001,  5),
        new("GBPUSD",  1.2650,  0.0001,  5),
        new("USDJPY",  151.50,  0.01,    3),
        new("XAUUSD",  2350.00, 0.50,    2),
        new("BTCUSD",  67500.0, 10.0,    2)
    };

    private static readonly string[] DealSymbols = { "EURUSD", "GBPUSD", "USDJPY", "XAUUSD", "BTCUSD" };
    private const ulong MinLogin = 50001;
    private const ulong MaxLogin = 50020;

    /// <inheritdoc />
    public event Action<RawDeal>? OnDealAdd;

    /// <inheritdoc />
#pragma warning disable CS0067 // OnDealUpdate is part of IMT5Api contract; simulator only fires OnDealAdd
    public event Action<RawDeal>? OnDealUpdate;
#pragma warning restore CS0067

    /// <inheritdoc />
    public event Action<RawTick>? OnTick;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public string LastError { get; private set; } = "";

    /// <summary>
    /// Initializes the MT5 API simulator.
    /// </summary>
    /// <param name="logger">Logger for simulator activity.</param>
    public MT5ApiSimulator(ILogger<MT5ApiSimulator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Initialize()
    {
        _logger.LogInformation("[Simulator] MT5 API initialized (simulated)");
        return true;
    }

    /// <inheritdoc />
    public bool Connect(string server, ulong login, string password, uint timeoutMs = 30000)
    {
        _connected = true;
        _logger.LogInformation(
            "[Simulator] Connected to {Server} as login {Login} (simulated)",
            server, login);
        return true;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        _connected = false;
        StopTimers();
        _logger.LogInformation("[Simulator] Disconnected (simulated)");
    }

    /// <inheritdoc />
    public bool SubscribeDeals()
    {
        _dealTimer = new Timer(GenerateDeal, null, 1000, Timeout.Infinite);
        _logger.LogInformation("[Simulator] Deal subscription started — generating deals every 1-5 seconds");
        return true;
    }

    /// <inheritdoc />
    public void UnsubscribeDeals()
    {
        _dealTimer?.Dispose();
        _dealTimer = null;
        _logger.LogInformation("[Simulator] Deal subscription stopped");
    }

    /// <inheritdoc />
    public bool SubscribeTicks(string symbolMask = "*")
    {
        _tickTimer = new Timer(GenerateTicks, null, 200, 200);
        _logger.LogInformation(
            "[Simulator] Tick subscription started — {Count} symbols at 200ms intervals",
            Symbols.Length);
        return true;
    }

    /// <inheritdoc />
    public void UnsubscribeTicks()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
        _logger.LogInformation("[Simulator] Tick subscription stopped");
    }

    /// <inheritdoc />
    public List<RawDeal> RequestDeals(ulong login, DateTimeOffset from, DateTimeOffset toTime)
    {
        _logger.LogDebug("[Simulator] RequestDeals for login {Login} — returning empty (backfill not simulated)", login);
        return new List<RawDeal>();
    }

    /// <inheritdoc />
    public List<RawTick> RequestTicks(string symbol, DateTimeOffset from, DateTimeOffset toTime)
    {
        _logger.LogDebug("[Simulator] RequestTicks for {Symbol} — returning empty (backfill not simulated)", symbol);
        return new List<RawTick>();
    }

    /// <inheritdoc />
    public ulong[] GetUserLogins(string groupMask)
    {
        var logins = new ulong[MaxLogin - MinLogin + 1];
        for (ulong i = 0; i < (ulong)logins.Length; i++)
        {
            logins[i] = MinLogin + i;
        }
        _logger.LogDebug("[Simulator] GetUserLogins — returning {Count} simulated logins", logins.Length);
        return logins;
    }

    /// <inheritdoc />
    public RawUser? GetUser(ulong login)
    {
        if (login < MinLogin || login > MaxLogin)
            return null;

        return new RawUser
        {
            Login = login,
            Name = $"Simulated Trader {login}",
            Group = "real\\standard",
            Leverage = 100,
            Balance = 10000.0 + (login - MinLogin) * 500.0,
            Equity = 10000.0 + (login - MinLogin) * 500.0,
            Agent = login % 5 == 0 ? login - 1 : 0
        };
    }

    /// <inheritdoc />
    public List<RawSymbol> GetSymbols()
    {
        var result = new List<RawSymbol>();
        foreach (var s in Symbols)
        {
            result.Add(new RawSymbol
            {
                Symbol = s.Name,
                Description = $"{s.Name} (simulated)",
                Digits = s.Digits,
                ContractSize = s.Name.StartsWith("XAU", StringComparison.Ordinal) ? 100 : s.Name.StartsWith("BTC", StringComparison.Ordinal) ? 1 : 100000,
                TickSize = s.Step,
                TickValue = 1.0,
                CurrencyBase = s.Name[..3],
                CurrencyProfit = s.Name[3..]
            });
        }
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTimers();
    }

    private void GenerateTicks(object? state)
    {
        if (!_connected) return;

        var nowMsc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var sym in Symbols)
        {
            // Random walk: ±step with slight bias toward mean
            var direction = _random.NextDouble() > 0.5 ? 1.0 : -1.0;
            sym.CurrentBid += direction * sym.Step * (_random.NextDouble() * 3);
            var spread = sym.Step * (2 + _random.NextDouble() * 3);

            var tick = new RawTick
            {
                Symbol = sym.Name,
                Bid = Math.Round(sym.CurrentBid, sym.Digits),
                Ask = Math.Round(sym.CurrentBid + spread, sym.Digits),
                TimeMsc = nowMsc
            };

            OnTick?.Invoke(tick);
        }
    }

    private void GenerateDeal(object? state)
    {
        if (!_connected) return;

        try
        {
            var dealId = Interlocked.Increment(ref _nextDealId);
            var login = (ulong)_random.Next((int)MinLogin, (int)MaxLogin + 1);
            var symbol = DealSymbols[_random.Next(DealSymbols.Length)];
            var action = (uint)(_random.Next(2)); // 0=BUY, 1=SELL
            var volume = (ulong)(_random.Next(1, 50) * 1000); // 0.10 to 5.00 lots in raw format
            var sym = Array.Find(Symbols, s => s.Name == symbol)!;
            var price = Math.Round(sym.CurrentBid, sym.Digits);
            var nowMsc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var deal = new RawDeal
            {
                DealId = (ulong)dealId,
                Login = login,
                TimeMsc = nowMsc,
                Symbol = symbol,
                Action = action,
                VolumeRaw = volume,
                Price = price,
                Profit = Math.Round((_random.NextDouble() - 0.5) * 200, 2),
                Commission = -Math.Round(_random.NextDouble() * 5, 2),
                Storage = Math.Round((_random.NextDouble() - 0.5) * 2, 2),
                Fee = 0,
                Reason = (uint)_random.Next(2), // CLIENT or EXPERT
                ExpertId = _random.Next(3) == 0 ? (ulong)_random.Next(1000, 9999) : 0,
                Comment = "",
                PositionId = (ulong)dealId // Simplified: each deal is its own position
            };

            OnDealAdd?.Invoke(deal);
        }
        finally
        {
            // Schedule next deal in 1-5 seconds
            var nextMs = _random.Next(1000, 5001);
            _dealTimer?.Change(nextMs, Timeout.Infinite);
        }
    }

    private void StopTimers()
    {
        _tickTimer?.Dispose();
        _tickTimer = null;
        _dealTimer?.Dispose();
        _dealTimer = null;
    }

    /// <summary>
    /// Mutable state for a simulated symbol's price walk.
    /// </summary>
    private sealed class SimulatedSymbol
    {
        public string Name { get; }
        public double CurrentBid { get; set; }
        public double Step { get; }
        public int Digits { get; }

        public SimulatedSymbol(string name, double startBid, double step, int digits)
        {
            Name = name;
            CurrentBid = startBid;
            Step = step;
            Digits = digits;
        }
    }
}
