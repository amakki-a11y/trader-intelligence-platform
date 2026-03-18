using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Models;
using TIP.Data;

namespace TIP.Tests.Data;

/// <summary>
/// Tests for DealRepository.GetAllDealsAsync server filtering.
/// Uses a real TimescaleDB connection to verify SQL WHERE clause logic.
/// Skipped automatically if the database is not available.
/// </summary>
[TestClass]
public class DealRepositoryServerFilterTests
{
    private const string ConnString = "Host=localhost;Port=5432;Database=tip;Username=tip_user;Password=tip_dev_2026;Include Error Detail=true";
    private static DbConnectionFactory? _dbFactory;
    private static bool _dbAvailable;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _dbFactory = new DbConnectionFactory(ConnString);
        try
        {
            using var conn = _dbFactory.OpenConnectionAsync().GetAwaiter().GetResult();
            _dbAvailable = true;
        }
        catch
        {
            _dbAvailable = false;
        }
    }

    private static DealRecord MakeDeal(ulong dealId, string server, long timeMsc = 1000000000000)
    {
        return new DealRecord
        {
            DealId = dealId,
            Login = 50001,
            TimeMsc = timeMsc,
            Symbol = "EURUSD",
            Action = 0,
            Volume = 1.0,
            Price = 1.085,
            Profit = 0,
            Commission = -3.5,
            Swap = 0,
            Fee = 0,
            Reason = 0,
            ExpertId = 0,
            Comment = "test",
            PositionId = dealId,
            Server = server
        };
    }

    private async Task CleanupTestDeals()
    {
        await using var conn = await _dbFactory!.OpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "DELETE FROM deals WHERE comment = 'test' AND login = 50001", conn);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task GetAllDealsAsync_ReturnsOnlyMatchingServer()
    {
        if (!_dbAvailable) Assert.Inconclusive("TimescaleDB not available");

        await CleanupTestDeals().ConfigureAwait(false);

        var repoInsert = new DealRepository(NullLogger<DealRepository>.Instance, _dbFactory!);

        // Insert 3 deals for server-A
        for (ulong i = 900001; i <= 900003; i++)
            await repoInsert.InsertAsync(MakeDeal(i, "server-A", 1000000000000 + (long)i)).ConfigureAwait(false);

        // Insert 2 deals for server-B
        for (ulong i = 900004; i <= 900005; i++)
            await repoInsert.InsertAsync(MakeDeal(i, "server-B", 1000000000000 + (long)i)).ConfigureAwait(false);

        // Query with server filter = "server-A"
        var repoFiltered = new DealRepository(NullLogger<DealRepository>.Instance, _dbFactory!, "server-A");
        var deals = await repoFiltered.GetAllDealsAsync().ConfigureAwait(false);

        // Should only get server-A deals (may include other test data, so filter)
        var testDeals = deals.FindAll(d => d.Comment == "test" && d.Login == 50001);
        Assert.AreEqual(3, testDeals.Count);
        Assert.IsTrue(testDeals.TrueForAll(d => d.Server == "server-A"));

        await CleanupTestDeals().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task GetAllDealsAsync_NoServerFilter_ReturnsAll()
    {
        if (!_dbAvailable) Assert.Inconclusive("TimescaleDB not available");

        await CleanupTestDeals().ConfigureAwait(false);

        var repoInsert = new DealRepository(NullLogger<DealRepository>.Instance, _dbFactory!);

        // Insert 3 deals for server-A
        for (ulong i = 900001; i <= 900003; i++)
            await repoInsert.InsertAsync(MakeDeal(i, "server-A", 1000000000000 + (long)i)).ConfigureAwait(false);

        // Insert 2 deals for server-B
        for (ulong i = 900004; i <= 900005; i++)
            await repoInsert.InsertAsync(MakeDeal(i, "server-B", 1000000000000 + (long)i)).ConfigureAwait(false);

        // Query with no server filter (empty string)
        var repoUnfiltered = new DealRepository(NullLogger<DealRepository>.Instance, _dbFactory!, "");
        var deals = await repoUnfiltered.GetAllDealsAsync().ConfigureAwait(false);

        var testDeals = deals.FindAll(d => d.Comment == "test" && d.Login == 50001);
        Assert.AreEqual(5, testDeals.Count);

        await CleanupTestDeals().ConfigureAwait(false);
    }
}
