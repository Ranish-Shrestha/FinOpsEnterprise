using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FinOps.IntegrationTests;

/// <summary>
/// Runs the API's real Entra JWT bearer configuration (Production environment, no mock scheme). Only the source of
/// the OpenID metadata is replaced: instead of downloading Entra's keys, the handler is given a local signing key,
/// published under the issuer Entra uses for the tenant. Everything else (issuer, audience, lifetime, signature
/// rules, the scope policy and application-owned permissions) is the production code path.
/// </summary>
public sealed class JwtValidationTests
{
    private const string TenantId = "7a1d6c3e-5b2f-4e8a-9c0d-1f2e3a4b5c6d";
    private const string Audience = "api://finops-jwt-tests";
    private const string Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";

    private static readonly RsaSecurityKey TrustedKey = CreateKey("trusted-key");
    private static readonly RsaSecurityKey UntrustedKey = CreateKey("untrusted-key");

    [Fact]
    public async Task Valid_token_with_the_api_scope_is_accepted()
    {
        await using var factory = CreateFactory();
        using var client = Client(factory, Token());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_401_with_a_bearer_challenge()
    {
        await using var factory = CreateFactory();
        using var client = ApiFactory.CreateHttpsClient(factory);
        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    public static TheoryData<string, string> RejectedTokens() => new()
    {
        { "wrong issuer", Token(issuer: "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0") },
        { "Entra v1 issuer", Token(issuer: $"https://sts.windows.net/{TenantId}/") },
        { "wrong audience", Token(audience: "api://another-api") },
        { "signed by an untrusted key", Token(key: UntrustedKey) },
        { "expired beyond the clock skew", Token(expires: DateTime.UtcNow.AddMinutes(-5)) },
        { "not yet valid", Token(notBefore: DateTime.UtcNow.AddMinutes(10), expires: DateTime.UtcNow.AddHours(1)) },
        { "unsigned (alg none)", Token(key: null) },
        { "tampered payload", Tamper(Token()) },
        { "not a JWT", "not-a-token" },
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Invalid_tokens_are_401(string reason, string token)
    {
        await using var factory = CreateFactory();
        using var client = Client(factory, token);
        var status = (await client.GetAsync("/api/me")).StatusCode;
        Assert.True(status == HttpStatusCode.Unauthorized, $"{reason}: expected 401, got {(int)status}");
        Assert.Equal(0, factory.UserAccess.Lookups); // rejected before any database lookup
    }

    [Fact]
    public async Task Expiry_within_the_two_minute_clock_skew_is_tolerated()
    {
        await using var factory = CreateFactory();
        using var client = Client(factory, Token(expires: DateTime.UtcNow.AddSeconds(-30)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
    }

    [Fact]
    public async Task Valid_token_without_the_api_scope_is_403()
    {
        await using var factory = CreateFactory();
        using var client = Client(factory, Token(scopes: "User.Read"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
    }

    [Fact]
    public async Task Scope_in_an_app_role_claim_does_not_count()
    {
        // Permissions come from SQL; a "roles" claim (app roles) must not stand in for the delegated scope.
        await using var factory = CreateFactory();
        using var client = Client(factory, Token(scopes: null, extraClaims: new() { ["roles"] = new[] { "FinOps.Access" } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
    }

    [Fact]
    public async Task Valid_token_for_an_unprovisioned_user_is_403()
    {
        await using var factory = CreateFactory();
        using var client = Client(factory, Token(objectId: TestUsers.Unprovisioned));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Permissions_come_from_the_application_not_the_token()
    {
        // A valid token for a provisioned user with no permissions; claims in the token cannot add any.
        await using var factory = CreateFactory();
        using var client = Client(factory, Token(objectId: TestUsers.NoPermissions,
            extraClaims: new() { ["roles"] = new[] { "Administrator" }, ["permissions"] = "departments.read" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/departments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Development_user_header_is_ignored_in_production()
    {
        await using var factory = CreateFactory();
        using var client = ApiFactory.CreateHttpsClient(factory, TestUsers.Administrator);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    private static ApiFactory CreateFactory() => new(
        "Production",
        settings: new Dictionary<string, string?>
        {
            ["Security:TenantId"] = TenantId,
            ["Security:Audience"] = Audience,
        },
        testServices: services => services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            // Stands in for https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration.
            // The framework turns a preset Configuration into a static configuration manager: no network call.
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(TrustedKey);
            options.Configuration = metadata;
        }));

    private static HttpClient Client(ApiFactory factory, string token)
    {
        var client = ApiFactory.CreateHttpsClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static string Token(
        string issuer = Issuer,
        string audience = Audience,
        string objectId = TestUsers.Employee,
        string? scopes = "User.Read FinOps.Access",
        DateTime? notBefore = null,
        DateTime? expires = null,
        Dictionary<string, object>? extraClaims = null,
        RsaSecurityKey? key = null,
        bool useDefaultKey = true)
    {
        var claims = new Dictionary<string, object> { ["oid"] = objectId, ["tid"] = TenantId, ["ver"] = "2.0" };
        if (scopes is not null) claims["scp"] = scopes;
        foreach (var (name, value) in extraClaims ?? []) claims[name] = value;

        var expiry = expires ?? DateTime.UtcNow.AddHours(1);
        var start = notBefore ?? expiry.AddHours(-1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = start,
            NotBefore = start,
            Expires = expiry,
            Claims = claims,
        };
        var signingKey = key ?? (useDefaultKey ? TrustedKey : null);
        if (signingKey is not null) descriptor.SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static string Token(RsaSecurityKey? key) => key is null
        ? Token(useDefaultKey: false)
        : Token(key: key, useDefaultKey: false);

    /// <summary>Changes the payload's oid to the administrator's while keeping the original signature.</summary>
    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace(TestUsers.Employee, TestUsers.Administrator);
        return $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";
    }

    private static RsaSecurityKey CreateKey(string keyId) => new(RSA.Create(2048)) { KeyId = keyId };
}
