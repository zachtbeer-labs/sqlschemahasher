using System.Reflection;
using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Extraction;

/// <summary>
/// Pins <see cref="SchemaExtractor"/>'s private empty-definition sentinel: it must stay <c>"0"</c> to
/// match SQL Server's own <c>CHECKSUM('')</c>, so a refactor of the fallback used when a procedure's
/// definition hash is unavailable (and it is not an encrypted module, which gets a distinct sentinel)
/// can't silently change this subtle coupling.
/// </summary>
[TestClass]
public class EmptyDefinitionSentinelTests
{
    [TestMethod]
    public void ComputeEmptyDefinitionHash_MatchesServerSideChecksumOfEmptyString()
    {
        var method = typeof(SchemaExtractor).GetMethod("ComputeEmptyDefinitionHash", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull("SchemaExtractor.ComputeEmptyDefinitionHash must still exist");

        var result = (string)method!.Invoke(null, null)!;

        result.ShouldBe("0", "The empty-definition sentinel must match SQL Server's CHECKSUM('') = 0");
    }
}
