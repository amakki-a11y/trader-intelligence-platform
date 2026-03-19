namespace TIP.Core.Models;

/// <summary>MT5 deal action codes from CIMTDeal.Action().</summary>
public enum DealAction : int
{
    Buy = 0, Sell = 1, Balance = 2, Credit = 3, Charge = 4,
    Correction = 5, Bonus = 6, Commission = 7, CommissionDaily = 8,
    CommissionMonthly = 9, AgentDaily = 10, AgentMonthly = 11,
    InterestRate = 12, Dividend = 13, DividendFranked = 14, Tax = 15,
    BuyCanceled = 16, SellCanceled = 17
}

/// <summary>MT5 deal entry codes from CIMTDeal.Entry().</summary>
public enum DealEntry : int
{
    In = 0, Out = 1, InOut = 2, OutBy = 3
}

/// <summary>Position direction.</summary>
public enum PositionDirection : int
{
    Buy = 0, Sell = 1
}
