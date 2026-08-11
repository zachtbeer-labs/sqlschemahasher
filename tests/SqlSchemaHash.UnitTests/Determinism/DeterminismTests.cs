using System.Globalization;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Determinism;

/// <summary>
/// Hash determinism tests that require no database: culture independence, the integer-serialization
/// golden wire-format vector, and the HASHBYTES-support version/engine-edition decision. (Container-
/// dependent determinism tests — identical/different real schemas, object-creation-order independence —
/// live in the integration suite's Determinism/RegressionAnchorTests coverage.)
/// </summary>
[TestClass]
public class DeterminismTests
{
    [TestMethod]
    public void SchemaHash_IsIndependentOfCurrentCulture()
    {
        // Sorting must be ordinal: under en-US "Åland" sorts before "Borders" (Å ~ A),
        // under da-DK it sorts after (Å is the last letter of the Danish alphabet).
        // The hash of the same metadata must not depend on the machine's culture.
        var enUs = new CultureInfo("en-US");
        var daDk = new CultureInfo("da-DK");

        if (Math.Sign(string.Compare("Åland", "Borders", enUs, CompareOptions.None)) == Math.Sign(string.Compare("Åland", "Borders", daDk, CompareOptions.None)))
            Assert.Inconclusive("This environment's ICU data does not order the probe strings differently between en-US and da-DK, so the test cannot detect culture-sensitive sorting.");

        var columns = new List<ColumnSchema> { new("Id", "int", 4, 10, 0, false) };
        var tables = new List<TableSchema>
        {
            new("dbo", "Åland", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null),
            new("dbo", "Borders", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null)
        };
        var schema = new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());

        var hashEnUs = ComputeHashWithCulture(schema, enUs);
        var hashDaDk = ComputeHashWithCulture(schema, daDk);

        hashEnUs.ShouldBe(hashDaDk, "The hash of identical schema metadata must not depend on the current culture");
    }

    [TestMethod]
    public void SchemaHash_IntegerSerialization_IsLittleEndianAndStable()
    {
        // AppendInt and AppendString's length prefix write little-endian explicitly so the hash
        // does not depend on the host's byte order. This golden vector pins that layout: a change
        // to the constant means the hash wire format changed and previously-stored hashes are invalid.
        var columns = new List<ColumnSchema> { new("Id", "int", 4, 10, 0, false) };
        var tables = new List<TableSchema>
        {
            new("dbo", "T", columns, new List<IndexSchema>(), new List<KeyConstraintSchema>(), new List<ForeignKeyConstraintSchema>(), new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>(), null)
        };
        var schema = new SchemaMetadata(tables, new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());

        new SchemaHashCalculator().ComputeHash(schema)
            .ShouldBe("82d011e1ddb7ab5b7a89b5099181873768948d51f5eb595e87c6bbb76185f84c", "The integer byte layout of the hash must remain stable and independent of host endianness");
    }

    [TestMethod]
    public void SchemaHash_TriggerDisabledState_ChangesHash()
    {
        // DB-free sanity check for the new object kinds: two in-memory schemas differing only in
        // TriggerSchema.IsDisabled must hash differently.
        var enabled = new TriggerSchema("dbo", "trg_T", "dbo", "T", false, false, false, new List<TriggerEventSchema>(), "abc");
        var disabled = enabled with { IsDisabled = true };

        var schemaEnabled = new SchemaMetadata(new List<TableSchema>(), new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema> { enabled }, new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());
        var schemaDisabled = new SchemaMetadata(new List<TableSchema>(), new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema> { disabled }, new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());

        new SchemaHashCalculator().ComputeHash(schemaEnabled).ShouldNotBe(new SchemaHashCalculator().ComputeHash(schemaDisabled), "A trigger's IsDisabled state must change the hash");
    }

    [TestMethod]
    public void SchemaHash_FunctionTypeDesc_ChangesHash()
    {
        // DB-free sanity check: two in-memory schemas differing only in FunctionSchema.TypeDesc must
        // hash differently, since TypeDesc is what distinguishes scalar/inline-TVF/multi-statement-TVF.
        var scalar = new FunctionSchema("dbo", "F", "SQL_SCALAR_FUNCTION", new List<ParameterSchema>(), "abc");
        var inlineTvf = scalar with { TypeDesc = "SQL_INLINE_TABLE_VALUED_FUNCTION" };

        var schemaScalar = new SchemaMetadata(new List<TableSchema>(), new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema> { scalar }, new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());
        var schemaTvf = new SchemaMetadata(new List<TableSchema>(), new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema> { inlineTvf }, new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());

        new SchemaHashCalculator().ComputeHash(schemaScalar).ShouldNotBe(new SchemaHashCalculator().ComputeHash(schemaTvf), "A function's TypeDesc must change the hash even with identical parameters and definition hash");
    }

    [TestMethod]
    public void SchemaHash_ExtendedPropertyValue_ChangesHash()
    {
        // DB-free sanity check: two in-memory schemas differing only in an extended property's value
        // must hash differently, and a NULL value must stay distinct from an empty-string value
        // (ValueType is null only for a NULL value).
        var described = new ExtendedPropertySchema("OBJECT_OR_COLUMN", "dbo", "T", null, "MS_Description", "nvarchar", "Orders table");
        var redescribed = described with { Value = "Archived orders table" };
        var nullValued = described with { ValueType = null, Value = null };
        var emptyValued = described with { Value = string.Empty };

        static string HashOf(ExtendedPropertySchema property) => new SchemaHashCalculator().ComputeHash(new SchemaMetadata(new List<TableSchema>(), new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema> { property }));

        HashOf(described).ShouldNotBe(HashOf(redescribed), "An extended property's value must change the hash");
        HashOf(nullValued).ShouldNotBe(HashOf(emptyValued), "A NULL extended property value must hash differently from an empty-string value");
    }

    [TestMethod]
    public void HashBytesSupport_IsDeterminedByVersionAndEngineEdition()
    {
        // Azure SQL Database reports major version 12 but supports unlimited HASHBYTES;
        // the decision must consider the engine edition, not just the version number.
        SchemaExtractor.SupportsUnlimitedHashBytes(12, 5).ShouldBeTrue("Azure SQL Database (EngineEdition 5) supports unlimited HASHBYTES despite reporting version 12");
        SchemaExtractor.SupportsUnlimitedHashBytes(12, 8).ShouldBeTrue("Azure SQL Managed Instance (EngineEdition 8) supports unlimited HASHBYTES");
        SchemaExtractor.SupportsUnlimitedHashBytes(13, 3).ShouldBeTrue("On-prem SQL Server 2016+ supports unlimited HASHBYTES");
        SchemaExtractor.SupportsUnlimitedHashBytes(11, 3).ShouldBeFalse("On-prem SQL Server 2012 has the 8000-byte HASHBYTES limit");
    }

    private static string ComputeHashWithCulture(SchemaMetadata schema, CultureInfo culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return new SchemaHashCalculator().ComputeHash(schema);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
