using System.Globalization;
using System.Text;

namespace SqlSchemaHasher.Benchmarks.Corpus;

/// <summary>
/// Renders a <see cref="SchemaProfile"/> into T-SQL that seeds an equivalent real database for the
/// integration benchmark tier. Object <em>counts</em> mirror <see cref="MetadataCorpus"/> exactly —
/// per-object-kind count parity between the two renderers is pinned by test — but the schemas this
/// renderer produces are deliberately simpler in ways the counts don't capture. It does not model:
/// computed columns, indexed views, multi-statement table-valued functions (only scalar and inline
/// TVFs), varied per-parameter types/directions on modules (every module takes a single <c>@p0 int</c>
/// rather than <see cref="SchemaProfile.ParametersPerModule"/> varied parameters), <c>INSTEAD OF</c>
/// or disabled triggers (only plain <c>AFTER</c>), column-scoped extended properties (only
/// object-scoped), and cycling sequences. Beyond these omissions, a seeded database still would not
/// extract to byte-identical metadata against <see cref="MetadataCorpus"/>'s hand-built values, since
/// SQL Server contributes its own defaults and system-named constraints.
///
/// Returns ordered batches rather than one string: <c>GO</c> is an SSMS client directive that
/// <c>SqlCommand</c> rejects, and each module creation must be the first statement in its batch.
/// </summary>
public static class DdlCorpus
{
    public static IReadOnlyList<string> Script(SchemaProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var batches = new List<string>();

        AppendSchemas(batches);
        AppendTables(batches, profile);
        AppendForeignKeys(batches, profile);
        AppendIndexes(batches, profile);
        AppendTableTypes(batches, profile);
        AppendSequences(batches, profile);
        AppendStoredProcedures(batches, profile);
        AppendViews(batches, profile);
        AppendFunctions(batches, profile);
        AppendTriggers(batches, profile);
        AppendSynonyms(batches, profile);
        AppendExtendedProperties(batches, profile);

        return batches;
    }

    private static string SchemaFor(int index) => CorpusSchemas.For(index);

    // The corpus's one job is byte-identical output across machines and runs, so its number formatting
    // is pinned explicitly here rather than inherited from whatever culture happens to be active in the
    // process running the benchmark.
    private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AppendSchemas(List<string> batches)
    {
        // dbo always exists; the rest are created individually because CREATE SCHEMA must be alone in its batch.
        var nonDefaultSchemas = CorpusSchemas.Names.Where(name => name != "dbo");
        foreach (var schemaName in nonDefaultSchemas)
        {
            batches.Add($"CREATE SCHEMA [{schemaName}];");
        }
    }

    private static void AppendTables(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.Tables; i++)
        {
            var name = $"Table{Inv(i)}";
            var builder = new StringBuilder();
            builder.AppendLine($"CREATE TABLE [{SchemaFor(i)}].[{name}] (");
            builder.AppendLine($"    [{name}Id] int IDENTITY(1,1) NOT NULL,");

            for (var c = 1; c < profile.ColumnsPerTable; c++)
            {
                var nullability = c % 3 == 0 ? "NOT NULL" : "NULL";
                builder.AppendLine($"    [Col{Inv(c)}] {ColumnTypeFor(c, profile)} {nullability},");
            }

            builder.AppendLine($"    CONSTRAINT [PK_{name}] PRIMARY KEY CLUSTERED ([{name}Id]),");
            builder.AppendLine($"    CONSTRAINT [CK_{name}] CHECK ([Col1] > 0)");
            builder.AppendLine(");");
            batches.Add(builder.ToString());

            batches.Add($"ALTER TABLE [{SchemaFor(i)}].[{name}] ADD CONSTRAINT [DF_{name}_Col1] DEFAULT ((0)) FOR [Col1];");
        }
    }

    /// <summary>
    /// Col1 is int so the CHECK and DEFAULT above are valid. Columns 2 through <c>1 + ForeignKeysPerTable</c>
    /// are also int: <see cref="AppendForeignKeys"/> targets exactly those columns, and a foreign key's
    /// referencing column must match the type of the parent's int identity primary key it references. The
    /// rest cycle through the other types so the schema stays type-diverse.
    /// </summary>
    private static string ColumnTypeFor(int columnIndex, SchemaProfile profile)
    {
        if (columnIndex >= 1 && columnIndex <= 1 + profile.ForeignKeysPerTable)
        {
            return "int";
        }

        var types = new[] { "bigint", "nvarchar(128)", "decimal(18,4)", "bit", "datetime2", "uniqueidentifier", "varbinary(256)" };
        return types[columnIndex % types.Length];
    }

    private static void AppendForeignKeys(List<string> batches, SchemaProfile profile)
    {
        // Table 0 has no parent; every other table references earlier tables to keep the graph acyclic.
        for (var i = 1; i < profile.Tables; i++)
        {
            for (var k = 0; k < profile.ForeignKeysPerTable; k++)
            {
                var referencedIndex = Math.Max(0, i - 1 - k);
                batches.Add($"ALTER TABLE [{SchemaFor(i)}].[Table{Inv(i)}] ADD CONSTRAINT [FK_Table{Inv(i)}_{Inv(referencedIndex)}_{Inv(k)}] FOREIGN KEY ([Col{Inv(k + 2)}]) REFERENCES [{SchemaFor(referencedIndex)}].[Table{Inv(referencedIndex)}] ([Table{Inv(referencedIndex)}Id]);");
            }
        }
    }

    private static void AppendIndexes(List<string> batches, SchemaProfile profile)
    {
        // Index 0 is the clustered primary key created inline, so nonclustered indexes start at 1.
        for (var i = 0; i < profile.Tables; i++)
        {
            for (var k = 1; k < profile.IndexesPerTable; k++)
            {
                batches.Add($"CREATE NONCLUSTERED INDEX [IX_Table{Inv(i)}_{Inv(k)}] ON [{SchemaFor(i)}].[Table{Inv(i)}] ([Col{Inv(k)}]) INCLUDE ([Col{Inv(k % (profile.ColumnsPerTable - 1) + 1)}]);");
            }
        }
    }

    private static void AppendTableTypes(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.TableTypes; i++)
        {
            var name = $"Type{Inv(i)}";
            var builder = new StringBuilder();
            builder.AppendLine($"CREATE TYPE [{SchemaFor(i)}].[{name}] AS TABLE (");
            builder.AppendLine($"    [{name}Id] int NOT NULL,");
            for (var c = 1; c < profile.ColumnsPerTable; c++)
            {
                builder.AppendLine($"    [Col{Inv(c)}] {ColumnTypeFor(c, profile)} NULL,");
            }

            builder.AppendLine($"    PRIMARY KEY CLUSTERED ([{name}Id])");
            builder.AppendLine(");");
            batches.Add(builder.ToString());
        }
    }

    private static void AppendSequences(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.Sequences; i++)
        {
            batches.Add($"CREATE SEQUENCE [{SchemaFor(i)}].[seq_Sequence{Inv(i)}] AS bigint START WITH 1 INCREMENT BY 1 CACHE 50;");
        }
    }

    private static void AppendStoredProcedures(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.StoredProcedures; i++)
        {
            var parameters = string.Join(", ", Enumerable.Range(0, profile.ParametersPerModule).Select(p => $"@p{Inv(p)} int = NULL"));
            var targetTable = i % Math.Max(profile.Tables, 1);
            batches.Add($"CREATE PROCEDURE [{SchemaFor(i)}].[usp_Proc{Inv(i)}] {parameters} AS BEGIN SET NOCOUNT ON; SELECT COUNT(*) FROM [{SchemaFor(targetTable)}].[Table{Inv(targetTable)}] WHERE [Col1] = @p0; END");
        }
    }

    private static void AppendViews(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.Views; i++)
        {
            var targetTable = i % Math.Max(profile.Tables, 1);
            batches.Add($"CREATE VIEW [{SchemaFor(i)}].[vw_View{Inv(i)}] AS SELECT [Table{Inv(targetTable)}Id], [Col1] FROM [{SchemaFor(targetTable)}].[Table{Inv(targetTable)}];");
        }
    }

    private static void AppendFunctions(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.Functions; i++)
        {
            var name = $"fn_Function{Inv(i)}";
            var schemaName = SchemaFor(i);
            // Alternate scalar and inline table-valued functions so both calculator paths are exercised.
            if (i % 2 == 0)
            {
                batches.Add($"CREATE FUNCTION [{schemaName}].[{name}] (@p0 int) RETURNS int AS BEGIN RETURN @p0 + {Inv(i)}; END");
            }
            else
            {
                var targetTable = i % Math.Max(profile.Tables, 1);
                batches.Add($"CREATE FUNCTION [{schemaName}].[{name}] (@p0 int) RETURNS TABLE AS RETURN (SELECT [Col1] FROM [{SchemaFor(targetTable)}].[Table{Inv(targetTable)}] WHERE [Col1] = @p0);");
            }
        }
    }

    private static void AppendTriggers(List<string> batches, SchemaProfile profile)
    {
        var eventTypes = new[] { "INSERT", "UPDATE", "DELETE" };
        for (var i = 0; i < profile.Triggers; i++)
        {
            var targetTable = i % Math.Max(profile.Tables, 1);
            batches.Add($"CREATE TRIGGER [{SchemaFor(targetTable)}].[tr_Trigger{Inv(i)}] ON [{SchemaFor(targetTable)}].[Table{Inv(targetTable)}] AFTER {eventTypes[i % eventTypes.Length]} AS BEGIN SET NOCOUNT ON; END");
        }
    }

    private static void AppendSynonyms(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.Synonyms; i++)
        {
            var targetTable = i % Math.Max(profile.Tables, 1);
            batches.Add($"CREATE SYNONYM [{SchemaFor(i)}].[syn_Synonym{Inv(i)}] FOR [{SchemaFor(targetTable)}].[Table{Inv(targetTable)}];");
        }
    }

    private static void AppendExtendedProperties(List<string> batches, SchemaProfile profile)
    {
        for (var i = 0; i < profile.ExtendedProperties; i++)
        {
            var targetTable = i % Math.Max(profile.Tables, 1);
            // A given property name can only be added once per object, and a profile may ask for more
            // properties than it has tables. Wrap onto distinct names past the first pass rather than
            // colliding on MS_Description — sp_addextendedproperty errors on a duplicate.
            var propertyName = i < profile.Tables ? "MS_Description" : $"BenchProp{Inv(i)}";
            batches.Add($"EXEC sp_addextendedproperty @name = N'{propertyName}', @value = N'Synthetic description {Inv(i)}', @level0type = N'SCHEMA', @level0name = N'{SchemaFor(targetTable)}', @level1type = N'TABLE', @level1name = N'Table{Inv(targetTable)}';");
        }
    }
}
