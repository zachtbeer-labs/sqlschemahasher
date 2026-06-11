using Testcontainers.MsSql;

namespace SqlSchemaHash.IntegrationTests;

[TestClass]
public class SqlServerFixture
{

    private static MsSqlContainer? _container;

    public static string ConnectionString { get; private set; } = string.Empty;

    [AssemblyInitialize]
    public static async Task AssemblyInitialize(TestContext context)
    {
        var image = Environment.GetEnvironmentVariable("SQLSERVER_IMAGE") ?? "mcr.microsoft.com/mssql/server:2025-latest";

        _container = new MsSqlBuilder()
            .WithImage(image)
            .WithPassword("DeepDishD@tabas3!")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}
