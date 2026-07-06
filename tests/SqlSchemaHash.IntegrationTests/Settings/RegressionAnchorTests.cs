using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Settings;

/// <summary>
/// TESTPLAN §C P0 — meta-invariants &amp; regression anchors.
///
/// Golden hashes pin the exact hash of one deliberately rich reference schema under the bare
/// Strict baseline and each preset. Any unintended change to field order, extraction fidelity, or
/// preset composition trips these. The pinned values are SQL-Server-version sensitive by design:
/// if the container image changes and a hash shifts, that is a signal to review, not to blindly
/// re-baseline.
///
/// The reference schema is deliberately free of system-generated names (every constraint and index
/// is explicitly named) because a system name embeds the per-database <c>object_id</c> and so is not
/// reproducible across freshly-created databases under exact (Strict) comparison. Facets that
/// inherently carry system names — temporal history indexes and table-type constraints — are covered
/// by relative-equality tests elsewhere, not pinned here. <see cref="Determinism_RichSchema_CrossDatabase"/>
/// guards this reproducibility precondition for the golden pins.
/// </summary>
[TestClass]
public class RegressionAnchorTests : MatrixTestBase
{
    // Pinned hashes for RichReferenceSchema(). Captured from a green run against the
    // mcr.microsoft.com/mssql/server:2025-latest container. See class remarks.
    private const string ExpectedStrictHash = "2:pVaLzeHJ87w+XualkpP5NUHJuMoQ6FuWJkfmAztZ1Js=";
    private const string ExpectedV1Hash = "2:9qLTFzD6bQQjlw92EMYVIFmjz51qv21AmGR3wzqZyL0=";
    private const string ExpectedV2Hash = "2:pVaLzeHJ87w+XualkpP5NUHJuMoQ6FuWJkfmAztZ1Js=";
    private const string ExpectedStructuralHash = "2:XuV+VsucLx0dmKE3pvpLnNdLE2kme2TQX4BW9a7ZFpE=";

    /// <summary>
    /// A deliberately rich, fully explicitly-named schema exercising most catalog surfaces the hasher
    /// captures: a table with a collation-pinned unique column, a masked column, a computed+persisted
    /// column, a rowguid column with a default, a sparse column, a CHECK, an XML DOCUMENT column;
    /// a table with an identity(seed) PK, an untrusted NOT-FOR-REPLICATION cascading FK, a filtered
    /// index, a nonclustered columnstore index and a disabled DESC index; a stored procedure created
    /// under a non-default SET option; and a table type with an identity column.
    /// Dependency order matters: all QUOTED_IDENTIFIER-ON DDL runs before the proc flips it OFF.
    /// </summary>
    private static string[] RichReferenceSchema() => new[]
    {
        // XML schema collection (single global element ⇒ usable as a DOCUMENT type).
        @"CREATE XML SCHEMA COLLECTION dbo.PersonSchema AS
            '<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""p"" type=""xsd:string""/></xsd:schema>'",

        @"CREATE TABLE dbo.Person (
            PersonId INT NOT NULL CONSTRAINT PK_Person PRIMARY KEY,
            Email NVARCHAR(256) COLLATE Latin1_General_CS_AS NOT NULL CONSTRAINT UQ_Person_Email UNIQUE,
            Ssn CHAR(11) MASKED WITH (FUNCTION = 'partial(0,""XXX-XX-"",4)') NULL,
            Age INT NOT NULL CONSTRAINT CK_Person_Age CHECK (Age >= 0),
            AgeDoubled AS (Age * 2) PERSISTED,
            RowGuid UNIQUEIDENTIFIER ROWGUIDCOL NOT NULL CONSTRAINT DF_Person_RowGuid DEFAULT NEWID(),
            Note NVARCHAR(200) SPARSE NULL,
            Status NVARCHAR(20) NOT NULL CONSTRAINT DF_Person_Status DEFAULT N'active',
            Doc XML (DOCUMENT dbo.PersonSchema) NULL
        )",

        @"CREATE TABLE dbo.Membership (
            MembershipId INT IDENTITY(1000,5) NOT NULL CONSTRAINT PK_Membership PRIMARY KEY,
            PersonId INT NOT NULL,
            Level INT NOT NULL,
            CreatedUtc DATETIME2 NOT NULL
        )",

        // Untrusted (WITH NOCHECK), NOT FOR REPLICATION, cascading FK.
        @"ALTER TABLE dbo.Membership WITH NOCHECK ADD CONSTRAINT FK_Membership_Person
            FOREIGN KEY (PersonId) REFERENCES dbo.Person(PersonId) ON DELETE CASCADE NOT FOR REPLICATION",

        @"CREATE INDEX IX_Membership_Person ON dbo.Membership(PersonId) WHERE Level > 0",
        @"CREATE NONCLUSTERED COLUMNSTORE INDEX CSI_Membership ON dbo.Membership(Level, CreatedUtc)",
        @"CREATE INDEX IX_Membership_Created ON dbo.Membership(CreatedUtc DESC)",
        @"ALTER INDEX IX_Membership_Created ON dbo.Membership DISABLE",

        // Stored procedure captured under a non-default SET option.
        @"SET QUOTED_IDENTIFIER OFF",
        @"CREATE PROCEDURE dbo.GetPerson @PersonId INT, @IncludeDoc BIT = 0 AS
            BEGIN SELECT PersonId FROM dbo.Person WHERE PersonId = @PersonId END",
        @"SET QUOTED_IDENTIFIER ON",

        // Table type with an identity column (table types cannot carry named constraints, so it is
        // kept constraint-free to stay reproducible across databases).
        @"CREATE TYPE dbo.OrderLineType AS TABLE (
            LineId INT IDENTITY(1,1) NOT NULL,
            Sku NVARCHAR(20) NOT NULL,
            Qty INT NOT NULL
        )",
    };

    [TestMethod]
    public async Task Determinism_RichSchema_CrossDatabase()
    {
        // Precondition for the golden pins: the reference schema must hash identically across two
        // independently-created databases (no object_id leakage, no system-generated names).
        var a = await BuildSchemaAsync("richA", RichReferenceSchema());
        var b = await BuildSchemaAsync("richB", RichReferenceSchema());

        Hash(a, Strict).ShouldBe(Hash(b, Strict), "The reference schema must be reproducible across databases under Strict");

        // No sysdiagram objects are present, so V2 (which only adds IgnoreSysDiagramObjects) must
        // produce the same hash as the bare Strict baseline.
        Hash(a, Strict).ShouldBe(Hash(a, SchemaHashOptions.V2), "With no sysdiagram objects, V2 must equal the Strict hash");
    }

    [TestMethod]
    public async Task Golden_RichReferenceSchema_StrictBaseline()
    {
        var schema = await BuildSchemaAsync("goldenStrict", RichReferenceSchema());
        Hash(schema, Strict).ShouldBe(ExpectedStrictHash,
            "The bare Strict baseline hash must remain byte-for-byte stable (all-Strict = full-fidelity back-compat invariant)");
    }

    [TestMethod]
    public async Task Golden_RichReferenceSchema_V1Preset()
    {
        var schema = await BuildSchemaAsync("goldenV1", RichReferenceSchema());
        Hash(schema, SchemaHashOptions.V1).ShouldBe(ExpectedV1Hash, "The V1 preset hash must remain stable");
    }

    [TestMethod]
    public async Task Golden_RichReferenceSchema_V2Preset()
    {
        var schema = await BuildSchemaAsync("goldenV2", RichReferenceSchema());
        Hash(schema, SchemaHashOptions.V2).ShouldBe(ExpectedV2Hash, "The V2 (Default) preset hash must remain stable");
    }

    [TestMethod]
    public async Task Golden_RichReferenceSchema_StructuralPreset()
    {
        var schema = await BuildSchemaAsync("goldenStructural", RichReferenceSchema());
        Hash(schema, SchemaHashOptions.Structural).ShouldBe(ExpectedStructuralHash, "The Structural preset hash must remain stable");
    }

    [TestMethod]
    public async Task Determinism_CreationOrderIndependent()
    {
        // The same set of (dependency-free) objects created in different orders must hash identically:
        // the calculator sorts by fully-qualified name, so nothing may leak object-creation order
        // (or catalog object_id) into the hash.
        var orderA = new[]
        {
            "CREATE TABLE dbo.Alpha (Id INT NOT NULL CONSTRAINT PK_Alpha PRIMARY KEY, Name NVARCHAR(50) NOT NULL)",
            "CREATE TABLE dbo.Beta (Id INT NOT NULL CONSTRAINT PK_Beta PRIMARY KEY, Val DECIMAL(10,2) NOT NULL)",
            "CREATE INDEX IX_Alpha_Name ON dbo.Alpha(Name)",
            "CREATE PROCEDURE dbo.ProcOne @X INT AS BEGIN SELECT @X AS Value END",
            "CREATE PROCEDURE dbo.ProcTwo AS BEGIN SELECT 1 AS One END",
            "CREATE TYPE dbo.TypeOne AS TABLE (Id INT NOT NULL, Label NVARCHAR(20) NOT NULL)",
        };
        var orderB = new[]
        {
            "CREATE TYPE dbo.TypeOne AS TABLE (Id INT NOT NULL, Label NVARCHAR(20) NOT NULL)",
            "CREATE PROCEDURE dbo.ProcTwo AS BEGIN SELECT 1 AS One END",
            "CREATE TABLE dbo.Beta (Id INT NOT NULL CONSTRAINT PK_Beta PRIMARY KEY, Val DECIMAL(10,2) NOT NULL)",
            "CREATE PROCEDURE dbo.ProcOne @X INT AS BEGIN SELECT @X AS Value END",
            "CREATE TABLE dbo.Alpha (Id INT NOT NULL CONSTRAINT PK_Alpha PRIMARY KEY, Name NVARCHAR(50) NOT NULL)",
            "CREATE INDEX IX_Alpha_Name ON dbo.Alpha(Name)",
        };

        var a = await BuildSchemaAsync("orderA", orderA);
        var b = await BuildSchemaAsync("orderB", orderB);

        Hash(a, Strict).ShouldBe(Hash(b, Strict), "Object-creation order must not affect the hash");
    }
}
