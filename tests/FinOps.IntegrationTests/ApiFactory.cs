using System.Security.Claims;
using System.Text.Encodings.Web;
using FinOps.Application.Departments;
using FinOps.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinOps.IntegrationTests;

/// <summary>Synthetic callers understood by <see cref="ApiFactory"/>'s access reader double.</summary>
internal static class TestUsers
{
    internal const string Employee = "20000000-0000-0000-0000-000000000001";
    internal const string Administrator = "20000000-0000-0000-0000-000000000003";
    internal const string NoPermissions = "20000000-0000-0000-0000-000000000098";
    internal const string Unprovisioned = "20000000-0000-0000-0000-000000000099";
}

internal static class TestDepartments
{
    internal static readonly DepartmentDto Engineering = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Synthetic Engineering");
    internal static readonly DepartmentDto Finance = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Synthetic Finance");
    internal static readonly DepartmentDto Operations = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Synthetic Operations");
    internal static readonly DepartmentDto[] All = [Engineering, Finance, Operations];
}

/// <summary>
/// HTTP-level host with explicit doubles for persistence and readiness. Validates routing, authorization
/// wiring and error contracts, not database behavior (see SqlServerTests for that).
/// </summary>
internal sealed class ApiFactory(
    string environment = "Development",
    IDepartmentReader? reader = null,
    bool useTokenLikeTestScheme = false,
    IDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? testServices = null) : WebApplicationFactory<Program>
{
    public CountingUserAccessReader UserAccess { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var mock = environment == "Development" && !useTokenLikeTestScheme ? "true" : "false";
        var configuration = new Dictionary<string, string?>
        {
            // Not contacted in HTTP-only tests. No password or external SQL dependency.
            ["ConnectionStrings:FinOps"] = "Server=127.0.0.1;Database=FinOpsHttpTests;Integrated Security=True;Connect Timeout=1",
            ["Security:UseDevelopmentMockAuthentication"] = mock,
            ["Security:TenantId"] = "00000000-0000-0000-0000-000000000000",
            ["Security:Audience"] = "api://finops-tests"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) configuration[key] = value;

        builder.UseEnvironment(environment);
        foreach (var (key, value) in configuration) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDepartmentReader>();
            services.AddSingleton(reader ?? new FilteringDepartmentReader());
            services.RemoveAll<IUserAccessReader>();
            services.AddSingleton<IUserAccessReader>(UserAccess);
            services.RemoveAll<IConfigureOptions<HealthCheckServiceOptions>>();
            services.AddHealthChecks().AddCheck("database", () => HealthCheckResult.Unhealthy(), tags: ["ready"]);
        });

        if (testServices is not null) builder.ConfigureTestServices(testServices);

        if (useTokenLikeTestScheme)
        {
            // Stands in for JWT bearer: claims come from headers so scope handling can be tested without Entra.
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TokenLikeTestHandler>(
                    TokenLikeTestHandler.SchemeName, _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TokenLikeTestHandler.SchemeName;
                    options.DefaultChallengeScheme = TokenLikeTestHandler.SchemeName;
                });
            });
        }
    }

    internal static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory, string? objectId = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        if (objectId is not null) client.DefaultRequestHeaders.Add("X-Development-User", objectId);
        return client;
    }

    /// <summary>Mirrors the real reader's contract: membership filter unless the caller may read all.</summary>
    internal sealed class FilteringDepartmentReader : IDepartmentReader
    {
        public Task<IReadOnlyList<DepartmentDto>> GetAuthorizedAsync(IReadOnlyCollection<Guid> departmentIds,
            bool includeAll, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DepartmentDto>>(
                TestDepartments.All.Where(d => includeAll || departmentIds.Contains(d.Id)).ToList());
    }

    internal sealed class CountingUserAccessReader : IUserAccessReader
    {
        private int lookups;
        public int Lookups => lookups;

        public Task<CurrentUserDto?> FindByEntraObjectIdAsync(Guid objectId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref lookups);
            CurrentUserDto? user = objectId.ToString() switch
            {
                TestUsers.Employee => new(Guid.NewGuid(), "HTTP Employee", "employee@example.invalid",
                    ["Employee"], [ApplicationPermissions.DepartmentsRead, ApplicationPermissions.ExpensesCreate],
                    [TestDepartments.Engineering.Id]),
                TestUsers.Administrator => new(Guid.NewGuid(), "HTTP Administrator", "admin@example.invalid",
                    ["Administrator"], [ApplicationPermissions.DepartmentsRead, ApplicationPermissions.DepartmentsReadAll], []),
                TestUsers.NoPermissions => new(Guid.NewGuid(), "HTTP No Permissions", "none@example.invalid",
                    [], [], [TestDepartments.Engineering.Id]),
                _ => null
            };
            return Task.FromResult(user);
        }
    }

    private sealed class TokenLikeTestHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "TokenLikeTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var objectId = Request.Headers["X-Test-Oid"].ToString();
            if (string.IsNullOrEmpty(objectId)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("oid", objectId) };
            var scopes = Request.Headers["X-Test-Scopes"].ToString();
            if (scopes.Length > 0) claims.Add(new Claim("scp", scopes));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
