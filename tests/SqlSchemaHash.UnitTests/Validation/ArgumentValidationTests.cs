using zachtbeer.SqlSchemaHasher;
using Shouldly;

namespace SqlSchemaHash.UnitTests.Validation;

/// <summary>
/// Facade-level argument validation added in v2: these throw before any database connection is
/// attempted, so they require no database.
/// </summary>
[TestClass]
public class ArgumentValidationTests
{
    [TestMethod]
    public async Task GetHashAsync_NullConnectionString_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(null!));
    }

    [TestMethod]
    public async Task GetHashAsync_EmptyConnectionString_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(string.Empty));
    }

    [TestMethod]
    public async Task GetHashAsync_WhitespaceConnectionString_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync("   "));
    }

    [TestMethod]
    public async Task GetHashAsync_WithOptions_NullConnectionString_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync(null!, SchemaHashOptions.Default));
    }

    [TestMethod]
    public async Task GetHashAsync_NullOptions_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.GetHashAsync("Server=.;Database=x;", null!));
    }

    [TestMethod]
    public async Task ExtractSchemaAsync_NullConnectionString_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.ExtractSchemaAsync(null!));
    }

    [TestMethod]
    public async Task ExtractSchemaAsync_EmptyConnectionString_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.ExtractSchemaAsync(string.Empty));
    }

    [TestMethod]
    public async Task ExtractSchemaAsync_WithOptions_NullConnectionString_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.ExtractSchemaAsync(null!, SchemaHashOptions.Default));
    }

    [TestMethod]
    public async Task ExtractSchemaAsync_NullOptions_ThrowsArgumentNullException()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.ExtractSchemaAsync("Server=.;Database=x;", null!));
    }

    [TestMethod]
    public void ComputeHash_NullSchema_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => zachtbeer.SqlSchemaHasher.SqlSchemaHash.ComputeHash(null!));
    }
}
