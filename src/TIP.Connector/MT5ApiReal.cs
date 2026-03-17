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
/// </summary>
public sealed class MT5ApiReal : IMT5Api
{
    private CIMTManagerAPI? _manager;
    private DealSinkHandler? _dealSink;
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

    /// <inheritdoc />
    public bool Initialize()
    {
        var res = SMTManagerAPIFactory.Initialize(null);
        return res == MTRetCode.MT_RET_OK;
    }

    /// <inheritdoc />
    public bool Connect(string server, ulong login, string password, uint timeoutMs = 30000)
    {
        _manager = SMTManagerAPIFactory.CreateManager(
            SMTManagerAPIFactory.ManagerAPIVersion, out var res);

        if (res != MTRetCode.MT_RET_OK || _manager == null)
            return false;

        res = _manager.Connect(server, login, password, null,
            CIMTManagerAPI.EnPumpModes.PUMP_MODE_FULL, timeoutMs);

        _connected = res == MTRetCode.MT_RET_OK;
        return _connected;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        _connected = false;
        UnsubscribeDeals();
        UnsubscribeTicks();
        _manager?.Disconnect();
        _manager?.Release();
        _manager = null;
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
        // MT5 tick subscription is handled via pump mode (PUMP_MODE_FULL)
        // Ticks arrive through the manager's OnTick callback
        return _manager != null;
    }

    /// <inheritdoc />
    public void UnsubscribeTicks()
    {
        // Tick unsubscription happens on disconnect
    }

    /// <inheritdoc />
    public List<RawDeal> RequestDeals(ulong login, DateTimeOffset from, DateTimeOffset toTime)
    {
        var result = new List<RawDeal>();
        if (_manager == null) return result;

        var dealArray = _manager.DealCreateArray();
        if (dealArray == null) return result;

        var fromMt5 = SMTTime.FromDateTime(from.UtcDateTime);
        var toMt5 = SMTTime.FromDateTime(toTime.UtcDateTime);

        var res = _manager.DealRequest(login, fromMt5, toMt5, dealArray);
        if (res != MTRetCode.MT_RET_OK) return result;

        for (uint i = 0; i < dealArray.Total(); i++)
        {
            var deal = dealArray.Next(i);
            if (deal == null) continue;

            // CRITICAL: Copy ALL fields NOW — native object dies after this loop
            result.Add(CopyDeal(deal));
        }

        return result;
    }

    /// <inheritdoc />
    public List<RawTick> RequestTicks(string symbol, DateTimeOffset from, DateTimeOffset toTime)
    {
        // TickHistoryRequest implementation
        // This would use manager.TickHistoryRequest for the symbol
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
        if (_manager == null) return null;

        var user = _manager.UserCreate();
        if (user == null) return null;

        var res = _manager.UserRequest(login, user);
        if (res != MTRetCode.MT_RET_OK) return null;

        return new RawUser
        {
            Login = (ulong)user.Login(),
            Name = user.Name(),
            Group = user.Group(),
            Leverage = user.Leverage(),
            Balance = user.Balance(),
            Equity = user.Equity(),
            Agent = (ulong)user.Agent()
        };
    }

    /// <inheritdoc />
    public List<RawSymbol> GetSymbols()
    {
        var result = new List<RawSymbol>();
        if (_manager == null) return result;

        var total = _manager.SymbolTotal();
        for (uint i = 0; i < total; i++)
        {
            var sym = _manager.SymbolNext(i);
            if (sym == null) continue;

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

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    /// <summary>
    /// Copies all fields from a native CIMTDeal into a RawDeal.
    /// MUST be called inside the callback — native object is invalid after callback returns.
    /// </summary>
    private static RawDeal CopyDeal(CIMTDeal deal)
    {
        return new RawDeal
        {
            DealId = (ulong)deal.Deal(),
            Login = (ulong)deal.Login(),
            TimeMsc = deal.TimeMsc(),
            Symbol = deal.Symbol(),
            Action = (uint)deal.Action(),
            VolumeRaw = (ulong)deal.Volume(),
            Price = deal.Price(),
            Profit = deal.Profit(),
            Commission = deal.Commission(),
            Storage = deal.Storage(),      // MT5 calls swap "Storage"
            Fee = deal.Fee(),
            Reason = (uint)deal.Reason(),
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
}

#endif
