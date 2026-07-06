using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Tests for the domain normalization enums added alongside the v2 extraction hardening. Each test
/// builds a fixture pair that differs only in one captured dimension and asserts it hashes as
/// Different under the exact (Strict) baseline and Equal once the corresponding normalization bit is
/// set. Bit-off behavior (the Strict baseline) is covered implicitly by the "differs by default" leg.
/// </summary>
[TestClass]
public class NormalizationEnumTests : IntegrationTestBase
{
    #region Index storage / lock / disabled

    [TestMethod]
    public async Task Index_FillFactor_Differs_CollapsesUnderIgnoreFillFactor()
    {
        var db1 = await CreateTestDatabaseAsync("FillFactor1");
        var db2 = await CreateTestDatabaseAsync("FillFactor2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)";
        await ExecuteSqlAsync(db1, tableSql);
        await ExecuteSqlAsync(db2, tableSql);
        await ExecuteSqlAsync(db1, "CREATE INDEX IX_T_C ON T(C) WITH (FILLFACTOR = 70)");
        await ExecuteSqlAsync(db2, "CREATE INDEX IX_T_C ON T(C) WITH (FILLFACTOR = 90)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different fill factors differ under the exact baseline");

        var ignore = new SchemaHashOptions { Indexes = IndexNormalization.IgnoreFillFactor };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreFillFactor must collapse a fill-factor-only difference");
    }

    [TestMethod]
    public async Task Index_PadIndex_Differs_CollapsesUnderIgnorePadIndex()
    {
        var db1 = await CreateTestDatabaseAsync("PadIndex1");
        var db2 = await CreateTestDatabaseAsync("PadIndex2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)";
        await ExecuteSqlAsync(db1, tableSql);
        await ExecuteSqlAsync(db2, tableSql);
        // Same fill factor so only PAD_INDEX (is_padded) differs.
        await ExecuteSqlAsync(db1, "CREATE INDEX IX_T_C ON T(C) WITH (PAD_INDEX = ON, FILLFACTOR = 80)");
        await ExecuteSqlAsync(db2, "CREATE INDEX IX_T_C ON T(C) WITH (PAD_INDEX = OFF, FILLFACTOR = 80)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "PAD_INDEX differs under the exact baseline");

        var ignore = new SchemaHashOptions { Indexes = IndexNormalization.IgnorePadIndex };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnorePadIndex must collapse a pad-index-only difference");
    }

    [TestMethod]
    public async Task Index_LockOptions_Differ_CollapseUnderIgnoreLockOptions()
    {
        var db1 = await CreateTestDatabaseAsync("LockOpt1");
        var db2 = await CreateTestDatabaseAsync("LockOpt2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)";
        await ExecuteSqlAsync(db1, tableSql);
        await ExecuteSqlAsync(db2, tableSql);
        await ExecuteSqlAsync(db1, "CREATE INDEX IX_T_C ON T(C) WITH (ALLOW_PAGE_LOCKS = OFF)");
        await ExecuteSqlAsync(db2, "CREATE INDEX IX_T_C ON T(C)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "ALLOW_PAGE_LOCKS differs under the exact baseline");

        var ignore = new SchemaHashOptions { Indexes = IndexNormalization.IgnoreLockOptions };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreLockOptions must collapse a lock-option-only difference");
    }

    [TestMethod]
    public async Task Index_Disabled_Differs_CollapsesUnderIgnoreDisabled()
    {
        // A disabled nonclustered index isolates is_disabled cleanly (no trust coupling as with constraints).
        var db1 = await CreateTestDatabaseAsync("IdxDisabled1");
        var db2 = await CreateTestDatabaseAsync("IdxDisabled2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)";
        await ExecuteSqlAsync(db1, tableSql);
        await ExecuteSqlAsync(db2, tableSql);
        await ExecuteSqlAsync(db1, "CREATE INDEX IX_T_C ON T(C)");
        await ExecuteSqlAsync(db2, "CREATE INDEX IX_T_C ON T(C)");
        await ExecuteSqlAsync(db1, "ALTER INDEX IX_T_C ON T DISABLE");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A disabled index differs under the exact baseline");

        var ignore = new SchemaHashOptions { Indexes = IndexNormalization.IgnoreDisabled };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreDisabled must collapse an index-disabled-only difference");
    }

    #endregion

    #region Constraint enforcement

    [TestMethod]
    public async Task ForeignKey_Untrusted_Differs_CollapsesUnderIgnoreTrust()
    {
        // A FK added WITH NOCHECK is enabled but untrusted (is_not_trusted = 1, is_disabled = 0),
        // isolating trust from the disabled flag.
        var db1 = await CreateTestDatabaseAsync("FkTrust1");
        var db2 = await CreateTestDatabaseAsync("FkTrust2");

        const string tablesSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL);";
        await ExecuteSqlAsync(db1, tablesSql);
        await ExecuteSqlAsync(db2, tablesSql);
        await ExecuteSqlAsync(db1, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2, "ALTER TABLE Child WITH CHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "An untrusted FK differs under the exact baseline");

        var ignore = new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreTrust };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreTrust must collapse a trust-only FK difference");
    }

    [TestMethod]
    public async Task ForeignKey_NotForReplication_Differs_CollapsesUnderIgnoreNotForReplication()
    {
        var db1 = await CreateTestDatabaseAsync("FkNfr1");
        var db2 = await CreateTestDatabaseAsync("FkNfr2");

        const string tablesSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL);";
        await ExecuteSqlAsync(db1, tablesSql);
        await ExecuteSqlAsync(db2, tablesSql);
        // A NOT FOR REPLICATION FK is always untrusted (replication bypasses it), so adding it WITH CHECK
        // would flip the trust flag too. Add BOTH FKs WITH NOCHECK so they share is_not_trusted = 1 and
        // differ ONLY in is_not_for_replication.
        await ExecuteSqlAsync(db1, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) NOT FOR REPLICATION");
        await ExecuteSqlAsync(db2, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id)");

        // Confirm the fixture isolates NOT FOR REPLICATION: the two FKs must differ ONLY in that flag.
        var fk1 = (await ExtractSchemaAsync(db1)).Tables.Single(t => t.Name == "Child").ForeignKeys.Single();
        var fk2 = (await ExtractSchemaAsync(db2)).Tables.Single(t => t.Name == "Child").ForeignKeys.Single();
        fk1.IsNotForReplication.ShouldBeTrue("db1's FK is declared NOT FOR REPLICATION");
        fk2.IsNotForReplication.ShouldBeFalse("db2's FK is not NOT FOR REPLICATION");
        fk1.IsDisabled.ShouldBe(fk2.IsDisabled, $"disabled must match (db1={fk1.IsDisabled}, db2={fk2.IsDisabled})");
        fk1.IsNotTrusted.ShouldBe(fk2.IsNotTrusted, $"trust must match to isolate NFR (db1={fk1.IsNotTrusted}, db2={fk2.IsNotTrusted})");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A NOT FOR REPLICATION FK differs under the exact baseline");

        var ignore = new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreNotForReplication };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreNotForReplication must collapse an NFR-only FK difference");
    }

    [TestMethod]
    public async Task CheckConstraint_Disabled_CollapsesOnlyUnderIgnoreDisabledAndTrust()
    {
        // Disabling a CHECK sets BOTH is_disabled and is_not_trusted (a disabled constraint is inherently
        // untrusted), so neither bit alone collapses the difference — both are required.
        var db1 = await CreateTestDatabaseAsync("CkDisabled1");
        var db2 = await CreateTestDatabaseAsync("CkDisabled2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_Age CHECK (Age > 0))";
        await ExecuteSqlAsync(db1, tableSql);
        await ExecuteSqlAsync(db2, tableSql);
        await ExecuteSqlAsync(db1, "ALTER TABLE T NOCHECK CONSTRAINT CK_Age");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A disabled CHECK differs under the exact baseline");

        var disabledOnly = new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreDisabled };
        (await ExtractAndHashAsync(db1, disabledOnly)).ShouldNotBe(await ExtractAndHashAsync(db2, disabledOnly), "IgnoreDisabled alone leaves the coupled untrusted flag differing");

        var both = new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreDisabled | ConstraintNormalization.IgnoreTrust };
        (await ExtractAndHashAsync(db1, both)).ShouldBe(await ExtractAndHashAsync(db2, both), "IgnoreDisabled | IgnoreTrust must collapse a disabled CHECK");
    }

    #endregion

    #region Column attributes

    [TestMethod]
    public async Task Column_Collation_Differs_CollapsesUnderIgnoreCollation()
    {
        var db1 = await CreateTestDatabaseAsync("Collation1");
        var db2 = await CreateTestDatabaseAsync("Collation2");

        await ExecuteSqlAsync(db1, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL)");
        await ExecuteSqlAsync(db2, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(50) COLLATE Latin1_General_CI_AS NOT NULL)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different column collations differ under the exact baseline");

        var ignore = new SchemaHashOptions { Columns = ColumnNormalization.IgnoreCollation };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreCollation must collapse a collation-only difference");
    }

    [TestMethod]
    public async Task Column_DynamicDataMasking_Differs_CollapsesUnderIgnoreDynamicDataMasking()
    {
        var db1 = await CreateTestDatabaseAsync("Masking1");
        var db2 = await CreateTestDatabaseAsync("Masking2");

        await ExecuteSqlAsync(db1, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Email VARCHAR(100) MASKED WITH (FUNCTION = 'email()') NULL)");
        await ExecuteSqlAsync(db2, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Email VARCHAR(100) NULL)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "A masked column differs under the exact baseline");

        var ignore = new SchemaHashOptions { Columns = ColumnNormalization.IgnoreDynamicDataMasking };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreDynamicDataMasking must collapse a masking-only difference");
    }

    #endregion

    #region Table-level facets

    [TestMethod]
    public async Task Table_IdentitySeed_Differs_CollapsesUnderIgnoreIdentitySeed()
    {
        var db1 = await CreateTestDatabaseAsync("IdSeed1");
        var db2 = await CreateTestDatabaseAsync("IdSeed2");

        await ExecuteSqlAsync(db1, "CREATE TABLE T (Id INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)");
        await ExecuteSqlAsync(db2, "CREATE TABLE T (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different identity seed/increment differ under the exact baseline");

        var ignore = new SchemaHashOptions { Tables = TableNormalization.IgnoreIdentitySeed };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreIdentitySeed must collapse a seed/increment-only difference");
    }

    [TestMethod]
    public async Task Table_TemporalRetention_Differs_CollapsesUnderIgnoreTemporalRetention()
    {
        var db1 = await CreateTestDatabaseAsync("Retention1");
        var db2 = await CreateTestDatabaseAsync("Retention2");

        // Identical system-versioned tables differing only in the finite history retention period.
        string TemporalSql(int months) => $@"
            CREATE TABLE T (
                Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY,
                Val INT NOT NULL,
                SysStart DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                SysEnd DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (SysStart, SysEnd)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.T_History, HISTORY_RETENTION_PERIOD = {months} MONTHS))";
        await ExecuteSqlAsync(db1, TemporalSql(6));
        await ExecuteSqlAsync(db2, TemporalSql(12));

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different temporal retention periods differ under the exact baseline");

        var ignore = new SchemaHashOptions { Tables = TableNormalization.IgnoreTemporalRetention };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreTemporalRetention must collapse a retention-only difference");
    }

    #endregion

    #region Module SET options

    [TestMethod]
    public async Task Procedure_SetOptions_Differ_CollapseUnderIgnoreSetOptions()
    {
        // uses_ansi_nulls is captured at CREATE time from the session SET options. EXEC() runs the CREATE
        // as the first statement of a nested batch under the caller's ANSI_NULLS setting.
        var db1 = await CreateTestDatabaseAsync("SetOpt1");
        var db2 = await CreateTestDatabaseAsync("SetOpt2");

        await ExecuteSqlAsync(db1, "SET ANSI_NULLS OFF; EXEC('CREATE PROCEDURE dbo.P AS SELECT 1')");
        await ExecuteSqlAsync(db2, "SET ANSI_NULLS ON; EXEC('CREATE PROCEDURE dbo.P AS SELECT 1')");

        (await ExtractAndHashAsync(db1)).ShouldNotBe(await ExtractAndHashAsync(db2), "Different CREATE-time ANSI_NULLS differ under the exact baseline");

        var ignore = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreSetOptions };
        (await ExtractAndHashAsync(db1, ignore)).ShouldBe(await ExtractAndHashAsync(db2, ignore), "IgnoreSetOptions must collapse a SET-option-only difference");
    }

    #endregion
}
