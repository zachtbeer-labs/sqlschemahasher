using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Settings;

/// <summary>
/// TESTPLAN §C P1 — high-use bit contracts: the loosening options users actually flip to suppress
/// real cross-environment drift. Each test proves the full 4-part contract (T1 sensitivity under
/// Strict, T2 collapse under the bit, T3 no-over-collapse) via <see cref="MatrixTestBase.AssertBitContractAsync"/>,
/// except a few that need bespoke setup and assert inline.
/// </summary>
[TestClass]
public class HighUseContractTests : MatrixTestBase
{
    // ── Constraint enforcement: IgnoreTrust (the classic post-bulk-load "untrusted" drift) ──────

    [TestMethod]
    public async Task IgnoreTrust_ForeignKey()
    {
        // baseline: trusted FK (WITH CHECK). variant: same FK added untrusted (WITH NOCHECK) ⇒
        // differs only in is_not_trusted. sibling: trusted FK with a different referential action.
        var parent = "CREATE TABLE dbo.Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY)";
        string[] Child(string alter) => new[]
        {
            parent,
            "CREATE TABLE dbo.Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL)",
            alter,
        };

        await AssertBitContractAsync(
            "Constraints.IgnoreTrust (FK)",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreTrust },
            baseline: Child("ALTER TABLE dbo.Child WITH CHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id)"),
            variant: Child("ALTER TABLE dbo.Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id)"),
            sibling: Child("ALTER TABLE dbo.Child WITH CHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id) ON DELETE CASCADE"));
    }

    [TestMethod]
    public async Task IgnoreTrust_CheckConstraint()
    {
        // baseline: trusted CHECK. variant: same CHECK added untrusted (WITH NOCHECK). sibling: a
        // different predicate.
        string[] Table(string alter) => new[]
        {
            "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL)",
            alter,
        };

        await AssertBitContractAsync(
            "Constraints.IgnoreTrust (CHECK)",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreTrust },
            baseline: Table("ALTER TABLE dbo.T WITH CHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 0)"),
            variant: Table("ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 0)"),
            sibling: Table("ALTER TABLE dbo.T WITH CHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 18)"));
    }

    // ── Constraint enforcement: IgnoreDisabled ──────────────────────────────────────────────────
    // Disabling an FK/CHECK sets both is_disabled AND is_not_trusted, so to isolate the disabled
    // facet the baseline is enabled-but-untrusted (WITH NOCHECK ADD) and the variant additionally
    // disables it (NOCHECK CONSTRAINT). The pair then differs ONLY in is_disabled.

    [TestMethod]
    public async Task IgnoreDisabled_ForeignKey()
    {
        var parent = "CREATE TABLE dbo.Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY)";
        string[] Child(params string[] alters) => new[]
        {
            parent,
            "CREATE TABLE dbo.Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL)",
        }.Concat(alters).ToArray();

        var addUntrusted = "ALTER TABLE dbo.Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id)";

        await AssertBitContractAsync(
            "Constraints.IgnoreDisabled (FK)",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreDisabled },
            baseline: Child(addUntrusted),
            variant: Child(addUntrusted, "ALTER TABLE dbo.Child NOCHECK CONSTRAINT FK_Child_Parent"),
            sibling: Child("ALTER TABLE dbo.Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent(Id) ON DELETE CASCADE"));
    }

    [TestMethod]
    public async Task IgnoreDisabled_CheckConstraint()
    {
        string[] Table(params string[] alters) => new[]
        {
            "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL)",
        }.Concat(alters).ToArray();

        var addUntrusted = "ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 0)";

        await AssertBitContractAsync(
            "Constraints.IgnoreDisabled (CHECK)",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreDisabled },
            baseline: Table(addUntrusted),
            variant: Table(addUntrusted, "ALTER TABLE dbo.T NOCHECK CONSTRAINT CK_T_Age"),
            sibling: Table("ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK (Age >= 18)"));
    }

    // ── Column collation ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreCollation_Column()
    {
        await AssertBitContractAsync(
            "Columns.IgnoreCollation",
            new SchemaHashOptions { Columns = ColumnNormalization.IgnoreCollation },
            baseline: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(50) COLLATE Latin1_General_CI_AS NOT NULL)" },
            variant: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(50) COLLATE Latin1_General_CS_AS NOT NULL)" },
            // sibling: a genuine type/length change must still register even with collation ignored.
            sibling: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(60) COLLATE Latin1_General_CI_AS NOT NULL)" });
    }

    // ── Names: IgnoreNames (indexes and constraints) ────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreNames_Index()
    {
        var table = "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL, D INT NOT NULL)";
        await AssertBitContractAsync(
            "Indexes.IgnoreNames",
            new SchemaHashOptions { Indexes = IndexNormalization.IgnoreNames },
            baseline: new[] { table, "CREATE INDEX IX_Alpha ON dbo.T(C)" },
            variant: new[] { table, "CREATE INDEX IX_Beta ON dbo.T(C)" },
            // sibling: a different key column must still register with names ignored.
            sibling: new[] { table, "CREATE INDEX IX_Alpha ON dbo.T(D)" });
    }

    [TestMethod]
    public async Task IgnoreNames_Constraint()
    {
        await AssertBitContractAsync(
            "Constraints.IgnoreNames",
            new SchemaHashOptions { Constraints = ConstraintNormalization.IgnoreNames },
            baseline: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL, Age INT NOT NULL CONSTRAINT CK_Alpha CHECK (Age >= 0))" },
            variant: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL, Age INT NOT NULL CONSTRAINT CK_Beta CHECK (Age >= 0))" },
            // sibling: a different predicate must still register with names ignored.
            sibling: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL, Age INT NOT NULL CONSTRAINT CK_Alpha CHECK (Age >= 18))" });
    }

    [TestMethod]
    public async Task IgnoreNames_Pk_CollapsesInBothDomains()
    {
        // A PK surfaces as BOTH an index name and a key-constraint name, so neutralizing it needs
        // both domains' IgnoreNames set at once.
        var loosened = new SchemaHashOptions { Indexes = IndexNormalization.IgnoreNames, Constraints = ConstraintNormalization.IgnoreNames };
        await AssertBitContractAsync(
            "Indexes.IgnoreNames + Constraints.IgnoreNames (PK)",
            loosened,
            baseline: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_Alpha PRIMARY KEY, C INT NOT NULL)" },
            variant: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_Beta PRIMARY KEY, C INT NOT NULL)" },
            // sibling: a different PK key column set must still register.
            sibling: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL, C INT NOT NULL, CONSTRAINT PK_Alpha PRIMARY KEY (Id, C))" });
    }

    // ── Names: NormalizeAutoGeneratedNames (bespoke — needs divergent system-name suffixes) ──────

    [TestMethod]
    public async Task NormalizeAutoGeneratedNames_SystemNamedPk()
    {
        // System PK names derive their hex suffix from the object id; two pristine databases can mint
        // identical names, so perturb the second's object ids first to force differing suffixes.
        const string tableSql = "CREATE TABLE dbo.Assets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL)";

        var a = await BuildSchemaAsync("autoA", tableSql);
        var b = await BuildSchemaAsync("autoB",
            "CREATE TABLE dbo.Dummy (Id INT NOT NULL PRIMARY KEY)",
            "DROP TABLE dbo.Dummy",
            tableSql);
        // Explicit-name control: a PK with a real name must NOT be collapsed by the bit.
        var explicitName = await BuildSchemaAsync("autoExplicit", "CREATE TABLE dbo.Assets (Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY, Name NVARCHAR(100) NOT NULL)");

        // A PK surfaces in both domains, so normalize both.
        var normalized = new SchemaHashOptions { Indexes = IndexNormalization.NormalizeAutoGeneratedNames, Constraints = ConstraintNormalization.NormalizeAutoGeneratedNames };

        Hash(a, Strict).ShouldNotBe(Hash(b, Strict), "T1: system PK names with different hex suffixes differ under Strict");
        Hash(a, normalized).ShouldBe(Hash(b, normalized), "T2: NormalizeAutoGeneratedNames neutralizes system-generated PK name suffixes");
        Hash(a, normalized).ShouldNotBe(Hash(explicitName, normalized), "T3: an explicitly-named PK must stay distinct from a system-named one");
    }

    // ── Index clustering ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task NormalizeClustering_RowstorePlacement()
    {
        await AssertBitContractAsync(
            "Indexes.NormalizeClustering",
            new SchemaHashOptions { Indexes = IndexNormalization.NormalizeClustering },
            baseline: new[]
            {
                "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY CLUSTERED, Name NVARCHAR(100) NOT NULL)",
                "CREATE NONCLUSTERED INDEX IX_T_Name ON dbo.T(Name)",
            },
            variant: new[]
            {
                "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY NONCLUSTERED, Name NVARCHAR(100) NOT NULL)",
                "CREATE CLUSTERED INDEX IX_T_Name ON dbo.T(Name)",
            });
    }

    [TestMethod]
    public async Task NormalizeClustering_ColumnstoreDistinctionPreserved()
    {
        // NormalizeClustering collapses rowstore CLUSTERED≡NONCLUSTERED only; clustered vs
        // nonclustered COLUMNSTORE are fundamentally different storage and must stay distinct.
        var normalized = new SchemaHashOptions { Indexes = IndexNormalization.NormalizeClustering };

        var nonclustered = await BuildSchemaAsync("csNci",
            "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Val INT NOT NULL)",
            "CREATE NONCLUSTERED COLUMNSTORE INDEX CSI_T ON dbo.T(Val)");
        var clustered = await BuildSchemaAsync("csCci",
            "CREATE TABLE dbo.T (Val INT NOT NULL)",
            "CREATE CLUSTERED COLUMNSTORE INDEX CSI_T ON dbo.T");

        Hash(nonclustered, normalized).ShouldNotBe(Hash(clustered, normalized),
            "Clustered vs nonclustered columnstore must remain distinct even under NormalizeClustering");
    }

    // ── Index key sort order ────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreSortOrder_Index()
    {
        var table = "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL, B INT NOT NULL)";
        await AssertBitContractAsync(
            "Indexes.IgnoreSortOrder",
            new SchemaHashOptions { Indexes = IndexNormalization.IgnoreSortOrder },
            baseline: new[] { table, "CREATE INDEX IX_T_AB ON dbo.T(A ASC, B ASC)" },
            variant: new[] { table, "CREATE INDEX IX_T_AB ON dbo.T(A DESC, B DESC)" },
            // sibling: key column ORDER stays significant even when direction is ignored.
            sibling: new[] { table, "CREATE INDEX IX_T_AB ON dbo.T(B ASC, A ASC)" });
    }

    // ── Table column order ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreColumnOrder_Table()
    {
        await AssertBitContractAsync(
            "Tables.IgnoreColumnOrder",
            new SchemaHashOptions { Tables = TableNormalization.IgnoreColumnOrder },
            baseline: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha INT NOT NULL, Beta NVARCHAR(20) NULL)" },
            variant: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Beta NVARCHAR(20) NULL, Alpha INT NOT NULL)" },
            // sibling: a genuinely different column must still register even with order ignored.
            sibling: new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha BIGINT NOT NULL, Beta NVARCHAR(20) NULL)" });
    }

    [TestMethod]
    public async Task IgnoreColumnOrder_TableTypeOrderStillSignificant()
    {
        // A TVP marshals positionally, so IgnoreColumnOrder (tables-only) must NOT relax table-type
        // column order.
        var loosened = new SchemaHashOptions { Tables = TableNormalization.IgnoreColumnOrder };
        var a = await BuildSchemaAsync("udtOrderA", "CREATE TYPE dbo.OrderLine AS TABLE (Sku NVARCHAR(20) NOT NULL, Qty INT NOT NULL)");
        var b = await BuildSchemaAsync("udtOrderB", "CREATE TYPE dbo.OrderLine AS TABLE (Qty INT NOT NULL, Sku NVARCHAR(20) NOT NULL)");

        Hash(a, loosened).ShouldNotBe(Hash(b, loosened), "IgnoreColumnOrder must not relax table-type (TVP) column order");
    }

    // ── Module body text ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task IgnoreBodyText_Procedure()
    {
        await AssertBitContractAsync(
            "Modules.IgnoreBodyText",
            new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText },
            baseline: new[] { "CREATE PROCEDURE dbo.P @X INT AS BEGIN SELECT @X AS Value END" },
            variant: new[] { "CREATE PROCEDURE dbo.P @X INT AS BEGIN SELECT @X + 1 AS Value END" },
            // sibling: a different parameter signature must still register with body text ignored.
            sibling: new[] { "CREATE PROCEDURE dbo.P @X BIGINT AS BEGIN SELECT @X AS Value END" });
    }
}
