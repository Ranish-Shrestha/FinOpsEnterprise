using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinOps.Application.Departments;
using FinOps.Application.Identity;

namespace FinOps.IntegrationTests;

public sealed class HealthAndContractTests
{
    [Fact]
    public async Task Liveness_remains_healthy_when_readiness_is_unhealthy()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);
        var live = await client.GetAsync("/health");
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Healthy", (await live.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("Unhealthy", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Openapi_describes_the_development_contract()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/departments", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/me", out _));
    }

    [Fact]
    public async Task Production_does_not_expose_openapi_anonymously()
    {
        await using var factory = new ApiFactory("Production");
        using var client = ApiFactory.CreateHttpsClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Unexpected_failures_use_problem_details_without_internal_exception_text()
    {
        await using var factory = new ApiFactory(reader: new FailingReader());
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        var response = await client.GetAsync("/api/departments");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("internal-provider-detail", body);
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("traceId", out _));
    }

    private sealed class FailingReader : IDepartmentReader
    {
        public Task<IReadOnlyList<DepartmentDto>> GetAuthorizedAsync(IReadOnlyCollection<Guid> departmentIds,
            bool includeAll, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("internal-provider-detail");
    }
}

public sealed class AuthorizationTests
{
    [Fact]
    public async Task Missing_authentication_is_401()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_but_unprovisioned_user_is_403()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Unprovisioned);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Provisioned_user_without_the_permission_is_403()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.NoPermissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
        // /api/me needs only a provisioned identity, so the user can still see why access is missing.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Token_without_the_api_scope_is_403_even_for_a_permitted_user()
    {
        await using var factory = new ApiFactory(useTokenLikeTestScheme: true);
        using var client = ApiFactory.CreateHttpsClient(factory);
        client.DefaultRequestHeaders.Add("X-Test-Oid", TestUsers.Administrator);
        client.DefaultRequestHeaders.Add("X-Test-Scopes", "User.Read Other.Scope");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
    }

    [Fact]
    public async Task Token_with_the_api_scope_among_several_is_accepted()
    {
        await using var factory = new ApiFactory(useTokenLikeTestScheme: true);
        using var client = ApiFactory.CreateHttpsClient(factory);
        client.DefaultRequestHeaders.Add("X-Test-Oid", TestUsers.Employee);
        client.DefaultRequestHeaders.Add("X-Test-Scopes", "User.Read FinOps.Access");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
    }

    [Fact]
    public async Task Employee_sees_only_member_departments()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        var departments = await client.GetFromJsonAsync<DepartmentDto[]>("/api/departments");
        Assert.Equal(TestDepartments.Engineering, Assert.Single(departments!));
    }

    [Fact]
    public async Task Global_read_permission_sees_all_departments()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Administrator);
        var departments = await client.GetFromJsonAsync<DepartmentDto[]>("/api/departments");
        Assert.Equal(TestDepartments.All.Length, departments!.Length);
    }

    [Fact]
    public async Task Current_user_reports_application_owned_access()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Administrator);
        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/me");
        Assert.Contains(ApplicationPermissions.DepartmentsReadAll, me!.Permissions);
    }

    [Fact]
    public async Task Access_is_resolved_once_per_request()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        await client.GetAsync("/api/departments");
        Assert.Equal(1, factory.UserAccess.Lookups);
    }

    [Fact]
    public void Mock_authentication_outside_development_fails_startup()
    {
        using var factory = new ApiFactory("Production",
            settings: new Dictionary<string, string?> { ["Security:UseDevelopmentMockAuthentication"] = "true" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("cannot be enabled outside Development", error.Message);
    }

    [Fact]
    public void Entra_mode_without_tenant_or_audience_fails_startup()
    {
        using var factory = new ApiFactory("Production",
            settings: new Dictionary<string, string?> { ["Security:TenantId"] = "", ["Security:Audience"] = "" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Security:TenantId", error.Message);
    }
}

public sealed class HttpHardeningTests
{
    [Fact]
    public async Task Safe_inbound_correlation_id_is_echoed()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "client-req.42");
        var response = await client.GetAsync("/health");
        Assert.Equal("client-req.42", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Unsafe_inbound_correlation_id_is_replaced()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-ID", "bad value\"<script>");
        var response = await client.GetAsync("/health");
        var echoed = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.NotEqual("bad value\"<script>", echoed);
        Assert.Matches("^[A-Za-z0-9._-]+$", echoed);
    }

    [Fact]
    public async Task Api_responses_carry_security_headers_and_are_not_cached()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        var response = await client.GetAsync("/api/departments");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Api_requests_over_the_limit_are_rejected_with_429()
    {
        await using var factory = new ApiFactory(settings: new Dictionary<string, string?>
        {
            ["RateLimiting:PermitLimit"] = "2",
            ["RateLimiting:WindowSeconds"] = "60"
        });
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Employee);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/departments")).StatusCode);
        // Health probes are not business endpoints and are not rate limited.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
