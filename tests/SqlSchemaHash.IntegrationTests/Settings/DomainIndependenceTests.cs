using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Settings;

/// <summary>
/// TESTPLAN §C P3 — domain independence (orthogonality). Loosening one domain must never suppress a
/// distinction that belongs to another domain: the five normalization enums are grouped precisely so
/// each governs its own area and nothing else. Each test loosens one domain and asserts that a change
/// in a *different* domain still moves the hash.
/// </summary>
[TestClass]
public class DomainIndependenceTests : MatrixTestBase
{
    private async Task AssertStillDiffersAsync(string because, SchemaHashOptions loosened, string[] a, string[] b)
    {
        var sa = await BuildSchemaAsync("indepA", a);
        var sb = await BuildSchemaAsync("indepB", b);
        Hash(sa, loosened).ShouldNotBe(Hash(sb, loosened), because);
    }

    [TestMethod]
    public Task IndexesStructural_LeavesColumnCollationIntact() =>
        AssertStillDiffersAsync(
            "Indexes.Structural must not touch column collation",
            new SchemaHashOptions { Indexes = IndexNormalization.Structural },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(50) COLLATE Latin1_General_CI_AS NOT NULL)" },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Name NVARCHAR(50) COLLATE Latin1_General_CS_AS NOT NULL)" });

    [TestMethod]
    public Task IndexesStructural_LeavesProcBodyIntact() =>
        AssertStillDiffersAsync(
            "Indexes.Structural must not touch stored procedure bodies",
            new SchemaHashOptions { Indexes = IndexNormalization.Structural },
            new[] { "CREATE PROCEDURE dbo.P AS BEGIN SELECT 1 AS V END" },
            new[] { "CREATE PROCEDURE dbo.P AS BEGIN SELECT 2 AS V END" });

    [TestMethod]
    public Task TablesStructural_LeavesIndexFillFactorIntact() =>
        AssertStillDiffersAsync(
            "Tables.Structural (column order) must not touch index fill factor",
            new SchemaHashOptions { Tables = TableNormalization.Structural },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)", "CREATE INDEX IX_T_C ON dbo.T(C) WITH (FILLFACTOR = 80)" },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)", "CREATE INDEX IX_T_C ON dbo.T(C) WITH (FILLFACTOR = 90)" });

    [TestMethod]
    public Task TablesStructural_LeavesConstraintTrustIntact() =>
        AssertStillDiffersAsync(
            "Tables.Structural must not touch constraint enforcement state",
            new SchemaHashOptions { Tables = TableNormalization.Structural },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL, Age INT NOT NULL)", "ALTER TABLE dbo.T WITH CHECK ADD CONSTRAINT CK_T_Age CHECK (Age > 0)" },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL, Age INT NOT NULL)", "ALTER TABLE dbo.T WITH NOCHECK ADD CONSTRAINT CK_T_Age CHECK (Age > 0)" });

    [TestMethod]
    public Task ConstraintsStructural_LeavesColumnTypeIntact() =>
        AssertStillDiffersAsync(
            "Constraints.Structural (names) must not touch column types",
            new SchemaHashOptions { Constraints = ConstraintNormalization.Structural },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C BIGINT NOT NULL)" });

    [TestMethod]
    public Task ConstraintsStructural_LeavesIdentitySeedIntact() =>
        AssertStillDiffersAsync(
            "Constraints.Structural must not touch identity seed/increment",
            new SchemaHashOptions { Constraints = ConstraintNormalization.Structural },
            new[] { "CREATE TABLE dbo.T (Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            new[] { "CREATE TABLE dbo.T (Id INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" });

    [TestMethod]
    public Task ColumnsIgnoreCollation_LeavesIndexNameIntact() =>
        AssertStillDiffersAsync(
            "Columns.IgnoreCollation must not touch index names",
            new SchemaHashOptions { Columns = ColumnNormalization.IgnoreCollation },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)", "CREATE INDEX IX_Alpha ON dbo.T(C)" },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)", "CREATE INDEX IX_Beta ON dbo.T(C)" });

    [TestMethod]
    public Task ModulesIgnoreBodyText_LeavesTableColumnsIntact() =>
        AssertStillDiffersAsync(
            "Modules.IgnoreBodyText must not touch table columns",
            new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL)" },
            new[] { "CREATE TABLE dbo.T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, C INT NOT NULL, D INT NOT NULL)" });
}
