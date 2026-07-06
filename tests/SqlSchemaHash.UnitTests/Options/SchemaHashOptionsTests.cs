using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Options;

/// <summary>
/// In-memory sanity checks for <see cref="SchemaHashOptions"/> that require no database: the
/// <see cref="SchemaHashOptions.Default"/>/<see cref="SchemaHashOptions.V2"/> alias, and the invariant
/// that an all-<c>Strict</c> options object reproduces a bare <c>new SchemaHashOptions()</c> hash
/// byte-for-byte (documented in CLAUDE.md's <c>SchemaHashOptions</c> section).
/// </summary>
[TestClass]
public class SchemaHashOptionsTests
{
    [TestMethod]
    public void Default_IsV2()
    {
        var defaultOptions = SchemaHashOptions.Default;
        var v2 = SchemaHashOptions.V2;

        defaultOptions.Tables.ShouldBe(v2.Tables);
        defaultOptions.Columns.ShouldBe(v2.Columns);
        defaultOptions.Indexes.ShouldBe(v2.Indexes);
        defaultOptions.Constraints.ShouldBe(v2.Constraints);
        defaultOptions.Modules.ShouldBe(v2.Modules);
        defaultOptions.IgnoreSysDiagramObjects.ShouldBe(v2.IgnoreSysDiagramObjects);
    }

    [TestMethod]
    public void ComputeHash_ExplicitAllStrictOptions_MatchesBareOptions()
    {
        var schema = BuildSampleSchema();

        var bareOptions = new SchemaHashOptions();
        var explicitStrictOptions = new SchemaHashOptions
        {
            Tables = TableNormalization.Strict,
            Columns = ColumnNormalization.Strict,
            Indexes = IndexNormalization.Strict,
            Constraints = ConstraintNormalization.Strict,
            Modules = ModuleNormalization.Strict,
        };

        zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, bareOptions).ShouldBe(zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(schema, explicitStrictOptions), "An explicit all-Strict options object must reproduce the bare-default hash byte-for-byte");
    }

    private static SchemaMetadata BuildSampleSchema()
    {
        var columns = new List<ColumnSchema> { new("Id", "int", 4, 10, 0, false), new("Name", "nvarchar", 100, 0, 0, true) };
        var tables = new List<TableSchema>
        {
            new("dbo", "T", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null)
        };
        return new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());
    }
}
