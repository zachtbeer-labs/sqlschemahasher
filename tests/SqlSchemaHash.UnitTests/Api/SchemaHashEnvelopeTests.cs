using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Api;

/// <summary>
/// Pure unit tests for the <see cref="SchemaHashResult"/> envelope: string round-tripping,
/// parsing (including legacy bare-base64 rejection), and the version/hash comparison
/// truth table. These require no database.
/// </summary>
[TestClass]
public class SchemaHashEnvelopeTests
{
    [TestMethod]
    public void ToString_RendersCanonicalEnvelope()
    {
        new SchemaHashResult(2, "dGhpcyBpc0hhc2g").ToString().ShouldBe("2:dGhpcyBpc0hhc2g");
    }

    [TestMethod]
    public void Parse_RoundTripsToString()
    {
        var original = new SchemaHashResult(2, "dGhpcyBpc0hhc2g");
        var parsed = SchemaHashResult.Parse(original.ToString());

        parsed.ShouldBe(original);
        parsed.Version.ShouldBe(2);
        parsed.Hash.ShouldBe("dGhpcyBpc0hhc2g");
    }

    [TestMethod]
    public void Parse_PreservesHashContainingBase64Symbols()
    {
        // Base64 can contain '+', '/', '=' but never ':', so the hash survives the split intact.
        var parsed = SchemaHashResult.Parse("2:ab+/cd==");
        parsed.Hash.ShouldBe("ab+/cd==");
    }

    [TestMethod]
    public void TryParse_ReturnsFalse_ForLegacyBareBase64()
    {
        // A legacy unversioned hash has no separator and must not parse.
        SchemaHashResult.TryParse("dGhpcyBpc0hhc2g", out _).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("2hash")]     // missing ':'
    [DataRow(":hash")]     // empty version
    [DataRow("x:hash")]    // non-numeric version
    [DataRow("2:")]          // empty hash
    public void TryParse_ReturnsFalse_ForMalformedInput(string? value)
    {
        SchemaHashResult.TryParse(value, out _).ShouldBeFalse();
    }

    [TestMethod]
    public void Parse_ThrowsOnMalformedInput()
    {
        Should.Throw<FormatException>(() => SchemaHashResult.Parse("not-an-envelope"));
    }

    [TestMethod]
    public void Compare_Equal_WhenVersionAndHashMatch()
    {
        SchemaHashResult.Compare("2:abc", "2:abc").ShouldBe(SchemaHashComparison.Equal);
    }

    [TestMethod]
    public void Compare_Different_WhenOnlyHashDiffers()
    {
        SchemaHashResult.Compare("2:abc", "2:xyz").ShouldBe(SchemaHashComparison.Different);
    }

    [TestMethod]
    public void Compare_Incomparable_WhenVersionDiffers()
    {
        SchemaHashResult.Compare("2:abc", "3:abc").ShouldBe(SchemaHashComparison.Incomparable);
    }

    [TestMethod]
    public void Compare_Incomparable_WhenEitherSideIsLegacyBareHash()
    {
        SchemaHashResult.Compare("2:abc", "abc").ShouldBe(SchemaHashComparison.Incomparable);
        SchemaHashResult.Compare("abc", "2:abc").ShouldBe(SchemaHashComparison.Incomparable);
    }
}
