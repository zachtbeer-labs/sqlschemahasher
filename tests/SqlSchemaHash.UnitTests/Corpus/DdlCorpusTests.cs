using System.Text.RegularExpressions;
using SqlSchemaHasher.Benchmarks.Corpus;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Corpus;

/// <summary>
/// The DDL renderer seeds the integration-tier database. These tests cover generation only —
/// that the script actually executes against SQL Server is proven by running the integration
/// benchmarks, which is a manual, Docker-dependent step.
/// </summary>
[TestClass]
public class DdlCorpusTests
{
    [TestMethod]
    public void Script_IsDeterministic()
    {
        var first = DdlCorpus.Script(SchemaProfile.Small);
        var second = DdlCorpus.Script(SchemaProfile.Small);

        first.ShouldBe(second, "Script generation must be deterministic so the seeded database is identical across runs");
    }

    [TestMethod]
    public void Script_CreatesEveryObjectKindInTheProfile()
    {
        var profile = SchemaProfile.Small;

        var batches = DdlCorpus.Script(profile);
        var script = string.Join("\n", batches);

        CountOccurrences(script, "CREATE TABLE ").ShouldBe(profile.Tables);
        CountOccurrences(script, "CREATE PROCEDURE ").ShouldBe(profile.StoredProcedures);
        CountOccurrences(script, "CREATE VIEW ").ShouldBe(profile.Views);
        CountOccurrences(script, "CREATE TRIGGER ").ShouldBe(profile.Triggers);
        CountOccurrences(script, "CREATE SEQUENCE ").ShouldBe(profile.Sequences);
        CountOccurrences(script, "CREATE SYNONYM ").ShouldBe(profile.Synonyms);
        CountOccurrences(script, "CREATE TYPE ").ShouldBe(profile.TableTypes);
    }

    [TestMethod]
    public void Script_PutsEachModuleCreationInItsOwnBatch()
    {
        // CREATE PROCEDURE/VIEW/FUNCTION/TRIGGER must each be the first statement in its batch.
        var batches = DdlCorpus.Script(SchemaProfile.Small);
        var moduleBatches = batches.Where(b => b.Contains("CREATE PROCEDURE ", StringComparison.Ordinal) || b.Contains("CREATE VIEW ", StringComparison.Ordinal) || b.Contains("CREATE TRIGGER ", StringComparison.Ordinal) || b.Contains("CREATE FUNCTION ", StringComparison.Ordinal)).ToList();

        moduleBatches.ShouldNotBeEmpty();
        foreach (var batch in moduleBatches)
        {
            batch.TrimStart().ShouldStartWith("CREATE ");
        }
    }

    [TestMethod]
    public void Script_ContainsNoBatchSeparatorKeyword()
    {
        // GO is an SSMS client directive; SqlCommand rejects it.
        var batches = DdlCorpus.Script(SchemaProfile.Small);

        foreach (var batch in batches)
        {
            batch.ShouldNotContain("\nGO", Case.Sensitive);
        }
    }

    [TestMethod]
    public void Script_ConstraintsAndIndexesReferenceColumnsThatActuallyExist()
    {
        // Regex parsing is safe here because DdlCorpus generates this SQL itself, in a known, fixed
        // shape — this is not general SQL parsing. Covers all three profiles because the defect this
        // guards against (mismatched foreign-key column types) only ever showed up once ForeignKeysPerTable
        // grew past 1, which Small alone would not have exercised.
        foreach (var profile in SchemaProfile.All)
        {
            AssertReferentialIntegrity(profile);
        }
    }

    private static void AssertReferentialIntegrity(SchemaProfile profile)
    {
        var batches = DdlCorpus.Script(profile);
        var tables = ParseTables(profile, batches);

        AssertForeignKeysReferenceExistingIntColumns(profile, batches, tables);
        AssertDefaultConstraintColumnsExist(profile, batches, tables);
        AssertIndexColumnsExist(profile, batches, tables);
    }

    private static readonly Regex TableHeaderPattern = new(@"^CREATE TABLE \[(?<schema>\w+)\]\.\[(?<table>\w+)\] \(");
    private static readonly Regex ColumnLinePattern = new(@"^\s*\[(?<column>\w+)\]\s+(?<type>\w+(?:\([^)]*\))?)\s+(?<identity>IDENTITY\(1,1\)\s+)?(?:NOT NULL|NULL),\s*$", RegexOptions.Multiline);
    private static readonly Regex CheckConstraintPattern = new(@"CONSTRAINT \[CK_\w+\] CHECK \(\[(?<column>\w+)\]");
    private static readonly Regex ForeignKeyPattern = new(@"^ALTER TABLE \[(?<childSchema>\w+)\]\.\[(?<childTable>\w+)\] ADD CONSTRAINT \[FK_\w+\] FOREIGN KEY \(\[(?<childColumn>\w+)\]\) REFERENCES \[(?<parentSchema>\w+)\]\.\[(?<parentTable>\w+)\] \(\[(?<parentColumn>\w+)\]\);$");
    private static readonly Regex DefaultConstraintPattern = new(@"^ALTER TABLE \[(?<schema>\w+)\]\.\[(?<table>\w+)\] ADD CONSTRAINT \[DF_\w+\] DEFAULT \(\(0\)\) FOR \[(?<column>\w+)\];$");
    private static readonly Regex IndexPattern = new(@"^CREATE NONCLUSTERED INDEX \[IX_\w+\] ON \[(?<schema>\w+)\]\.\[(?<table>\w+)\] \(\[(?<keyColumn>\w+)\]\) INCLUDE \(\[(?<includeColumn>\w+)\]\);$");

    private sealed record ParsedTable(string Schema, string Name, IReadOnlyDictionary<string, string> ColumnTypes, string IdentityColumn);

    private static Dictionary<(string Schema, string Table), ParsedTable> ParseTables(SchemaProfile profile, IReadOnlyList<string> batches)
    {
        var tables = new Dictionary<(string Schema, string Table), ParsedTable>();
        var createTableBatches = batches.Where(b => b.StartsWith("CREATE TABLE ", StringComparison.Ordinal));
        foreach (var batch in createTableBatches)
        {
            var header = TableHeaderPattern.Match(batch);
            header.Success.ShouldBeTrue($"[{profile.Name}] Could not parse a CREATE TABLE header from batch:\n{batch}");
            var schema = header.Groups["schema"].Value;
            var table = header.Groups["table"].Value;

            var columnTypes = new Dictionary<string, string>();
            string? identityColumn = null;
            foreach (Match columnMatch in ColumnLinePattern.Matches(batch))
            {
                var columnName = columnMatch.Groups["column"].Value;
                columnTypes[columnName] = columnMatch.Groups["type"].Value;
                if (columnMatch.Groups["identity"].Success)
                {
                    identityColumn = columnName;
                }
            }

            identityColumn.ShouldNotBeNull($"[{profile.Name}] Table [{schema}].[{table}] declares no IDENTITY column");
            tables[(schema, table)] = new ParsedTable(schema, table, columnTypes, identityColumn!);

            var checkMatch = CheckConstraintPattern.Match(batch);
            if (checkMatch.Success)
            {
                var checkColumn = checkMatch.Groups["column"].Value;
                columnTypes.ContainsKey(checkColumn).ShouldBeTrue($"[{profile.Name}] Table [{schema}].[{table}]'s CHECK constraint references undeclared column [{checkColumn}]");
            }
        }

        return tables;
    }

    private static void AssertForeignKeysReferenceExistingIntColumns(SchemaProfile profile, IReadOnlyList<string> batches, IReadOnlyDictionary<(string Schema, string Table), ParsedTable> tables)
    {
        var foreignKeyBatches = batches.Where(b => b.Contains("ADD CONSTRAINT [FK_", StringComparison.Ordinal));
        foreach (var batch in foreignKeyBatches)
        {
            var match = ForeignKeyPattern.Match(batch);
            match.Success.ShouldBeTrue($"[{profile.Name}] Could not parse foreign key batch:\n{batch}");

            var childKey = (match.Groups["childSchema"].Value, match.Groups["childTable"].Value);
            var parentKey = (match.Groups["parentSchema"].Value, match.Groups["parentTable"].Value);
            var childColumn = match.Groups["childColumn"].Value;
            var parentColumn = match.Groups["parentColumn"].Value;

            tables.ContainsKey(childKey).ShouldBeTrue($"[{profile.Name}] Foreign key references child table [{childKey.Item1}].[{childKey.Item2}], which was never declared");
            tables.ContainsKey(parentKey).ShouldBeTrue($"[{profile.Name}] Foreign key references parent table [{parentKey.Item1}].[{parentKey.Item2}], which was never declared");

            var child = tables[childKey];
            var parent = tables[parentKey];

            child.ColumnTypes.ContainsKey(childColumn).ShouldBeTrue($"[{profile.Name}] Foreign key on [{child.Schema}].[{child.Name}] references its own undeclared column [{childColumn}]");
            child.ColumnTypes[childColumn].ShouldBe("int", $"[{profile.Name}] Foreign key column [{child.Schema}].[{child.Name}].[{childColumn}] has type '{child.ColumnTypes[childColumn]}' but must be 'int' to match the referenced identity primary key [{parent.Schema}].[{parent.Name}].[{parent.IdentityColumn}]");

            parentColumn.ShouldBe(parent.IdentityColumn, $"[{profile.Name}] Foreign key on [{child.Schema}].[{child.Name}] references [{parent.Schema}].[{parent.Name}].[{parentColumn}], which is not that table's identity primary key column [{parent.IdentityColumn}]");
        }
    }

    private static void AssertDefaultConstraintColumnsExist(SchemaProfile profile, IReadOnlyList<string> batches, IReadOnlyDictionary<(string Schema, string Table), ParsedTable> tables)
    {
        var defaultBatches = batches.Where(b => b.Contains("ADD CONSTRAINT [DF_", StringComparison.Ordinal));
        foreach (var batch in defaultBatches)
        {
            var match = DefaultConstraintPattern.Match(batch);
            match.Success.ShouldBeTrue($"[{profile.Name}] Could not parse DEFAULT constraint batch:\n{batch}");

            var tableKey = (match.Groups["schema"].Value, match.Groups["table"].Value);
            var column = match.Groups["column"].Value;

            tables.ContainsKey(tableKey).ShouldBeTrue($"[{profile.Name}] DEFAULT constraint references table [{tableKey.Item1}].[{tableKey.Item2}], which was never declared");
            tables[tableKey].ColumnTypes.ContainsKey(column).ShouldBeTrue($"[{profile.Name}] DEFAULT constraint on [{tableKey.Item1}].[{tableKey.Item2}] references undeclared column [{column}]");
        }
    }

    private static void AssertIndexColumnsExist(SchemaProfile profile, IReadOnlyList<string> batches, IReadOnlyDictionary<(string Schema, string Table), ParsedTable> tables)
    {
        var indexBatches = batches.Where(b => b.StartsWith("CREATE NONCLUSTERED INDEX ", StringComparison.Ordinal));
        foreach (var batch in indexBatches)
        {
            var match = IndexPattern.Match(batch);
            match.Success.ShouldBeTrue($"[{profile.Name}] Could not parse index batch:\n{batch}");

            var tableKey = (match.Groups["schema"].Value, match.Groups["table"].Value);
            var keyColumn = match.Groups["keyColumn"].Value;
            var includeColumn = match.Groups["includeColumn"].Value;

            tables.ContainsKey(tableKey).ShouldBeTrue($"[{profile.Name}] Index references table [{tableKey.Item1}].[{tableKey.Item2}], which was never declared");
            var table = tables[tableKey];
            table.ColumnTypes.ContainsKey(keyColumn).ShouldBeTrue($"[{profile.Name}] Index on [{tableKey.Item1}].[{tableKey.Item2}] references undeclared key column [{keyColumn}]");
            table.ColumnTypes.ContainsKey(includeColumn).ShouldBeTrue($"[{profile.Name}] Index on [{tableKey.Item1}].[{tableKey.Item2}] references undeclared INCLUDE column [{includeColumn}]");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
