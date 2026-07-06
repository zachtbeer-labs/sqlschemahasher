using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.IntegrationTests.Fidelity;

/// <summary>
/// Constraint-level schema fidelity: a genuine difference in a CHECK / FOREIGN KEY / UNIQUE / DEFAULT
/// constraint — enforcement state, referential actions, referenced table/column, NOT FOR REPLICATION,
/// unique-constraint-vs-index, default column placement — must change the hash, and the enforcement
/// metadata and system-named flag are pinned. Constraint-name normalization contracts live in Settings.
/// </summary>
[TestClass]
public class ConstraintFidelityTests : IntegrationTestBase
{
    [TestMethod]
    public async Task CheckConstraint_Disabled_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("CkDisabled1");
        var db2Name = await CreateTestDatabaseAsync("CkDisabled2");

        const string tableSql = "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_Age CHECK (Age >= 0))";
        await ExecuteSqlAsync(db1Name, tableSql);
        await ExecuteSqlAsync(db2Name, tableSql);

        await ExecuteSqlAsync(db2Name, "ALTER TABLE T NOCHECK CONSTRAINT CK_Age");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A disabled CHECK constraint no longer enforces data integrity and must change the hash");
    }

    [TestMethod]
    public async Task ConstraintName_IsExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("CkNameMeta");

        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, Age INT NOT NULL, CONSTRAINT CK_Age_Positive CHECK (Age >= 0))");

        var table = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "T");
        table.CheckConstraints.Single().Name.ShouldBe("CK_Age_Positive", "The constraint name must be captured");
        table.KeyConstraints.Single(c => c.Type == "PRIMARY KEY").Name.ShouldBe("PK_T");
    }

    [TestMethod]
    public async Task ForeignKey_DisabledAndUntrusted_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkTrust1");
        var db2Name = await CreateTestDatabaseAsync("FkTrust2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, PId INT NULL);";
        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        // db1: enforced and trusted. db2: added WITH NOCHECK (untrusted) then disabled.
        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child WITH CHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child NOCHECK CONSTRAINT FK_Child_Parent");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A disabled/untrusted foreign key has different enforcement semantics and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_EnforcementState_IsExtracted()
    {
        var dbName = await CreateTestDatabaseAsync("FkStateMeta");

        await ExecuteSqlAsync(dbName, @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, PId INT NULL);");
        await ExecuteSqlAsync(dbName, "ALTER TABLE Child WITH NOCHECK ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PId) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(dbName, "ALTER TABLE Child NOCHECK CONSTRAINT FK_Child_Parent");

        var fk = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "Child").ForeignKeys.Single();
        fk.IsDisabled.ShouldBeTrue("A NOCHECK CONSTRAINT foreign key must be reported as disabled");
        fk.IsNotTrusted.ShouldBeTrue("A WITH NOCHECK foreign key must be reported as untrusted");
    }

    [TestMethod]
    public async Task ForeignKey_NotForReplication_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkNfr1");
        var db2Name = await CreateTestDatabaseAsync("FkNfr2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_P PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_C PRIMARY KEY, PId INT NULL);";
        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_C FOREIGN KEY (PId) REFERENCES Parent(Id) NOT FOR REPLICATION");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_C FOREIGN KEY (PId) REFERENCES Parent(Id)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A NOT FOR REPLICATION foreign key differs behaviorally and must change the hash");
    }

    [TestMethod]
    public async Task SystemNamedConstraint_IsFlaggedAsSystemNamed()
    {
        var dbName = await CreateTestDatabaseAsync("SysNameTrue");

        // An inline PRIMARY KEY with no CONSTRAINT name gets a system-generated name.
        await ExecuteSqlAsync(dbName, "CREATE TABLE T (Id INT NOT NULL PRIMARY KEY)");

        var pk = (await ExtractSchemaAsync(dbName)).Tables.Single(t => t.Name == "T").KeyConstraints.Single(c => c.Type == "PRIMARY KEY");
        pk.IsSystemNamed.ShouldBeTrue("A system-generated PK name must report is_system_named = true");
    }

    [TestMethod]
    public async Task UniqueConstraint_VsUniqueIndex_ChangesHash_AndIsExtracted()
    {
        var db1Name = await CreateTestDatabaseAsync("UqCon1");
        var db2Name = await CreateTestDatabaseAsync("UqIdx2");

        await ExecuteSqlAsync(db1Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL CONSTRAINT UQ_T UNIQUE)");
        await ExecuteSqlAsync(db2Name, "CREATE TABLE T (Id INT NOT NULL CONSTRAINT PK_T PRIMARY KEY, A INT NOT NULL)");
        await ExecuteSqlAsync(db2Name, "CREATE UNIQUE INDEX UQ_T ON T(A)");

        (await ExtractAndHashAsync(db1Name)).ShouldNotBe(await ExtractAndHashAsync(db2Name), "A UNIQUE constraint is distinct from a plain unique index and must change the hash");

        var idx = (await ExtractSchemaAsync(db1Name)).Tables.Single(t => t.Name == "T").Indexes.Single(i => i.Name == "UQ_T");
        idx.IsUniqueConstraint.ShouldBeTrue("A unique-constraint-backed index must report is_unique_constraint = true");
    }

    [TestMethod]
    public async Task DefaultConstraint_MovedToDifferentColumn_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("DefaultCol1");
        var db2Name = await CreateTestDatabaseAsync("DefaultCol2");

        await ExecuteSqlAsync(db1Name, @"
            CREATE TABLE Settings (
                Id INT NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
                ColA INT NOT NULL CONSTRAINT DF_Settings_Value DEFAULT 0,
                ColB INT NOT NULL
            )");

        await ExecuteSqlAsync(db2Name, @"
            CREATE TABLE Settings (
                Id INT NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
                ColA INT NOT NULL,
                ColB INT NOT NULL CONSTRAINT DF_Settings_Value DEFAULT 0
            )");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A default constraint applied to a different column is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_DifferentDeleteAction_ChangesHash()
    {
        // ON DELETE CASCADE vs NO ACTION is a behavioral schema difference held in
        // sys.foreign_keys.delete_referential_action_desc, not in the FK's column list.
        var db1Name = await CreateTestDatabaseAsync("FkAction1");
        var db2Name = await CreateTestDatabaseAsync("FkAction2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON DELETE CASCADE");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON DELETE NO ACTION");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key's ON DELETE action is part of the schema and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_DifferentUpdateAction_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkUpdate1");
        var db2Name = await CreateTestDatabaseAsync("FkUpdate2");

        const string baseSql = @"
            CREATE TABLE Parent (Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON UPDATE CASCADE");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES Parent(Id) ON UPDATE NO ACTION");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key's ON UPDATE action is part of the schema and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_ReferencingDifferentColumn_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkColumn1");
        var db2Name = await CreateTestDatabaseAsync("FkColumn2");

        const string baseSql = @"
            CREATE TABLE Parent (
                Id INT NOT NULL CONSTRAINT PK_Parent PRIMARY KEY,
                AltId INT NOT NULL CONSTRAINT UQ_Parent_AltId UNIQUE
            );
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentRef INT NOT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentRef) REFERENCES Parent(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentRef) REFERENCES Parent(AltId)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key referencing a different column of the parent table is a schema difference and must change the hash");
    }

    [TestMethod]
    public async Task ForeignKey_ReferencingDifferentTable_ChangesHash()
    {
        var db1Name = await CreateTestDatabaseAsync("FkTarget1");
        var db2Name = await CreateTestDatabaseAsync("FkTarget2");

        const string baseSql = @"
            CREATE TABLE ParentA (Id INT NOT NULL CONSTRAINT PK_ParentA PRIMARY KEY);
            CREATE TABLE ParentB (Id INT NOT NULL CONSTRAINT PK_ParentB PRIMARY KEY);
            CREATE TABLE Child (Id INT NOT NULL CONSTRAINT PK_Child PRIMARY KEY, ParentId INT NOT NULL);";

        await ExecuteSqlAsync(db1Name, baseSql);
        await ExecuteSqlAsync(db2Name, baseSql);

        await ExecuteSqlAsync(db1Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES ParentA(Id)");
        await ExecuteSqlAsync(db2Name, "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES ParentB(Id)");

        var hash1 = await ExtractAndHashAsync(db1Name);
        var hash2 = await ExtractAndHashAsync(db2Name);

        hash1.ShouldNotBe(hash2, "A foreign key pointing at a different table is a schema difference and must change the hash");
    }
}
