using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FinOps.IntegrationTests;

/// <summary>
/// The OpenAPI document and Swagger UI: Development only, every API operation declares how to authenticate,
/// and docs/api/openapi.json stays identical to what the code produces.
/// </summary>
public sealed class ApiDocumentationTests
{
    private const string UpdateVariable = "FINOPS_UPDATE_OPENAPI";

    [Fact]
    public async Task Swagger_ui_is_served_in_development_with_its_own_content_security_policy()
    {
        await using var factory = new ApiFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);

        var page = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var policy = string.Join(' ', page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", policy);
        Assert.DoesNotContain("unsafe-eval", policy);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);

        // The relaxed policy is for the Swagger page only; API responses keep the strict one.
        var api = await client.GetAsync("/api/me");
        Assert.Equal("default-src 'none'; frame-ancestors 'none'",
            string.Join(' ', api.Headers.GetValues("Content-Security-Policy")));
    }

    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi/v1.json")]
    public async Task Production_serves_neither_swagger_nor_the_document(string path)
    {
        await using var factory = new ApiFactory("Production");
        using var client = ApiFactory.CreateHttpsClient(factory);
        var status = (await client.GetAsync(path)).StatusCode;
        Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, $"{path}: {(int)status}");
    }

    [Fact]
    public async Task Every_api_operation_declares_authentication()
    {
        var document = await LoadDocumentAsync(new ApiFactory());

        var schemes = document["components"]!["securitySchemes"]!.AsObject();
        Assert.Equal("bearer", (string?)schemes["EntraBearer"]!["scheme"]);
        Assert.Equal("X-Development-User", (string?)schemes["DevelopmentUser"]!["name"]);
        Assert.Null(document["servers"]);

        var operations = Operations(document).ToList();
        Assert.NotEmpty(operations);
        foreach (var (path, method, operation) in operations.Where(o => o.Path.StartsWith("/api/", StringComparison.Ordinal)))
        {
            var names = operation["security"]?.AsArray()
                .SelectMany(requirement => requirement!.AsObject().Select(p => p.Key)).ToList() ?? [];
            Assert.True(names.Contains("EntraBearer") && names.Contains("DevelopmentUser"),
                $"{method.ToUpperInvariant()} {path} does not declare its authentication.");
        }
    }

    [Fact]
    public async Task Development_persona_scheme_is_described_only_with_mock_authentication()
    {
        var document = await LoadDocumentAsync(new ApiFactory(useTokenLikeTestScheme: true));
        var schemes = document["components"]!["securitySchemes"]!.AsObject();
        Assert.True(schemes.ContainsKey("EntraBearer"));
        Assert.False(schemes.ContainsKey("DevelopmentUser"));
    }

    [Fact]
    public async Task Committed_openapi_document_matches_the_code()
    {
        var current = Normalize(await LoadDocumentAsync(new ApiFactory()));
        var path = Path.Combine(Repository.Root, "docs", "api", "openapi.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, current);
            return;
        }

        Assert.True(File.Exists(path), $"Missing {path}. Generate it: set {UpdateVariable}=1 and run this test.");
        var committed = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        Assert.True(committed == current,
            $"docs/api/openapi.json is out of date. Set {UpdateVariable}=1, run this test, and commit the result.");
    }

    private static async Task<JsonNode> LoadDocumentAsync(ApiFactory factory)
    {
        await using (factory)
        {
            using var client = ApiFactory.CreateHttpsClient(factory);
            var response = await client.GetAsync("/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
    }

    private static IEnumerable<(string Path, string Method, JsonNode Operation)> Operations(JsonNode document) =>
        from path in document["paths"]!.AsObject()
        from method in path.Value!.AsObject()
        where method.Key is "get" or "post" or "put" or "patch" or "delete"
        select (path.Key, method.Key, method.Value!);

    private static string Normalize(JsonNode document) =>
        document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable markdown in descriptions
        }).ReplaceLineEndings("\n") + "\n";
}
