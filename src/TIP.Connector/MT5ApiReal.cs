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
        if (_manager == null) return false;

        _dealSink = new DealSinkHandler(this);
        var res = _manager.DealSubscribe(_dealSink);
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
        if (_manager == null) return false;

        _tickSink = new TickSinkHandler(this);
        var res = _manager.TickSubscribe(_tickSink);
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

        // Get equity from CIMTAccount (CIMTUser does not have Equity)
        double equity = 0;
        _account.Clear();
        if (_manager.UserAccountRequest(login, _account) == MTRetCode.MT_RET_OK)
        {
            equity = _account.Equity();
        }

        return new RawUser
        {
            Login = _user.Login(),
            Name = _user.Name(),
            Group = _user.Group(),
            Leverage = _user.Leverage(),
            Balance = _user.Balance(),
            Equity = equity,
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
            PositionId = (ulong)deal.PositionID()
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
    /// </summary>
    private sealed class TickSinkHandler : CIMTTickSink
    {
        private readonly MT5ApiReal _owner;

        public TickSinkHandler(MT5ApiReal owner) => _owner = owner;

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
    }
}

#endif
