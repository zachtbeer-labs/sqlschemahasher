using System.Security.Cryptography;
using System.Text;
using zachtbeer.SqlSchemaHasher;

namespace SqlSchemaHasher.Benchmarks.Corpus;

/// <summary>
/// Renders a <see cref="SchemaProfile"/> into an in-memory <see cref="SchemaMetadata"/> graph for the
/// calculator benchmark tier. Generation is a pure function of loop indices — no randomness, no clock,
/// no environment — so the same profile always yields a byte-identical hash.
/// </summary>
public static class MetadataCorpus
{
    private static readonly string[] SchemaNames = ["dbo", "sales", "reporting", "staging"];
    private static readonly string[] DataTypes = ["int", "bigint", "nvarchar", "decimal", "bit", "datetime2", "uniqueidentifier", "varbinary"];

    /// <summary>An empty schema. Used as the base for single-object-kind projections.</summary>
    public static SchemaMetadata Empty => new(new List<TableSchema>(), new List<StoredProcedureSchema>(), new List<UserDefinedTableTypeSchema>(), new List<ViewSchema>(), new List<FunctionSchema>(), new List<TriggerSchema>(), new List<SequenceSchema>(), new List<SynonymSchema>(), new List<ExtendedPropertySchema>());

    /// <summary>Builds the full synthetic schema described by <paramref name="profile"/>.</summary>
    public static SchemaMetadata Build(SchemaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var tables = BuildTables(profile);
        var procedures = BuildStoredProcedures(profile);
        var tableTypes = BuildTableTypes(profile);
        var views = BuildViews(profile);
        var functions = BuildFunctions(profile);
        var triggers = BuildTriggers(profile);
        var sequences = BuildSequences(profile);
        var synonyms = BuildSynonyms(profile);
        var extendedProperties = BuildExtendedProperties(profile);

        return new SchemaMetadata(tables, procedures, tableTypes, views, functions, triggers, sequences, synonyms, extendedProperties);
    }

    private static string SchemaFor(int index) => SchemaNames[index % SchemaNames.Length];

    /// <summary>
    /// A deterministic stand-in for the server-side <c>HASHBYTES('SHA2_256', ...)</c> module hash:
    /// 64 lowercase hex characters, stable for a given seed.
    /// </summary>
    private static string DefinitionHash(string seed) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private static List<ColumnSchema> BuildColumns(SchemaProfile profile, string owner)
    {
        var columns = new List<ColumnSchema>(profile.ColumnsPerTable);
        for (var i = 0; i < profile.ColumnsPerTable; i++)
        {
            var dataType = DataTypes[i % DataTypes.Length];
            var isComputed = i > 0 && i % 7 == 0;
            var computedDefinition = isComputed ? $"([Col{i - 1}]+(1))" : null;
            columns.Add(new ColumnSchema($"Col{i}", dataType, MaxLength: 8 * (i % 16 + 1), Precision: 18, Scale: i % 4, IsNullable: i % 3 != 0, IsComputed: isComputed, ComputedDefinition: computedDefinition, IsPersisted: isComputed, CollationName: dataType == "nvarchar" ? "SQL_Latin1_General_CP1_CI_AS" : null, ColumnId: i + 1));
        }

        // Guarantees the owner participates in the hashed value, so two same-shaped tables differ.
        columns[0] = columns[0] with { Name = $"{owner}Id" };
        return columns;
    }

    private static List<IndexSchema> BuildIndexes(SchemaProfile profile, string tableName)
    {
        var indexes = new List<IndexSchema>(profile.IndexesPerTable);
        for (var i = 0; i < profile.IndexesPerTable; i++)
        {
            var keyColumns = new List<IndexKeyColumn> { new($"Col{i % profile.ColumnsPerTable}", IsDescendingKey: i % 2 == 1) };
            var includedColumns = new List<string> { $"Col{(i + 1) % profile.ColumnsPerTable}" };
            var typeDesc = i == 0 ? "CLUSTERED" : "NONCLUSTERED";
            indexes.Add(new IndexSchema($"IX_{tableName}_{i}", typeDesc, IsUnique: i == 0, IsUniqueConstraint: false, IsPrimaryKey: i == 0, IsDisabled: false, IgnoreDupKey: false, keyColumns, includedColumns, FilterDefinition: i % 5 == 4 ? $"([Col{i % profile.ColumnsPerTable}] IS NOT NULL)" : null, FillFactor: (byte)(i % 2 == 0 ? 0 : 90)));
        }

        return indexes;
    }

    private static List<TableSchema> BuildTables(SchemaProfile profile)
    {
        var tables = new List<TableSchema>(profile.Tables);
        for (var i = 0; i < profile.Tables; i++)
        {
            var name = $"Table{i}";
            var schemaName = SchemaFor(i);
            var columns = BuildColumns(profile, name);
            var indexes = BuildIndexes(profile, name);
            var keyConstraints = new List<KeyConstraintSchema> { new("PRIMARY_KEY_CONSTRAINT", $"PK_{name}", IsSystemNamed: false, new List<IndexKeyColumn> { new($"{name}Id", IsDescendingKey: false) }) };
            var foreignKeys = BuildForeignKeys(profile, i, name);
            var checkConstraints = new List<CheckConstraintSchema> { new($"CK_{name}", "([Col1]>(0))", IsDisabled: false, IsNotTrusted: false) };
            var defaultConstraints = new List<DefaultConstraintSchema> { new($"DF_{name}_Col1", "Col1", "((0))") };
            tables.Add(new TableSchema(schemaName, name, columns, indexes, keyConstraints, foreignKeys, checkConstraints, defaultConstraints, IdentityColumn: $"{name}Id", IdentitySeed: "1", IdentityIncrement: "1"));
        }

        return tables;
    }

    private static List<ForeignKeyConstraintSchema> BuildForeignKeys(SchemaProfile profile, int tableIndex, string tableName)
    {
        // Table 0 has no parent to reference, so it carries no foreign keys.
        var foreignKeys = new List<ForeignKeyConstraintSchema>();
        for (var i = 0; i < profile.ForeignKeysPerTable && tableIndex > 0; i++)
        {
            var referencedIndex = (tableIndex - 1 - i + profile.Tables) % profile.Tables;
            var columnPairs = new List<ForeignKeyColumnPair> { new($"Col{i + 1}", $"Table{referencedIndex}Id") };
            foreignKeys.Add(new ForeignKeyConstraintSchema($"FK_{tableName}_{referencedIndex}", SchemaFor(referencedIndex), $"Table{referencedIndex}", columnPairs, DeleteAction: "NO_ACTION", UpdateAction: "NO_ACTION", IsDisabled: false, IsNotTrusted: false));
        }

        return foreignKeys;
    }

    private static List<ParameterSchema> BuildParameters(SchemaProfile profile)
    {
        var parameters = new List<ParameterSchema>(profile.ParametersPerModule);
        for (var i = 0; i < profile.ParametersPerModule; i++)
        {
            parameters.Add(new ParameterSchema($"@p{i}", DataTypes[i % DataTypes.Length], MaxLength: 8 * (i % 8 + 1), Precision: 18, Scale: i % 4, IsNullable: true, IsOutput: i % 4 == 3));
        }

        return parameters;
    }

    private static List<StoredProcedureSchema> BuildStoredProcedures(SchemaProfile profile)
    {
        var procedures = new List<StoredProcedureSchema>(profile.StoredProcedures);
        for (var i = 0; i < profile.StoredProcedures; i++)
        {
            var name = $"usp_Proc{i}";
            procedures.Add(new StoredProcedureSchema(SchemaFor(i), name, BuildParameters(profile), DefinitionHash($"proc:{name}")));
        }

        return procedures;
    }

    private static List<UserDefinedTableTypeSchema> BuildTableTypes(SchemaProfile profile)
    {
        var tableTypes = new List<UserDefinedTableTypeSchema>(profile.TableTypes);
        for (var i = 0; i < profile.TableTypes; i++)
        {
            var name = $"Type{i}";
            var keyConstraints = new List<KeyConstraintSchema> { new("PRIMARY_KEY_CONSTRAINT", $"PK_{name}", IsSystemNamed: true, new List<IndexKeyColumn> { new($"{name}Id", IsDescendingKey: false) }) };
            tableTypes.Add(new UserDefinedTableTypeSchema(SchemaFor(i), name, BuildColumns(profile, name), new List<IndexSchema>(), keyConstraints, new List<CheckConstraintSchema>(), new List<DefaultConstraintSchema>()));
        }

        return tableTypes;
    }

    private static List<ViewSchema> BuildViews(SchemaProfile profile)
    {
        var views = new List<ViewSchema>(profile.Views);
        for (var i = 0; i < profile.Views; i++)
        {
            var name = $"vw_View{i}";
            // Every fifth view is indexed, mirroring the indexed-view path in the calculator.
            var indexes = i % 5 == 0 ? BuildIndexes(profile, name) : new List<IndexSchema>();
            views.Add(new ViewSchema(SchemaFor(i), name, indexes, DefinitionHash($"view:{name}")));
        }

        return views;
    }

    private static List<FunctionSchema> BuildFunctions(SchemaProfile profile)
    {
        var typeDescs = new[] { "SQL_SCALAR_FUNCTION", "SQL_INLINE_TABLE_VALUED_FUNCTION", "SQL_TABLE_VALUED_FUNCTION" };
        var functions = new List<FunctionSchema>(profile.Functions);
        for (var i = 0; i < profile.Functions; i++)
        {
            var name = $"fn_Function{i}";
            var typeDesc = typeDescs[i % typeDescs.Length];
            var parameters = BuildParameters(profile);
            // A scalar function's return type is the parameter_id = 0 row.
            if (typeDesc == "SQL_SCALAR_FUNCTION")
            {
                parameters.Insert(0, new ParameterSchema(string.Empty, "int", MaxLength: 4, Precision: 10, Scale: 0, IsNullable: true));
            }

            functions.Add(new FunctionSchema(SchemaFor(i), name, typeDesc, parameters, DefinitionHash($"function:{name}")));
        }

        return functions;
    }

    private static List<TriggerSchema> BuildTriggers(SchemaProfile profile)
    {
        var eventTypes = new[] { "INSERT", "UPDATE", "DELETE" };
        var triggers = new List<TriggerSchema>(profile.Triggers);
        for (var i = 0; i < profile.Triggers; i++)
        {
            var name = $"tr_Trigger{i}";
            var parentIndex = i % Math.Max(profile.Tables, 1);
            var events = new List<TriggerEventSchema> { new(eventTypes[i % eventTypes.Length], IsFirst: i % 3 == 0, IsLast: false) };
            triggers.Add(new TriggerSchema(SchemaFor(i), name, SchemaFor(parentIndex), $"Table{parentIndex}", IsDisabled: i % 11 == 0, IsInsteadOfTrigger: i % 7 == 0, IsNotForReplication: false, events, DefinitionHash($"trigger:{name}")));
        }

        return triggers;
    }

    private static List<SequenceSchema> BuildSequences(SchemaProfile profile)
    {
        var sequences = new List<SequenceSchema>(profile.Sequences);
        for (var i = 0; i < profile.Sequences; i++)
        {
            sequences.Add(new SequenceSchema(SchemaFor(i), $"seq_Sequence{i}", "bigint", Precision: 19, StartValue: "1", Increment: "1", MinimumValue: "-9223372036854775808", MaximumValue: "9223372036854775807", IsCycling: i % 2 == 0, IsCached: true, CacheSize: 50));
        }

        return sequences;
    }

    private static List<SynonymSchema> BuildSynonyms(SchemaProfile profile)
    {
        var synonyms = new List<SynonymSchema>(profile.Synonyms);
        for (var i = 0; i < profile.Synonyms; i++)
        {
            var targetIndex = i % Math.Max(profile.Tables, 1);
            synonyms.Add(new SynonymSchema(SchemaFor(i), $"syn_Synonym{i}", $"[{SchemaFor(targetIndex)}].[Table{targetIndex}]"));
        }

        return synonyms;
    }

    private static List<ExtendedPropertySchema> BuildExtendedProperties(SchemaProfile profile)
    {
        var properties = new List<ExtendedPropertySchema>(profile.ExtendedProperties);
        for (var i = 0; i < profile.ExtendedProperties; i++)
        {
            var targetIndex = i % Math.Max(profile.Tables, 1);
            // Alternate object-scoped and column-scoped properties to exercise both resolution paths.
            var subObjectName = i % 2 == 0 ? null : $"Col{i % profile.ColumnsPerTable}";
            properties.Add(new ExtendedPropertySchema("OBJECT_OR_COLUMN", SchemaFor(targetIndex), $"Table{targetIndex}", subObjectName, "MS_Description", "nvarchar", $"Synthetic description {i}"));
        }

        return properties;
    }
}
