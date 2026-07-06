using Dapper;
using Microsoft.Data.SqlClient;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Sequence schema fidelity: presence, start/increment/bounds/cycle/cache, data type (including an
/// alias-typed sequence), and — the key invariant — independence from current_value, which advances
/// on every NEXT VALUE FOR and is deliberately not part of the hash.
/// </summary>
[TestClass]
public class SequenceFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task AddingSequence_ChangesHash()
    {
        var dbName = await CreateTestDatabaseAsync("SeqAdd");
        var hashBefore = await ExtractAndHashAsync(dbName);

        await ExecuteSqlAsync(dbName, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 INCREMENT BY 1");
        var hashAfter = await ExtractAndHashAsync(dbName);

        hashBefore.ShouldNotBe(hashAfter, "Adding a sequence must change the hash");
    }

    [TestMethod]
    public async Task IdenticalSequence_AcrossDatabases_ProducesSameHash()
    {
        var db1 = await CreateTestDatabaseAsync("SeqSame1");
        var db2 = await CreateTestDatabaseAsync("SeqSame2");

        const string ddl = "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 INCREMENT BY 1";
        await ExecuteSqlAsync(db1, ddl);
        await ExecuteSqlAsync(db2, ddl);

        (await ExtractAndHashAsync(db1)).ShouldBe(await ExtractAndHashAsync(db2), "Identical sequences must produce the same hash");
    }

    [TestMethod]
    public async Task StartValueOrIncrementChange_ChangesHash()
    {
        var db1 = await CreateTestDatabaseAsync("SeqStart1");
        var db2 = await CreateTestDatabaseAsync("SeqStart2");
        var db3 = await CreateTestDatabaseAsync("SeqStart3");

        await ExecuteSqlAsync(db1, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 INCREMENT BY 1");
        await ExecuteSqlAsync(db2, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 100 INCREMENT BY 1");
        await ExecuteSqlAsync(db3, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 INCREMENT BY 5");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A different start value must change the hash");
        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db3), "A different increment must change the hash");
    }

    [TestMethod]
    public async Task BoundsAndCycleChange_ChangesHash()
    {
        var db1 = await CreateTestDatabaseAsync("SeqBounds1");
        var db2 = await CreateTestDatabaseAsync("SeqBounds2");
        var db3 = await CreateTestDatabaseAsync("SeqBounds3");

        await ExecuteSqlAsync(db1, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 MINVALUE 1 MAXVALUE 100 NO CYCLE");
        await ExecuteSqlAsync(db2, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 MINVALUE 0 MAXVALUE 100 NO CYCLE");
        await ExecuteSqlAsync(db3, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 MINVALUE 1 MAXVALUE 100 CYCLE");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A different MINVALUE must change the hash");
        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db3), "CYCLE vs NO CYCLE must change the hash");
    }

    [TestMethod]
    public async Task CacheOptions_AreAllPairwiseDistinct()
    {
        var dbNoCache = await CreateTestDatabaseAsync("SeqCacheNone");
        var dbDefaultCache = await CreateTestDatabaseAsync("SeqCacheDefault");
        var dbCache50 = await CreateTestDatabaseAsync("SeqCache50");

        await ExecuteSqlAsync(dbNoCache, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 NO CACHE");
        await ExecuteSqlAsync(dbDefaultCache, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 CACHE");
        await ExecuteSqlAsync(dbCache50, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 CACHE 50");

        var hashNoCache = await ExtractAndHashAsync(dbNoCache);
        var hashDefaultCache = await ExtractAndHashAsync(dbDefaultCache);
        var hashCache50 = await ExtractAndHashAsync(dbCache50);

        hashNoCache.ShouldNotBe(hashDefaultCache, "NO CACHE and default CACHE must hash differently");
        hashNoCache.ShouldNotBe(hashCache50, "NO CACHE and CACHE 50 must hash differently");
        hashDefaultCache.ShouldNotBe(hashCache50, "Default CACHE and CACHE 50 must hash differently");

        var seqNoCache = (await ExtractSchemaAsync(dbNoCache)).Sequences.Single();
        seqNoCache.IsCached.ShouldBeFalse();
        seqNoCache.CacheSize.ShouldBeNull();

        var seqDefaultCache = (await ExtractSchemaAsync(dbDefaultCache)).Sequences.Single();
        seqDefaultCache.IsCached.ShouldBeTrue();
        seqDefaultCache.CacheSize.ShouldBeNull();

        var seqCache50 = (await ExtractSchemaAsync(dbCache50)).Sequences.Single();
        seqCache50.IsCached.ShouldBeTrue();
        seqCache50.CacheSize.ShouldBe(50);
    }

    [TestMethod]
    public async Task CurrentValue_AdvancingViaNextValueFor_DoesNotChangeHash()
    {
        var dbName = await CreateTestDatabaseAsync("SeqState");
        await ExecuteSqlAsync(dbName, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1 INCREMENT BY 1");

        var hashBefore = await ExtractAndHashAsync(dbName);

        await using (var connection = new SqlConnection(GetConnectionString(dbName)))
        {
            await connection.OpenAsync();
            for (var i = 0; i < 5; i++)
                await connection.ExecuteScalarAsync<int>("SELECT NEXT VALUE FOR dbo.Seq1");
        }

        var hashAfter = await ExtractAndHashAsync(dbName);
        hashAfter.ShouldBe(hashBefore, "Advancing current_value via NEXT VALUE FOR must not change the hash — it is runtime state, not schema");
    }

    [TestMethod]
    public async Task DataTypeChange_ChangesHash_AndAliasTypeGetsBaseTypeEnrichment()
    {
        var dbInt = await CreateTestDatabaseAsync("SeqTypeInt");
        var dbBigInt = await CreateTestDatabaseAsync("SeqTypeBigInt");

        await ExecuteSqlAsync(dbInt, "CREATE SEQUENCE dbo.Seq1 AS INT START WITH 1");
        await ExecuteSqlAsync(dbBigInt, "CREATE SEQUENCE dbo.Seq1 AS BIGINT START WITH 1");

        (await ExtractAndHashAsync(dbInt)).ShouldNotBe(await ExtractAndHashAsync(dbBigInt), "A different data type must change the hash");

        var dbAlias = await CreateTestDatabaseAsync("SeqAliasType");
        await ExecuteSqlAsync(dbAlias, "CREATE TYPE dbo.Counter FROM INT NOT NULL");
        await ExecuteSqlAsync(dbAlias, "CREATE SEQUENCE dbo.Seq1 AS dbo.Counter START WITH 1");

        var sequence = (await ExtractSchemaAsync(dbAlias)).Sequences.Single();
        sequence.DataType.ShouldBe("dbo.Counter{int NOT NULL}", "A sequence over an alias type must carry its underlying base type enrichment");
    }
}
