using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Settings;

/// <summary>
/// TESTPLAN §C P3 — the remaining per-bit contracts: physical index storage/locking, identity
/// seed/NFR (tables and table types), temporal retention, column ANSI-padding and dynamic data
/// masking, constraint NOT-FOR-REPLICATION, and module SET options. Same 4-part contract as P1.
/// </summary>
[TestClass]
public class PhysicalBitContractTests : MatrixTestBase
{
    private const string ThreeColTable = "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL, D INT NOT NULL)";

    // ── Index physical storage / locking ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreFillFactor_Index()
    {
        await AssertBitContractAsync(
            "Indexes.IgnoreFillFactor",
            new SchemaHashOptions { Indexes = IndexNormalization.IgnoreFillFactor },
            baseline: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C) WITH (FILLFACTOR = 80)" },
            variant: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C) WITH (FILLFACTOR = 90)" },
            sibling: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(D) WITH (FILLFACTOR = 80)" });
    }

    [TestMethod]
    public async Task IgnorePadIndex_Index()
    {
        // FILLFACTOR held equal so only PAD_INDEX (is_padded) differs.
        await AssertBitContractAsync(
            "Indexes.IgnorePadIndex",
            new SchemaHashOptions { Indexes = IndexNormalization.IgnorePadIndex },
            baseline: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C) WITH (PAD_INDEX = ON, FILLFACTOR = 80)" },
            variant: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C) WITH (PAD_INDEX = OFF, FILLFACTOR = 80)" },
            sibling: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(D) WITH (PAD_INDEX = ON, FILLFACTOR = 80)" });
    }

    [TestMethod]
    public async Task IgnoreLockOptions_Index()
    {
        await AssertBitContractAsync(
            "Indexes.IgnoreLockOptions",
            new SchemaHashOptions { Indexes = IndexNormalization.IgnoreLockOptions },
            baseline: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C) WITH (ALLOW_ROW_LOCKS = OFF, ALLOW_PAGE_LOCKS = OFF)" },
            variant: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C) WITH (ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON)" },
            sibling: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(D) WITH (ALLOW_ROW_LOCKS = OFF, ALLOW_PAGE_LOCKS = OFF)" });
    }

    [TestMethod]
    public async Task IgnoreDisabled_Index()
    {
        await AssertBitContractAsync(
            "Indexes.IgnoreDisabled",
            new SchemaHashOptions { Indexes = IndexNormalization.IgnoreDisabled },
            baseline: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C)" },
            variant: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(C)", "ALTER INDEX IX_T_C ON dbo.T DISABLE" },
            sibling: new[] { ThreeColTable, "CREATE INDEX IX_T_C ON dbo.T(D)" });
    }

    // ── Identity seed / NFR (tables and table types) ────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreIdentitySeed_Table()
    {
        await AssertBitContractAsync(
            "Tables.IgnoreIdentitySeed",
            new SchemaHashOptions { Tables = TableNormalization.IgnoreIdentitySeed },
            baseline: new[] { "CREATE TABLE dbo.T (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            variant: new[] { "CREATE TABLE dbo.T (Id INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            // sibling: the identity column's data type still participates.
            sibling: new[] { "CREATE TABLE dbo.T (Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" });
    }

    [TestMethod]
    public async Task IgnoreIdentitySeed_TableType()
    {
        // Identity bits govern a table type's identity column too.
        await AssertBitContractAsync(
            "Tables.IgnoreIdentitySeed (table type)",
            new SchemaHashOptions { Tables = TableNormalization.IgnoreIdentitySeed },
            baseline: new[] { "CREATE TYPE dbo.Lines AS TABLE (Id INT IDENTITY(1,1) NOT NULL, V INT NOT NULL)" },
            variant: new[] { "CREATE TYPE dbo.Lines AS TABLE (Id INT IDENTITY(1000,5) NOT NULL, V INT NOT NULL)" },
            sibling: new[] { "CREATE TYPE dbo.Lines AS TABLE (Id INT IDENTITY(1,1) NOT NULL, V BIGINT NOT NULL)" });
    }

    [TestMethod]
    public async Task IgnoreIdentityNotForReplication_Table()
    {
        // The column IDENTITY clause carries the NOT FOR REPLICATION flag. (Table types have no
        // meaningful replication semantics, so this surface is tables-only.)
        await AssertBitContractAsync(
            "Tables.IgnoreIdentityNotForReplication",
            new SchemaHashOptions { Tables = TableNormalization.IgnoreIdentityNotForReplication },
            baseline: new[] { "CREATE TABLE dbo.T (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            variant: new[] { "CREATE TABLE dbo.T (Id INT IDENTITY(1,1) NOT FOR REPLICATION NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            // sibling: the identity column type still participates.
            sibling: new[] { "CREATE TABLE dbo.T (Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" });
    }

    // ── Temporal retention ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreTemporalRetention_Table()
    {
        string[] Temporal(string period) => new[]
        {
            $@"CREATE TABLE dbo.Acct (
                Id INT NOT NULL CONSTRAINT PK_Acct PRIMARY KEY,
                Bal DECIMAL(18,2) NOT NULL,
                ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.AcctHistory, HISTORY_RETENTION_PERIOD = {period}))",
        };

        await AssertBitContractAsync(
            "Tables.IgnoreTemporalRetention",
            new SchemaHashOptions { Tables = TableNormalization.IgnoreTemporalRetention },
            baseline: Temporal("3 MONTHS"),
            variant: Temporal("6 MONTHS"),
            // sibling: a non-temporal table must still differ (TemporalType participates).
            sibling: new[] { "CREATE TABLE dbo.Acct (Id INT NOT NULL CONSTRAINT PK_Acct PRIMARY KEY, Bal DECIMAL(18,2) NOT NULL)" });
    }

    // ── Column ANSI padding / dynamic data masking ──────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreAnsiPadding_Column()
    {
        // is_ansi_padded on a variable-length binary column is set from the session SET ANSI_PADDING
        // in effect at CREATE time; the shared connection makes the preceding SET batch stick.
        await AssertBitContractAsync(
            "Columns.IgnoreAnsiPadding",
            new SchemaHashOptions { Columns = ColumnNormalization.IgnoreAnsiPadding },
            baseline: new[] { "SET ANSI_PADDING OFF", "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, V VARBINARY(10) NULL)" },
            variant: new[] { "SET ANSI_PADDING ON", "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, V VARBINARY(10) NULL)" },
            // sibling: a genuine length change still registers with ANSI padding ignored.
            sibling: new[] { "SET ANSI_PADDING OFF", "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, V VARBINARY(20) NULL)" });
    }

    [TestMethod]
    public async Task IgnoreDynamicDataMasking_Column()
    {
        var ddm = new SchemaHashOptions { Columns = ColumnNormalization.IgnoreDynamicDataMasking };
        string[] Ssn(string mask) => new[] { $"CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Ssn CHAR(11) {mask} NULL)" };

        var maskedDefault = await BuildSchemaAsync("ddmDefault", Ssn("MASKED WITH (FUNCTION = 'default()')"));
        var maskedPartial = await BuildSchemaAsync("ddmPartial", Ssn("MASKED WITH (FUNCTION = 'partial(1,\"XXX\",1)')"));
        var unmasked = await BuildSchemaAsync("ddmNone", Ssn(""));
        var differentType = await BuildSchemaAsync("ddmType", new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Ssn CHAR(20) NULL)" });

        // T1: masked-vs-not and two different masking functions all differ under Strict.
        Hash(maskedDefault, Strict).ShouldNotBe(Hash(unmasked, Strict), "Masked vs unmasked differs under Strict");
        Hash(maskedDefault, Strict).ShouldNotBe(Hash(maskedPartial, Strict), "Two different masking functions differ under Strict");

        // T2: the bit neutralizes both the is_masked flag and the masking function.
        Hash(maskedDefault, ddm).ShouldBe(Hash(unmasked, ddm), "IgnoreDynamicDataMasking collapses masked vs unmasked");
        Hash(maskedDefault, ddm).ShouldBe(Hash(maskedPartial, ddm), "IgnoreDynamicDataMasking collapses differing masking functions");

        // T3: a real column difference still registers with masking ignored.
        Hash(maskedDefault, ddm).ShouldNotBe(Hash(differentType, ddm), "A genuine column type/length change must still register");
    }

    // ── Constraint NOT FOR REPLICATION (FK + CHECK) ─────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreNotForReplication_ForeignKey()
    {
        var parent = "CREATE TABLE dbo.Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY)";
        string[] Child(string alter) => new[]
        {
            parent,
            "CREATE TABLE dbo.Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL)",
            alter,
        };

        // NOT FOR REPLICATION on a WITH CHECK constraint also flips is_not_trusted, so both sides use
        // WITH NOCHECK (equally untrusted) to isolate is_not_for_replication as the only difference.
        await AssertBitContractAsync(
            "Constraints.IgnoreNotForReplication (FK)",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreNotForReplication },
            baseline: Child("ALTER TABLE dbo.Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id)"),
            variant: Child("ALTER TABLE dbo.Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id) NOT FOR REPLICATION"),
            sibling: Child("ALTER TABLE dbo.Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id) ON DELETE CASCADE"));
    }

    [TestMethod]
    public async Task IgnoreNotForReplication_CheckConstraint()
    {
        string[] Table(string alter) => new[]
        {
            "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL)",
            alter,
        };

        // WITH NOCHECK on both sides holds is_not_trusted equal so is_not_for_replication is isolated.
        await AssertBitContractAsync(
            "Constraints.IgnoreNotForReplication (CHECK)",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreNotForReplication },
            baseline: Table("ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 0)"),
            variant: Table("ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK NOT FOR REPLICATION (Age >= 0)"),
            sibling: Table("ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 18)"));
    }

    // ── Module SET options ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreSetOptions_Procedure()
    {
        await AssertBitContractAsync(
            "Modules.IgnoreSetOptions",
            new SchemaHashOptions { Modules = ModuleNormalization.IgnoreSetOptions },
            baseline: new[] { "SET QUOTED_IDENTIFIER OFF", "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 AS V END" },
            variant: new[] { "SET QUOTED_IDENTIFIER ON", "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 AS V END" },
            // sibling: the body still participates when only SET options are ignored.
            sibling: new[] { "SET QUOTED_IDENTIFIER OFF", "CREATE PROCEDURE dbo.P AS BEGIN SELECT 2 AS V END" });
    }

    [TestMethod]
    public async Task IgnoreSetOptions_ParticipatesIndependentlyOfBodyText()
    {
        // SET options are captured at CREATE time and are not in the module text, so they participate
        // even when the body-text hash is dropped.
        var a = await BuildSchemaAsync("setIndepA", "SET QUOTED_IDENTIFIER OFF", "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 AS V END");
        var b = await BuildSchemaAsync("setIndepB", "SET QUOTED_IDENTIFIER ON", "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 AS V END");

        var bodyOnly = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText };
        var bodyAndSet = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText | ModuleNormalization.IgnoreSetOptions };

        Hash(a, bodyOnly).ShouldNotBe(Hash(b, bodyOnly), "SET options still differ even when the body text is ignored");
        Hash(a, bodyAndSet).ShouldBe(Hash(b, bodyAndSet), "Adding IgnoreSetOptions collapses the remaining SET-option difference");
    }
}
