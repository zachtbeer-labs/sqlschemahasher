using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests;

/// <summary>
/// Tests covering the v2 comparison settings: index key sort order, index name handling,
/// sysdiagram exclusion, and the V1/V2/Structural presets.
/// </summary>
[TestClass]
public class PresetAndNormalizationTests : IntegrationTestBase
{
    #region Index Key Sort Order

    [TestMethod]
    public async Task IndexSortOrder_DescVsAsc_ChangesHash_UnderDefault()
    {
        var db1Name = await CreateTestDatabaseAsync("SortOrder1");
        var db2Name = await CreateTestDatabaseAsync("SortOrder2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )";

        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Created ON Assets(Created DESC)");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Created ON Assets(Created ASC)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A DESC index key is a different physical index than an ASC one and must change the hash under default options");
    }

    [TestMethod]
    public async Task IndexSortOrder_DescVsAsc_SameHash_UnderIgnoreIndexSortOrder()
    {
        var db1Name = await CreateTestDatabaseAsync("SortOrderIgnore1");
        var db2Name = await CreateTestDatabaseAsync("SortOrderIgnore2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )";

        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Created ON Assets(Created DESC)");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Created ON Assets(Created ASC)");

        var ignoreOptions = new SchemaHashOptions { Indexes = IndexNormalization.IgnoreSortOrder };
        (await ExtractAndHashAsync(db1Name, ignoreOptions)).ShouldBe(await ExtractAndHashAsync(db2Name, ignoreOptions), "IndexNormalization.IgnoreSortOrder must make ASC and DESC keys compare equal");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.V1)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.V1), "The V1 preset never compared sort order");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "The Structural preset ignores index key sort order");
    }

    [TestMethod]
    public async Task ColumnOrder_Swapped_SameHash_UnderIgnoreColumnOrder()
    {
        var db1Name = await CreateTestDatabaseAsync("ColOrderIgnore1");
        var db2Name = await CreateTestDatabaseAsync("ColOrderIgnore2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha INT NOT NULL, Beta NVARCHAR(20) NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Beta NVARCHAR(20) NULL, Alpha INT NOT NULL)");

        // Default: order is significant.
        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "Column order participates by default");

        var ignoreOptions = new SchemaHashOptions { Tables = TableNormalization.IgnoreColumnOrder };
        (await ExtractAndHashAsync(db1Name, ignoreOptions)).ShouldBe(await ExtractAndHashAsync(db2Name, ignoreOptions), "IgnoreColumnOrder must make the same columns in a different order compare equal");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "The Structural preset ignores column order");
    }

    [TestMethod]
    public async Task ColumnOrder_ChangedColumn_StillChangesHash_UnderIgnoreColumnOrder()
    {
        // Ignoring order must not collapse a genuine column difference: only position is relaxed.
        var db1Name = await CreateTestDatabaseAsync("ColOrderReal1");
        var db2Name = await CreateTestDatabaseAsync("ColOrderReal2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Alpha BIGINT NOT NULL)");

        var ignoreOptions = new SchemaHashOptions { Tables = TableNormalization.IgnoreColumnOrder };
        (await ExtractAndHashAsync(db1Name, ignoreOptions)).ShouldNotBe(await ExtractAndHashAsync(db2Name, ignoreOptions), "IgnoreColumnOrder relaxes position only — a changed column type must still change the hash");
    }

    [TestMethod]
    public async Task ColumnOrder_TableType_Swapped_ChangesHash_EvenUnderIgnoreColumnOrder()
    {
        // A TVP marshals positionally, so column order is part of its wire contract; IgnoreColumnOrder
        // (and the Structural preset) apply to tables only and must NOT relax table-type column order.
        var db1Name = await CreateTestDatabaseAsync("UdtColOrderIgnore1");
        var db2Name = await CreateTestDatabaseAsync("UdtColOrderIgnore2");

        await ExecuteSqlAsync(db1Name, "CREATE TYPE dbo.OrderLine AS TABLE (Sku NVARCHAR(20) NOT NULL, Qty INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TYPE dbo.OrderLine AS TABLE (Qty INT NOT NULL, Sku NVARCHAR(20) NOT NULL)");

        var ignoreOptions = new SchemaHashOptions { Tables = TableNormalization.IgnoreColumnOrder };
        (await ExtractAndHashAsync(db1Name, ignoreOptions)).ShouldNotBe(await ExtractAndHashAsync(db2Name, ignoreOptions), "IgnoreColumnOrder must not relax table-type column order (TVP positional marshalling)");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldNotBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "Structural must not relax table-type column order either");
    }

    [TestMethod]
    public async Task IndexSortOrder_SameMetadata_ComputeHashRespectsOption()
    {
        // Direction must be captured in SchemaMetadata so the same extracted schema can be
        // hashed with sort order respected or ignored.
        var dbName = await CreateTestDatabaseAsync("SortOrderMeta");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )");
        await ExecuteSqlAsync(dbName, "CREATE INDEX IX_Assets_Created ON Assets(Created DESC)");

        var schema = await ExtractSchemaAsync(dbName);

        var index = schema.Tables.Single(t => t.Name == "Assets").Indexes.Single(i => i.Name == "IX_Assets_Created");
        index.KeyColumns.Count.ShouldBe(1, "The index has a single key column");
        index.KeyColumns[0].Name.ShouldBe("Created", "The key column name must be captured");
        index.KeyColumns[0].IsDescendingKey.ShouldBeTrue("A descending key column must be flagged as descending");

        var hashWithDirection = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2);
        var hashIgnoringDirection = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V1);

        hashWithDirection.ShouldNotBe(hashIgnoringDirection, "The same metadata must hash differently under V2 (direction compared) and V1 (direction ignored) when a DESC index exists");
    }

    #endregion

    #region Clustering Type

    [TestMethod]
    public async Task ClusteringType_ClusteredVsNonclustered_SameHash_UnderStructural()
    {
        var db1Name = await CreateTestDatabaseAsync("Clustering1");
        var db2Name = await CreateTestDatabaseAsync("Clustering2");

        // PK nonclustered + clustered index vs PK clustered + nonclustered index on the same columns
        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY CLUSTERED,
                Name NVARCHAR(100) NOT NULL
            )");
        await ExecuteSqlAsync(db1Name, "CREATE NONCLUSTERED INDEX IX_Assets_Name ON Assets(Name)");

        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY NONCLUSTERED,
                Name NVARCHAR(100) NOT NULL
            )");
        await ExecuteSqlAsync(db2Name, "CREATE CLUSTERED INDEX IX_Assets_Name ON Assets(Name)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "Clustering placement is a physical schema difference and must change the hash under default options");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "The Structural preset must not distinguish clustered from nonclustered indexes");
    }

    #endregion

    #region Index Name Handling

    [TestMethod]
    public async Task SystemNamedPk_VsExplicitlyNamedPk_SameHash_UnderStructural()
    {
        var db1Name = await CreateTestDatabaseAsync("PkName1");
        var db2Name = await CreateTestDatabaseAsync("PkName2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE Assets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE Assets (Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY, Name NVARCHAR(100) NOT NULL)");

        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.V2)).ShouldNotBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.V2), "V2 compares index names exactly, so a system-named PK differs from an explicitly-named one");
        (await ExtractAndHashAsync(db1Name, SchemaHashOptions.Structural)).ShouldBe(await ExtractAndHashAsync(db2Name, SchemaHashOptions.Structural), "Structural ignores index names entirely, so only the PK definition matters");
    }

    [TestMethod]
    public async Task SystemNamedPk_DifferentHexSuffixes_SameHash_UnderNormalizeAutoGeneratedIndexNames()
    {
        var db1Name = await CreateTestDatabaseAsync("PkSuffix1");
        var db2Name = await CreateTestDatabaseAsync("PkSuffix2");

        // System-generated PK names derive their hex suffix from the object id. Two pristine
        // databases running identical DDL can mint identical system names, so perturb db2's
        // object ids first to guarantee the suffixes differ.
        await ExecuteSqlAsync(db2Name, "CREATE TABLE Dummy (Id INT NOT NULL PRIMARY KEY)");
        await ExecuteSqlAsync(db2Name, "DROP TABLE Dummy");

        const string tableSql = "CREATE TABLE Assets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL)";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        var exactOptions = new SchemaHashOptions();
        // A PK surfaces both as an index name and a key-constraint name, so normalize both domains.
        var normalizedOptions = new SchemaHashOptions { Indexes = IndexNormalization.NormalizeAutoGeneratedNames, Constraints = ConstraintNormalization.NormalizeAutoGeneratedNames };

        (await ExtractAndHashAsync(db1Name, exactOptions)).ShouldNotBe(await ExtractAndHashAsync(db2Name, exactOptions), "System-generated PK names with different hex suffixes must differ under exact comparison");
        (await ExtractAndHashAsync(db1Name, normalizedOptions)).ShouldBe(await ExtractAndHashAsync(db2Name, normalizedOptions), "NormalizeAutoGeneratedNames must neutralize system-generated PK name suffixes");
    }

    [TestMethod]
    public void IsAutoGeneratedName_DetectsKnownFormats()
    {
        SchemaHashCalculator.IsAutoGeneratedName("PK__TableNam__3214EC07A1B2C3D4").ShouldBeTrue("SQL Server system PK name (16-hex suffix)");
        SchemaHashCalculator.IsAutoGeneratedName("DF__Tab__Col__8E1F2D3C").ShouldBeTrue("SQL Server system default constraint name (8-hex suffix)");
        SchemaHashCalculator.IsAutoGeneratedName("nci_wi_Asset_EF8A0893C0DB8B0FC4AD9EABBE187744").ShouldBeTrue("GUID-suffixed missing-index name");
        SchemaHashCalculator.IsAutoGeneratedName("PK_Assets").ShouldBeFalse("Explicit PK name");
        SchemaHashCalculator.IsAutoGeneratedName("IX_Assets_Name").ShouldBeFalse("Explicit index name");
        SchemaHashCalculator.IsAutoGeneratedName("").ShouldBeFalse("Empty name");
    }

    [TestMethod]
    public void IsAutoGeneratedName_RejectsOrdinaryNamesWithShortHexSuffix()
    {
        // Regression: the GUID heuristic must require a long (>= 16-char) hex suffix. An explicit
        // index name whose final segment is a short 8-char hex run must NOT be treated as
        // auto-generated, otherwise NormalizeAutoGeneratedIndexNames would erase a real name.
        SchemaHashCalculator.IsAutoGeneratedName("IX_Audit_Record_CAFEBABE").ShouldBeFalse("An 8-hex final segment is too short to be a generated GUID suffix and must not be normalized");
        // 16- and 32-hex generated suffixes remain detected (the established contract).
        SchemaHashCalculator.IsAutoGeneratedName("IX_Employees_Name_ABC12345DEF67890").ShouldBeTrue("A 16-hex generated suffix is auto-generated");
        SchemaHashCalculator.IsAutoGeneratedName("nci_wi_Asset_EF8A0893C0DB8B0FC4AD9EABBE187744").ShouldBeTrue("A full 32-hex GUID suffix is auto-generated");
    }

    #endregion

    #region Filtered Indexes

    [TestMethod]
    public async Task FilteredIndex_DifferentPredicate_ChangesHash()
    {
        // The predicate lives in sys.indexes.filter_definition; two indexes that differ only in
        // their WHERE clause are different indexes and must hash differently.
        var db1Name = await CreateTestDatabaseAsync("Filter1");
        var db2Name = await CreateTestDatabaseAsync("Filter2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Status INT NOT NULL
            )";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Status ON Assets(Status) WHERE Status > 0");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Status ON Assets(Status) WHERE Status > 5");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A different filtered-index predicate is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task FilteredIndex_VsUnfiltered_ChangesHash_AndPredicateIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("FilterVsPlain1");
        var db2Name = await CreateTestDatabaseAsync("FilterVsPlain2");

        const string tableSql = @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Status INT NOT NULL
            )";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, "CREATE INDEX IX_Assets_Status ON Assets(Status) WHERE Status > 0");
        await ExecuteSqlAsync(db2Name, "CREATE INDEX IX_Assets_Status ON Assets(Status)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A filtered index must hash differently from an otherwise-identical unfiltered index");

        var filtered = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "Assets").Indexes.Single(i => i.Name == "IX_Assets_Status");
        filtered.FilterDefinition.ShouldNotBeNull("The filter predicate must be captured for a filtered index");
        filtered.FilterDefinition.ShouldContain("Status", customMessage: "The captured filter predicate must reference the filtered column");

        var unfiltered = (await ExtractSchemaAsync(db2Name)).Tables.Single(t => t.Name == "Assets").Indexes.Single(i => i.Name == "IX_Assets_Status");
        unfiltered.FilterDefinition.ShouldBeNull("An unfiltered index must have a null filter predicate");
    }

    #endregion

    #region Sysdiagram Objects

    [TestMethod]
    public async Task SysDiagrams_Table_IgnoredOnlyWhenToggleSet()
    {
        var db1Name = await CreateTestDatabaseAsync("Diagrams1");
        var db2Name = await CreateTestDatabaseAsync("Diagrams2");

        const string tableSql = "CREATE TABLE Assets (Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY)";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE sysdiagrams (
                name sysname NOT NULL,
                principal_id int NOT NULL,
                diagram_id int IDENTITY(1,1) PRIMARY KEY,
                version int,
                definition varbinary(max)
            )");

        var ignoreOptions = new SchemaHashOptions { IgnoreSysDiagramObjects = true };
        var neutralOptions = new SchemaHashOptions();

        (await ExtractAndHashAsync(db1Name, ignoreOptions)).ShouldBe(await ExtractAndHashAsync(db2Name, ignoreOptions), "IgnoreSysDiagramObjects must exclude the sysdiagrams table from the hash");
        (await ExtractAndHashAsync(db1Name, neutralOptions)).ShouldNotBe(await ExtractAndHashAsync(db2Name, neutralOptions), "The neutral baseline excludes nothing, so the sysdiagrams table must affect the hash");

        var schema = await ExtractSchemaAsync(db1Name, ignoreOptions);
        schema.Tables.Any(t => t.Name.Equals("sysdiagrams", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("sysdiagrams must be omitted from extraction when the toggle is set");
    }

    [TestMethod]
    public async Task IgnoreSysDiagramObjects_ComposesWithUserIgnoreSet()
    {
        var dbName = await CreateTestDatabaseAsync("DiagramsCompose");

        await ExecuteSqlAsync(dbName, "CREATE TABLE Assets (Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY)");
        await ExecuteSqlAsync(dbName, "CREATE TABLE MyTable (Id INT NOT NULL CONSTRAINT PK_MyTable PRIMARY KEY)");
        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE sysdiagrams (
                name sysname NOT NULL,
                principal_id int NOT NULL,
                diagram_id int IDENTITY(1,1) PRIMARY KEY,
                version int,
                definition varbinary(max)
            )");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyTable" }, IgnoreSysDiagramObjects = true };
        var schema = await ExtractSchemaAsync(dbName, options);

        schema.Tables.Any(t => t.Name.Equals("MyTable", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("A user-supplied ignore set must still apply alongside the diagram toggle");
        schema.Tables.Any(t => t.Name.Equals("sysdiagrams", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("Diagram exclusions must compose additively with the user-supplied ignore set");
        schema.Tables.Any(t => t.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue("Unrelated tables must still be extracted");
    }

    [TestMethod]
    public async Task UserDefinedTableType_IsExcluded_WhenInObjectNamesToIgnore()
    {
        // UDT extraction must honor ObjectNamesToIgnore, consistent with tables and procedures.
        var dbName = await CreateTestDatabaseAsync("UdtIgnore");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.IgnoredType AS TABLE (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.KeptType AS TABLE (Id INT NOT NULL)");

        var options = new SchemaHashOptions { ObjectNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IgnoredType" } };
        var schema = await ExtractSchemaAsync(dbName, options);

        schema.UserDefinedTableTypes.Any(u => u.Name.Equals("IgnoredType", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse("A UDT named in ObjectNamesToIgnore must be excluded, consistent with tables and procedures");
        schema.UserDefinedTableTypes.Any(u => u.Name.Equals("KeptType", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue("Unrelated UDTs must still be extracted");
    }

    #endregion

    #region Presets

    [TestMethod]
    public void Default_IsAliasForV2_AndPresetsReturnFreshInstances()
    {
        var defaults = SchemaHashOptions.Default;
        var v2 = SchemaHashOptions.V2;

        defaults.IgnoreSysDiagramObjects.ShouldBe(v2.IgnoreSysDiagramObjects);
        defaults.Tables.ShouldBe(v2.Tables);
        defaults.Columns.ShouldBe(v2.Columns);
        defaults.Indexes.ShouldBe(v2.Indexes);
        defaults.Constraints.ShouldBe(v2.Constraints);
        defaults.Modules.ShouldBe(v2.Modules);
        defaults.SchemaFilter.ShouldBe(v2.SchemaFilter);
        defaults.ObjectNamesToIgnore.ShouldBe(v2.ObjectNamesToIgnore);

        // Mutating one preset instance must not affect subsequent accesses
        var first = SchemaHashOptions.V2;
        first.Indexes = IndexNormalization.IgnoreSortOrder;
        SchemaHashOptions.V2.Indexes.ShouldBe(IndexNormalization.Strict, "Preset properties must return a fresh instance on every access");
    }

    #endregion

    #region Envelope version

    [TestMethod]
    public async Task Envelope_SameSchemaDifferentPresets_ShareVersion_ButCompareAsDifferent()
    {
        // The envelope does not encode the comparison options: two hashes of the same database under
        // different presets share the version but produce different hashes, so Compare reports Different
        // (NOT Incomparable). This documents the deliberate design — callers must use matching options;
        // an options mismatch is indistinguishable from a genuine schema difference.
        var dbName = await CreateTestDatabaseAsync("EnvelopeDifferentPresets");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )");
        await ExecuteSqlAsync(dbName, "CREATE INDEX IX_Assets_Created ON Assets(Created DESC)");

        var schema = await ExtractSchemaAsync(dbName);

        var v2 = SchemaHashResult.Parse(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2));
        var structural = SchemaHashResult.Parse(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.Structural));

        v2.Version.ShouldBe(structural.Version, "The hash-format version does not depend on the comparison options");
        v2.Hash.ShouldNotBe(structural.Hash, "Structural normalizes away the DESC key and constraint name, changing the hash");

        SchemaHashResult.Compare(v2, structural).ShouldBe(SchemaHashComparison.Different, "Different options are not detected — a hash mismatch under the same version reads as Different");
    }

    [TestMethod]
    public async Task Envelope_SameSchemaSameOptions_IsFullyEqual()
    {
        var dbName = await CreateTestDatabaseAsync("EnvelopeEqual");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Assets (
                Id INT NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
                Created DATETIME2 NOT NULL
            )");

        var schema = await ExtractSchemaAsync(dbName);

        var a = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2);
        var b = zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, SchemaHashOptions.V2);

        a.ShouldBe(b, "The whole envelope is deterministic for the same schema and options");
        SchemaHashResult.Compare(a, b).ShouldBe(SchemaHashComparison.Equal);
    }

    #endregion
}
