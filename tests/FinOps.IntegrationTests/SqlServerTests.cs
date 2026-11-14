using System.Net;
using System.Net.Http.Json;
using FinOps.Application.Departments;
using FinOps.Domain.Organization;
using FinOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;

namespace FinOps.IntegrationTests;

/// <summary>All Category=Database test classes share one SQL Server container and run sequentially.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}

/// <summary>
/// One disposable SQL Server for the database test collection. The fixture applies migrations and the Development demo
/// seed exactly as a developer would (`dotnet ef database update`, then `seed-demo-data`).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Keep in sync with docker-compose.yml.
    internal const string Image = "mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04";

    private readonly MsSqlContainer container = new MsSqlBuilder(Image).Build();
    private DatabaseApiFactory? factory;
    internal DatabaseApiFactory Factory =>
        factory ?? throw new InvalidOperationException("SQL Server fixture failed to initialize; is Docker running?");
    internal int MigrationOnlyUserCount { get; private set; }

    /// <summary>Connection string for another database on the same server (used by query-plan tests).</summary>
    internal string ConnectionStringFor(string database) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        factory = new DatabaseApiFactory(container.GetConnectionString());
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinOpsDbContext>();
        await db.Database.MigrateAsync();
        MigrationOnlyUserCount = await db.UserProfiles.CountAsync();
        await DemoDataSeeder.SeedAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        await container.DisposeAsync();
    }

    internal async Task<T> WithDbAsync<T>(Func<FinOpsDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<FinOpsDbContext>());
    }

    internal sealed class DatabaseApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:FinOps"] = connectionString }));
        }
    }
}

[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerTests(SqlServerFixture fixture)
{
    private HttpClient Client(DemoPersona persona) =>
        ApiFactory.CreateHttpsClient(fixture.Factory, persona.EntraObjectId.ToString());

    private async Task<string[]> DepartmentNamesFor(DemoPersona persona)
    {
        using var client = Client(persona);
        var departments = await client.GetFromJsonAsync<DepartmentDto[]>("/api/departments");
        return departments!.Select(d => d.Name).ToArray();
    }

    [Fact]
    public async Task Migrations_have_no_model_drift_and_are_repeatable()
    {
        await fixture.WithDbAsync(async db =>
        {
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            return 0;
        });
    }

    [Fact]
    public void Migrations_alone_create_no_user_accounts() =>
        Assert.Equal(0, fixture.MigrationOnlyUserCount);

    [Fact]
    public async Task Demo_seed_is_idempotent() =>
        Assert.Equal(0, await fixture.WithDbAsync(db => DemoDataSeeder.SeedAsync(db)));

    [Fact]
    public async Task Readiness_is_healthy_against_the_migrated_schema()
    {
        using var client = ApiFactory.CreateHttpsClient(fixture.Factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Employee_sees_only_their_department() =>
        Assert.Equal(["Demo Engineering"], await DepartmentNamesFor(DemoDataSeeder.Employee));

    [Fact]
    public async Task Expired_membership_grants_no_access() =>
        // The manager's Operations membership ended; only the active Finance membership counts.
        Assert.Equal(["Demo Finance"], await DepartmentNamesFor(DemoDataSeeder.Manager));

    [Fact]
    public async Task Administrator_with_global_read_sees_all_departments() =>
        Assert.Equal(["Demo Engineering", "Demo Finance", "Demo Operations"],
            await DepartmentNamesFor(DemoDataSeeder.Administrator));

    [Fact]
    public async Task Inactive_user_is_forbidden_despite_an_active_membership()
    {
        using var client = Client(DemoDataSeeder.FormerEmployee);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_department_names_are_rejected_by_sql_server()
    {
        await fixture.WithDbAsync(async db =>
        {
            db.Departments.Add(Department.Create("Demo Finance"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            return 0;
        });
    }
}
