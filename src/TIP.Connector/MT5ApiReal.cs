using System;
using System.Collections.Generic;

namespace TIP.Connector;

#if MT5_API_AVAILABLE

using MetaQuotes.MT5CommonAPI;
using MetaQuotes.MT5ManagerAPI;

/// <summary>
/// Real IMT5Api implementation wrapping the native MetaQuotes Manager API DLLs.
///
/// Design rationale:
/// - Entirely behind #if MT5_API_AVAILABLE so the solution builds without native DLLs.
/// - ALL native object fields are copied inside callbacks before they're invalidated.
/// - Volume converted from raw (1/10000) to VolumeRaw for consistent handling.
/// - Time preserved as milliseconds (TimeMsc) — conversion to DateTimeOffset happens upstream.
/// - Thread safety: MT5 callbacks fire on MT5's internal threads; we copy and fire events immediately.
/// - API signatures verified against working v1 RebateAbuseDetector code.
/// </summary>
public sealed class MT5ApiReal : IMT5Api
{
    private CIMTManagerAPI? _manager;
    private CIMTDealArray? _dealArray;
    private CIMTUser? _user;
    private CIMTAccount? _account;
    private DealSinkHandler? _dealSink;
    private TickSinkHandler? _tickSink;
    private bool _connected;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<RawDeal>? OnDealAdd;

    /// <inheritdoc />
    public event Action<RawDeal>? OnDealUpdate;

    /// <inheritdoc />
    public event Action<RawTick>? OnTick;

    /// <inheritdoc />
    public bool IsConnected => _connected && _manager != null;

    /// <summary>Last error description from MT5 API operations.</summary>
    public string LastError { get; private set; } = "";

    /// <inheritdoc />
    public bool Initialize()
    {
        var res = SMTManagerAPIFactory.Initialize(null);
        if (res != MTRetCode.MT_RET_OK)
        {
            LastError = $"MT5 API initialization failed ({res})";
            return false;
        }

        _manager = SMTManagerAPIFactory.CreateManager(
            SMTManagerAPIFactory.ManagerAPIVersion, out res);

        if (res != MTRetCode.MT_RET_OK || _manager == null)
        {
            LastError = $"Creating manager interface failed ({res})";
            SMTManagerAPIFactory.Shutdown();
            return false;
        }

        // Pre-create reusable API objects (same pattern as v1)
        _dealArray = _manager.DealCreateArray();
        _user = _manager.UserCreate();
        _account = _manager.UserCreateAccount();

        if (_dealArray == null || _user == null || _account == null)
        {
            LastError = "Failed to create API objects (DealArray/User/Account)";
            _dealArray?.Dispose();
            _user?.Dispose();
            _account?.Dispose();
            _manager.Dispose();
            _manager = null;
            SMTManagerAPIFactory.Shutdown();
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool Connect(string server, ulong login, string password, uint timeoutMs = 30000)
    {
        if (_manager == null)
        {
            LastError = "Manager not initialized — call Initialize() first";
            return false;
        }

        var res = _manager.Connect(server, login, password, null,
            CIMTManagerAPI.EnPumpModes.PUMP_MODE_FULL, timeoutMs);

        _connected = res == MTRetCode.MT_RET_OK;
        if (!_connected)
        {
            LastError = $"Connection failed ({res})";
        }
        return _connected;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        _connected = false;
        UnsubscribeDeals();
        UnsubscribeTicks();
        _manager?.Disconnect();
    }

    /// <inheritdoc />
    public bool SubscribeDeals()
    {
        if (_manager == null) { LastError = "Manager is null"; return false; }

        _dealSink = new DealSinkHandler(this);

        // RegisterSink() must be called to wire up native callbacks before subscribing
        var regRes = _dealSink.RegisterSink();
        if (regRes != MTRetCode.MT_RET_OK)
        {
            LastError = $"DealSink.RegisterSink failed: {regRes}";
            return false;
        }

        var res = _manager.DealSubscribe(_dealSink);
        if (res != MTRetCode.MT_RET_OK)
            LastError = $"DealSubscribe failed: {res}";
        return res == MTRetCode.MT_RET_OK;
    }

    /// <inheritdoc />
    public void UnsubscribeDeals()
    {
        if (_manager != null && _dealSink != null)
        {
            _manager.DealUnsubscribe(_dealSink);
            _dealSink = null;
        }
    }

    /// <inheritdoc />
    public bool SubscribeTicks(string symbolMask = "*")
    {
        if (_manager == null) { LastError = "Manager is null"; return false; }

        _tickSink = new TickSinkHandler(this);

        // RegisterSink() must be called to wire up native callbacks before subscribing
        var regRes = _tickSink.RegisterSink();
        if (regRes != MTRetCode.MT_RET_OK)
        {
            LastError = $"TickSink.RegisterSink failed: {regRes}";
            return false;
        }

        var res = _manager.TickSubscribe(_tickSink);
        if (res != MTRetCode.MT_RET_OK)
            LastError = $"TickSubscribe failed: {res}";
        return res == MTRetCode.MT_RET_OK;
    }

    /// <inheritdoc />
    public void UnsubscribeTicks()
    {
        if (_manager != null && _tickSink != null)
        {
            _manager.TickUnsubscribe(_tickSink);
            _tickSink = null;
        }
    }

    /// <inheritdoc />
    public List<RawDeal> RequestDeals(ulong login, DateTimeOffset from, DateTimeOffset toTime)
    {
        var result = new List<RawDeal>();
        if (_manager == null || _dealArray == null) return result;

        _dealArray.Clear();
        var fromMt5 = SMTTime.FromDateTime(from.UtcDateTime);
        var toMt5 = SMTTime.FromDateTime(toTime.UtcDateTime);

        var res = _manager.DealRequest(login, fromMt5, toMt5, _dealArray);
        if (res != MTRetCode.MT_RET_OK) return result;

        for (uint i = 0; i < _dealArray.Total(); i++)
        {
            var deal = _dealArray.Next(i);
            if (deal == null) continue;

            // CRITICAL: Copy ALL fields NOW — native object dies after this loop
            result.Add(CopyDeal(deal));
        }

        return result;
    }

    /// <inheritdoc />
    public List<RawTick> RequestTicks(string symbol, DateTimeOffset from, DateTimeOffset toTime)
    {
        // Tick history not used in v1 — returns empty for now
        // TODO: Phase 5 — implement TickHistoryRequest when needed
        return new List<RawTick>();
    }

    /// <inheritdoc />
    public ulong[] GetUserLogins(string groupMask)
    {
        if (_manager == null) return Array.Empty<ulong>();

        var logins = _manager.UserLogins(groupMask, out var res);
        return res == MTRetCode.MT_RET_OK && logins != null ? logins : Array.Empty<ulong>();
    }

    /// <inheritdoc />
    public RawUser? GetUser(ulong login)
    {
        if (_manager == null || _user == null || _account == null) return null;

        _user.Clear();
        var res = _manager.UserRequest(login, _user);
        if (res != MTRetCode.MT_RET_OK) return null;

        // Get equity, margin, free margin from CIMTAccount (CIMTUser does not have these)
        double equity = 0, margin = 0, freeMargin = 0, credit = 0;
        _account.Clear();
        if (_manager.UserAccountRequest(login, _account) == MTRetCode.MT_RET_OK)
        {
            equity = _account.Equity();
            margin = _account.Margin();
            freeMargin = _account.MarginFree();
            credit = _account.Credit();
        }

        return new RawUser
        {
            Login = _user.Login(),
            Name = _user.Name(),
            Group = _user.Group(),
            Leverage = _user.Leverage(),
            Balance = _user.Balance(),
            Equity = equity,
            Margin = margin,
            FreeMargin = freeMargin,
            Credit = credit,
            Currency = "USD", // CIMTUser currency method varies by SDK version
            RegistrationTime = (long)_user.Registration(),
            LastAccessTime = (long)_user.LastAccess(),
            Agent = (ulong)_user.Agent()
        };
    }

    /// <inheritdoc />
    public List<RawPosition> GetPositions(ulong login)
    {
        var result = new List<RawPosition>();
        if (_manager == null) return result;

        var posArray = _manager.PositionCreateArray();
        if (posArray == null) return result;

        var res = _manager.PositionGet(login, posArray);
        if (res != MTRetCode.MT_RET_OK)
        {
            posArray.Dispose();
            return result;
        }

        for (uint i = 0; i < posArray.Total(); i++)
        {
            var pos = posArray.Next(i);
            if (pos == null) continue;

            result.Add(new RawPosition
            {
                PositionId = pos.Position(),
                Login = pos.Login(),
                Symbol = pos.Symbol(),
                Action = pos.Action(),
                Volume = pos.Volume() / 10000.0,
                PriceOpen = pos.PriceOpen(),
                PriceCurrent = pos.PriceCurrent(),
                Profit = pos.Profit(),
                Storage = pos.Storage(),
                StopLoss = pos.PriceSL(),
                TakeProfit = pos.PriceTP(),
                TimeMsc = (long)pos.TimeCreate() * 1000,
                ExpertId = (ulong)pos.ExpertID(),
                Comment = pos.Comment()
            });
        }

        posArray.Dispose();
        return result;
    }

    /// <inheritdoc />
    public List<RawSymbol> GetSymbols()
    {
        var result = new List<RawSymbol>();
        if (_manager == null) return result;

        var total = _manager.SymbolTotal();
        var sym = _manager.SymbolCreate();
        if (sym == null) return result;

        for (uint i = 0; i < total; i++)
        {
            sym.Clear();
            var res = _manager.SymbolNext(i, sym);
            if (res != MTRetCode.MT_RET_OK) continue;

            result.Add(new RawSymbol
            {
                Symbol = sym.Symbol(),
                Description = sym.Description(),
                Digits = (int)sym.Digits(),
                ContractSize = sym.ContractSize(),
                TickSize = sym.TickSize(),
                TickValue = sym.TickValue(),
                CurrencyBase = sym.CurrencyBase(),
                CurrencyProfit = sym.CurrencyProfit()
            });
        }

        sym.Dispose();
        return result;
    }

    /// <inheritdoc />
    public RawTick? GetTickLast(string symbol)
    {
        if (_manager == null) return null;

        try
        {
            // TickLast(string symbol, out MTTickShort tick) → MTRetCode
            var res = _manager.TickLast(symbol, out MTTickShort tick);
            if (res != MTRetCode.MT_RET_OK) return null;
            if (tick.bid <= 0 && tick.ask <= 0) return null;

            return new RawTick
            {
                Symbol = symbol,
                Bid = tick.bid,
                Ask = tick.ask,
                TimeMsc = tick.datetime_msc
            };
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public RawTickStat? GetTickStat(string symbol)
    {
        if (_manager == null) return null;

        try
        {
            // MTTickStat has session-level data: bid_high, bid_low, ask_high, ask_low,
            // price_open, price_close — but NO current bid/ask fields.
            // We use price_close as best approximation for current price.
            var res = _manager.TickStat(symbol, out MTTickStat stat);
            if (res != MTRetCode.MT_RET_OK) return null;

            // Use bid_high as indicator of any session data existing
            if (stat.bid_high <= 0 && stat.ask_high <= 0 && stat.price_close <= 0) return null;

            // Best available "current" price: price_close, or midpoint of high/low
            var bid = stat.price_close > 0 ? stat.price_close : stat.bid_high;
            var ask = stat.ask_high > 0 ? stat.ask_high : bid;

            return new RawTickStat
            {
                Symbol = symbol,
                Bid = bid,
                Ask = ask,
                Last = stat.price_close,
                High = stat.bid_high,
                Low = stat.bid_low,
                TimeMsc = stat.datetime_msc
            };
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public List<RawTick> GetTickLastBatch()
    {
        var result = new List<RawTick>();
        if (_manager == null) return result;

        try
        {
            // Batch TickLast: returns array of MTTick for all symbols with recent activity.
            // The 'id' parameter is a rolling cursor — pass 0 to start fresh.
            uint id = 0;
            var ticks = _manager.TickLast(ref id, out var res);
            if (res != MTRetCode.MT_RET_OK || ticks == null) return result;

            foreach (var tick in ticks)
            {
                if (tick.bid > 0 || tick.ask > 0)
                {
                    result.Add(new RawTick
                    {
                        Symbol = tick.symbol,
                        Bid = tick.bid,
                        Ask = tick.ask,
                        TimeMsc = tick.datetime_msc
                    });
                }
            }
        }
        catch
        {
            // Batch API may not be supported on all server versions
        }

        return result;
    }

    /// <inheritdoc />
    public bool SelectedAddAll()
    {
        if (_manager == null) { LastError = "Manager is null"; return false; }

        var res = _manager.SelectedAddAll();
        if (res != MTRetCode.MT_RET_OK)
        {
            LastError = $"SelectedAddAll failed: {res}";
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _dealArray?.Dispose();
        _user?.Dispose();
        _account?.Dispose();
        _tickSink?.Dispose();
        _manager?.Dispose();
        _manager = null;
        SMTManagerAPIFactory.Shutdown();
    }

    /// <summary>
    /// Copies all fields from a native CIMTDeal into a RawDeal.
    /// MUST be called inside the callback — native object is invalid after callback returns.
    /// </summary>
    private static RawDeal CopyDeal(CIMTDeal deal)
    {
        return new RawDeal
        {
            DealId = deal.Deal(),
            Login = deal.Login(),
            TimeMsc = deal.TimeMsc(),
            Symbol = deal.Symbol(),
            Action = deal.Action(),
            VolumeRaw = (ulong)deal.Volume(),
            Price = deal.Price(),
            Profit = deal.Profit(),
            Commission = deal.Commission(),
            Storage = deal.Storage(),      // MT5 calls swap "Storage"
            Fee = deal.Fee(),
            Reason = deal.Reason(),
            ExpertId = (ulong)deal.ExpertID(),
            Comment = deal.Comment(),
            PositionId = (ulong)deal.PositionID(),
            Entry = deal.Entry()
        };
    }

    internal void FireDealAdd(RawDeal deal) => OnDealAdd?.Invoke(deal);
    internal void FireDealUpdate(RawDeal deal) => OnDealUpdate?.Invoke(deal);
    internal void FireTick(RawTick tick) => OnTick?.Invoke(tick);

    /// <summary>
    /// Internal CIMTDealSink handler that copies native deal data and fires events.
    /// </summary>
    private sealed class DealSinkHandler : CIMTDealSink
    {
        private readonly MT5ApiReal _owner;

        public DealSinkHandler(MT5ApiReal owner) => _owner = owner;

        public override void OnDealAdd(CIMTDeal deal)
        {
            var raw = CopyDeal(deal);
            _owner.FireDealAdd(raw);
        }

        public override void OnDealUpdate(CIMTDeal deal)
        {
            var raw = CopyDeal(deal);
            _owner.FireDealUpdate(raw);
        }
    }

    /// <summary>
    /// Internal CIMTTickSink handler that copies native tick data and fires OnTick events.
    ///
    /// Design rationale:
    /// - CIMTTickSink has two virtual OnTick overloads: (string, MTTickShort) and (int, MTTick).
    /// - Different MT5 server versions may call one or the other. We override both to be safe.
    /// - The (int feeder, MTTick tick) overload receives the full MTTick with symbol inside it.
    /// </summary>
    private sealed class TickSinkHandler : CIMTTickSink
    {
        private readonly MT5ApiReal _owner;

        public TickSinkHandler(MT5ApiReal owner) => _owner = owner;

        /// <summary>Overload called by some MT5 builds — receives symbol + short tick struct.</summary>
        public override void OnTick(string symbol, MTTickShort tick)
        {
            _owner.FireTick(new RawTick
            {
                Symbol = symbol,
                Bid = tick.bid,
                Ask = tick.ask,
                TimeMsc = tick.datetime_msc
            });
        }

        /// <summary>Overload called by some MT5 builds — receives feeder ID + full tick struct.</summary>
        public override void OnTick(int feeder, MTTick tick)
        {
            if (tick.bid > 0 || tick.ask > 0)
            {
                _owner.FireTick(new RawTick
                {
                    Symbol = tick.symbol,
                    Bid = tick.bid,
                    Ask = tick.ask,
                    TimeMsc = tick.datetime_msc
                });
            }
        }
    }
}

#endif
