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
    private const string ExpectedStrictHash = "2:vY/vRWoTqrGWuaCaii4KrQXQ6VC4cTOdf5Dh7n4KNLg=";
    private const string ExpectedV1Hash = "2:B4m0A/jBYmTJjp2Bntya/uHE0OAIN9W7Xo875N1TmMk=";
    private const string ExpectedV2Hash = "2:vY/vRWoTqrGWuaCaii4KrQXQ6VC4cTOdf5Dh7n4KNLg=";
    private const string ExpectedStructuralHash = "2:22xjNHhajNzWIcrksmRplmKVppGbGWi5PkOiZ+FVn4I=";

    /// <summary>
    /// A deliberately rich, fully explicitly-named schema exercising most catalog surfaces the hasher
    /// captures: a table with a collation-pinned unique column, a computed+persisted column, a rowguid
    /// column with a default, a sparse column, a CHECK, an XML DOCUMENT column; a table with an
    /// identity(seed) PK, a masked column, an untrusted NOT-FOR-REPLICATION cascading FK, a filtered
    /// index, a nonclustered columnstore index and a disabled DESC index; a stored procedure created
    /// under a non-default SET option; a table type with an identity column; an ordinary view; a
    /// schemabound indexed view; a scalar function over an alias-typed parameter; an inline TVF; a
    /// multi-statement TVF; a disabled DML trigger; a sequence with a non-default start/increment;
    /// a synonym; and extended properties (database-scoped plus MS_Description on a table and a column).
    /// Dependency order matters: all QUOTED_IDENTIFIER-ON DDL runs before the proc flips it OFF.
    /// </summary>
    private static string[] RichReferenceSchema() => new[]
    {
        // XML schema collection (single global element ⇒ usable as a DOCUMENT type).
        @"CREATE XML SCHEMA COLLECTION dbo.PersonSchema AS
            '<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""p"" type=""xsd:string""/></xsd:schema>'",

        // No MASKED column here: SQL Server 2019 refuses to create an index on a view that
        // references a table with any masked column, even one the view doesn't select — and
        // dbo.PersonSummary below is a schemabound indexed view over dbo.Person. The masked
        // column instead lives on dbo.Membership, which no indexed view references.
        @"CREATE TABLE dbo.Person (
            PersonId INT NOT NULL CONSTRAINT PK_Person PRIMARY KEY,
            Email NVARCHAR(256) COLLATE Latin1_General_CS_AS NOT NULL CONSTRAINT UQ_Person_Email UNIQUE,
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
            CreatedUtc DATETIME2 NOT NULL,
            Ssn CHAR(11) MASKED WITH (FUNCTION = 'partial(0,""XXX-XX-"",4)') NULL
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

        // Ordinary view.
        @"CREATE VIEW dbo.ActiveMemberships AS SELECT MembershipId, PersonId FROM dbo.Membership WHERE Level > 0",

        // Schemabound indexed view with an explicitly-named unique clustered index.
        @"CREATE VIEW dbo.PersonSummary WITH SCHEMABINDING AS SELECT PersonId, Email FROM dbo.Person",
        @"SET ARITHABORT ON",
        @"CREATE UNIQUE CLUSTERED INDEX IX_PersonSummary ON dbo.PersonSummary(PersonId)",

        // Scalar function over an alias-typed parameter.
        @"CREATE TYPE dbo.SmallAmount FROM DECIMAL(9,2) NOT NULL",
        @"CREATE FUNCTION dbo.ApplyDiscount(@amount dbo.SmallAmount) RETURNS DECIMAL(9,2) AS BEGIN RETURN @amount * 0.9 END",

        // Inline table-valued function.
        @"CREATE FUNCTION dbo.GetActiveMemberships() RETURNS TABLE AS RETURN (SELECT MembershipId, PersonId FROM dbo.Membership WHERE Level > 0)",

        // Multi-statement table-valued function.
        @"CREATE FUNCTION dbo.GetMembershipLevels() RETURNS @Result TABLE (Level INT NOT NULL) AS
            BEGIN
                INSERT INTO @Result SELECT DISTINCT Level FROM dbo.Membership
                RETURN
            END",

        // DML trigger, disabled (a non-default flag).
        @"CREATE TRIGGER dbo.trg_Membership_Audit ON dbo.Membership AFTER INSERT AS BEGIN SET NOCOUNT ON END",
        @"DISABLE TRIGGER dbo.trg_Membership_Audit ON dbo.Membership",

        // Sequence with a non-default start value and increment.
        @"CREATE SEQUENCE dbo.OrderNumberSeq AS BIGINT START WITH 1000 INCREMENT BY 5",

        // Synonym.
        @"CREATE SYNONYM dbo.PersonSyn FOR dbo.Person",

        // Extended properties: database-scoped plus MS_Description on a table and a column.
        @"EXEC sys.sp_addextendedproperty @name = N'AppVersion', @value = N'2.0'",
        @"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'People known to the system', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Person'",
        @"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Case-sensitive unique e-mail', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'Person', @level2type = N'COLUMN', @level2name = N'Email'",
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
