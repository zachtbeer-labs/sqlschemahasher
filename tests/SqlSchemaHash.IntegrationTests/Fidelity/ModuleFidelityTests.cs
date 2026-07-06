using Dapper;
using Microsoft.Data.SqlClient;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Stored-procedure / module schema fidelity: a genuine difference in a captured module attribute —
/// body text, schema, encryption, CREATE-time SET state (QUOTED_IDENTIFIER), and parameter metadata
/// (direction, readonly, table-valued/typed-XML type qualification) — must change the hash, with the
/// metadata pinned. Module-normalization contracts (IgnoreBodyText / IgnoreSetOptions) live in Settings.
/// </summary>
[TestClass]
public class ModuleFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task StoredProcDefinition_DifferentBodies_ProduceDifferentHashes()
    {
        // Arrange - Two procedures with same name/params but different body
        var db1Name = await CreateTestDatabaseAsync("ProcHash_Body1");
        var db2Name = await CreateTestDatabaseAsync("ProcHash_Body2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE PROCEDURE GetValue @id INT
            AS BEGIN SELECT @id * 2 END");

        await ExecuteSqlAsync(db2Name, @"
            CREATE PROCEDURE GetValue @id INT
            AS BEGIN SELECT @id * 3 END");

        // Act
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert
        hash1.ShouldNotBe(hash2, "Procs with different bodies must produce different hashes");
    }

    [TestMethod]
    public async Task StoredProcDefinition_IdenticalBodies_ProduceSameHash()
    {
        // Arrange
        var db1Name = await CreateTestDatabaseAsync("ProcHash_Same1");
        var db2Name = await CreateTestDatabaseAsync("ProcHash_Same2");

        var procDef = @"
            CREATE PROCEDURE GetProjectById @projectId INT
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT ProjectId, Name FROM Projects WHERE ProjectId = @projectId
            END";

        await ExecuteSqlAsync(db1Name, "CREATE TABLE Projects (ProjectId INT, Name NVARCHAR(100))");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE Projects (ProjectId INT, Name NVARCHAR(100))");
        await ExecuteSqlAsync(db1Name, procDef);
        await ExecuteSqlAsync(db2Name, procDef);

        // Act
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert
        hash1.ShouldBe(hash2, "Identical procedure definitions must produce same hash");
    }

    [TestMethod]
    public async Task StoredProcDefinition_ModifiedBody_ChangesHash()
    {
        // Arrange
        var dbName = await CreateTestDatabaseAsync("ProcHash_Alter");

        await ExecuteSqlAsync(dbName, @"
            CREATE PROCEDURE CalculateTotal @qty INT, @price DECIMAL(10,2)
            AS BEGIN SELECT @qty * @price END");

        // Act - Get hash before ALTER
        var hashBefore = await ExtractAndHashAsync(dbName);

        // ALTER the procedure body
        await ExecuteSqlAsync(dbName, @"
            ALTER PROCEDURE CalculateTotal @qty INT, @price DECIMAL(10,2)
            AS BEGIN SELECT @qty * @price * 1.1 END");

        var hashAfter = await ExtractAndHashAsync(dbName);

        // Assert
        hashBefore.ShouldNotBe(hashAfter, "ALTER PROCEDURE should change the hash");
    }

    [TestMethod]
    public async Task StoredProcDefinition_WhitespaceChanges_AffectHash()
    {
        // Arrange - Two procedures with same logic but different whitespace/comments
        var db1Name = await CreateTestDatabaseAsync("ProcHash_WS1");
        var db2Name = await CreateTestDatabaseAsync("ProcHash_WS2");

        await ExecuteSqlAsync(db1Name, @"CREATE PROCEDURE GetData AS BEGIN SELECT 1 END");

        await ExecuteSqlAsync(db2Name, @"
            CREATE PROCEDURE GetData
            AS
            BEGIN
                -- This is a comment
                SELECT 1
            END");

        // Act
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert
        hash1.ShouldNotBe(hash2, "Whitespace and comment changes affect the definition hash");
    }

    [TestMethod]
    public async Task StoredProcDefinition_InDifferentSchemas_ProduceDifferentHashes()
    {
        // Arrange - Same proc name and body, but in different schemas
        var db1Name = await CreateTestDatabaseAsync("ProcHash_Schema1");
        var db2Name = await CreateTestDatabaseAsync("ProcHash_Schema2");

        await ExecuteSqlAsync(db1Name, "CREATE SCHEMA sales");
        await ExecuteSqlAsync(db2Name, "CREATE SCHEMA reporting");

        await ExecuteSqlAsync(db1Name, @"
            CREATE PROCEDURE sales.GetData AS BEGIN SELECT 1 END");
        await ExecuteSqlAsync(db2Name, @"
            CREATE PROCEDURE reporting.GetData AS BEGIN SELECT 1 END");

        // Act
        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        // Assert
        hash1.ShouldNotBe(hash2, "Procs in different schemas must produce different hashes");
    }

    [TestMethod]
    public async Task EncryptedProcedure_UsesDistinctDefinitionSentinel()
    {
        // A WITH ENCRYPTION procedure has a NULL definition, so its body cannot be hashed. It must
        // still be distinguishable from the empty-definition fallback. Two different encrypted
        // procedures with the same signature remain indistinguishable — that limitation is inherent
        // because the server exposes nothing that reflects an encrypted body.
        var dbName = await CreateTestDatabaseAsync("EncProc");

        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE Secret WITH ENCRYPTION AS BEGIN SELECT 42 END");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE Plain AS BEGIN SELECT 42 END");

        var procs = (await ExtractSchemaAsync(dbName)).StoredProcedures;
        var encrypted = procs.Single(p => p.Name == "Secret");
        var plain = procs.Single(p => p.Name == "Plain");

        encrypted.DefinitionHash.ShouldBe("<encrypted>", "An encrypted procedure must carry a distinct sentinel rather than the empty-definition fallback");
        encrypted.DefinitionHash.ShouldNotBe(plain.DefinitionHash, "An encrypted procedure must not collide with an ordinary procedure's body hash");
    }

    [TestMethod]
    public async Task EncryptedProcedure_VsUnencrypted_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("EncVsPlain1");
        var db2Name = await CreateTestDatabaseAsync("EncVsPlain2");

        await ExecuteSqlAsync(db1Name, "CREATE PROCEDURE Secret WITH ENCRYPTION AS BEGIN SELECT 42 END");
        await ExecuteSqlAsync(db2Name, "CREATE PROCEDURE Secret AS BEGIN SELECT 42 END");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "An encrypted procedure must hash differently from an otherwise-identical unencrypted one");
    }

    [TestMethod]
    public async Task Procedure_QuotedIdentifierSetting_ChangesHash_AndIsExtracted()
    {
        // QUOTED_IDENTIFIER is baked in at CREATE time and is NOT part of the module text, so two procs
        // with byte-identical bodies but different SET state must still hash differently.
        var db1Name = await CreateTestDatabaseAsync("Qi1");
        var db2Name = await CreateTestDatabaseAsync("Qi2");

        const string procSql = "CREATE PROCEDURE P AS BEGIN SELECT 1 END";
        await ExecuteSqlAsync(db1Name, procSql); // QUOTED_IDENTIFIER ON (client default)

        // Flip the session setting and create the proc on the SAME connection so the OFF state sticks.
        await using (var connection = new SqlConnection(GetConnectionString(db2Name)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("SET QUOTED_IDENTIFIER OFF");
            await connection.ExecuteAsync(procSql);
        }

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "QUOTED_IDENTIFIER baked at create time must change the hash even with identical body text");

        var proc = (await ExtractSchemaAsync(db2Name)).StoredProcedures.Single(p => p.Name == "P");
        proc.UsesQuotedIdentifier.ShouldBeFalse("A proc created under SET QUOTED_IDENTIFIER OFF must be reported as such");
    }

    [TestMethod]
    public async Task Parameter_TableValuedType_IsSchemaQualified()
    {
        var dbName = await CreateTestDatabaseAsync("ParamTypeQualify");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.IdList AS TABLE (Id INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE P @ids dbo.IdList READONLY, @n INT AS BEGIN SELECT 1 END");

        var proc = (await ExtractSchemaAsync(dbName)).StoredProcedures.Single(p => p.Name == "P");
        proc.Parameters.Single(p => p.Name == "ids").Type.ShouldBe("dbo.IdList", "A table-valued parameter's type must be schema-qualified");
        proc.Parameters.Single(p => p.Name == "n").Type.ShouldBe("int", "A built-in parameter type must remain a bare name");
    }

    [TestMethod]
    public async Task Parameter_TypedXml_ChangesHash_AndCollectionIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("XmlParam1");
        var db2Name = await CreateTestDatabaseAsync("XmlParam2");

        const string collection = @"
            CREATE XML SCHEMA COLLECTION dbo.DocSchema AS
            '<xsd:schema xmlns:xsd=""http://www.w3.org/2001/XMLSchema""><xsd:element name=""root"" type=""xsd:string""/></xsd:schema>';";
        await ExecuteSqlAsync(db1Name, collection);
        await ExecuteSqlAsync(db1Name, "CREATE PROCEDURE P @doc XML(dbo.DocSchema) AS BEGIN SELECT 1 END");
        await ExecuteSqlAsync(db2Name, "CREATE PROCEDURE P @doc XML AS BEGIN SELECT 1 END");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A typed-XML parameter must not collide with an untyped-XML parameter");

        var p = (await ExtractSchemaAsync(db1Name)).StoredProcedures.Single(x => x.Name == "P").Parameters.Single(x => x.Name == "doc");
        p.XmlSchemaCollectionName.ShouldBe("dbo.DocSchema", "A typed-XML parameter must carry its schema-qualified collection name");
    }

    [TestMethod]
    public async Task Parameter_OutputVsInput_ChangesHash_EvenWithoutProcedureText()
    {
        // @x INT OUTPUT vs @x INT is a contract difference in sys.parameters.is_output. With
        // procedure text excluded, only the captured parameter metadata can surface it. Bodies are
        // identical so the difference is attributable solely to the OUTPUT flag.
        var db1Name = await CreateTestDatabaseAsync("ParamOut1");
        var db2Name = await CreateTestDatabaseAsync("ParamOut2");

        await ExecuteSqlAsync(db1Name, "CREATE PROCEDURE SetValue @x INT OUTPUT AS BEGIN SET @x = @x END");
        await ExecuteSqlAsync(db2Name, "CREATE PROCEDURE SetValue @x INT AS BEGIN SET @x = @x END");

        var noText = new SchemaHashOptions { Modules = ModuleNormalization.IgnoreBodyText };

        (await ExtractAndHashAsync(db1Name, noText)).ShouldNotBe(await ExtractAndHashAsync(db2Name, noText), "An OUTPUT parameter must change the hash even when stored procedure text is excluded");
    }

    [TestMethod]
    public async Task Parameter_OutputAndReadonly_AreExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("ParamDirMeta");

        await ExecuteSqlAsync(dbName, "CREATE TYPE dbo.IntList AS TABLE (Value INT NOT NULL)");
        await ExecuteSqlAsync(dbName, "CREATE PROCEDURE Process @items dbo.IntList READONLY, @count INT OUTPUT AS BEGIN SET @count = 0 END");

        var proc = (await ExtractSchemaAsync(dbName)).StoredProcedures.Single(p => p.Name == "Process");

        var items = proc.Parameters.Single(p => p.Name == "items");
        items.IsReadonly.ShouldBeTrue("A table-valued parameter is READONLY and must be reported as such");
        items.IsOutput.ShouldBeFalse("A READONLY parameter is not an output parameter");

        var count = proc.Parameters.Single(p => p.Name == "count");
        count.IsOutput.ShouldBeTrue("An OUTPUT parameter must be reported as output");
        count.IsReadonly.ShouldBeFalse("A scalar OUTPUT parameter is not readonly");
    }
}
