using System.Net;
using System.Net.Http.Json;
using FinOps.Api;
using FinOps.Api.Security;
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
    private static readonly RsaSecurityKey GoogleKey = CreateKey("google-key");
    private const string GoogleClientId = "finops-tests.apps.googleusercontent.com";

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

    public static TheoryData<string, Dictionary<string, object>, string, bool?> SignIns() => new()
    {
        { "member with MFA", new() { ["amr"] = new[] { "pwd", "mfa" } }, "Microsoft Entra ID", true },
        { "member, password only", new() { ["amr"] = new[] { "pwd" } }, "Microsoft Entra ID", false },
        { "Gmail guest via Google federation", new() { ["idp"] = "google.com", ["amr"] = new[] { "fed" } }, "Google", false },
        { "personal Microsoft account guest", new() { ["idp"] = "live.com" }, "Microsoft personal account", null },
    };

    [Theory]
    [MemberData(nameof(SignIns))]
    public async Task Sign_in_details_come_from_the_validated_token(string scenario, Dictionary<string, object> claims,
        string provider, bool? multiFactor)
    {
        await using var factory = CreateFactory();
        using var client = Client(factory, Token(extraClaims: claims));
        var details = await client.GetFromJsonAsync<SignInDetailsDto>("/api/me/sign-in");
        Assert.NotNull(details);
        Assert.True(provider == details.IdentityProvider, $"{scenario}: {details.IdentityProvider}");
        Assert.Equal(multiFactor, details.MultiFactor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Google_id_token_maps_to_a_stable_application_account(bool entraAlsoConfigured)
    {
        await using var factory = CreateGoogleFactory(entraAlsoConfigured);
        using var client = Client(factory, GoogleToken("108123456789012345678"));

        var details = await client.GetFromJsonAsync<SignInDetailsDto>("/api/me/sign-in");
        Assert.NotNull(details);
        Assert.Equal("Google (direct sign-in)", details.IdentityProvider);
        Assert.Null(details.MultiFactor); // Google ID tokens carry no amr
        Assert.Equal(GoogleAccounts.ObjectId("108123456789012345678").ToString(), details.AccountId);

        // Authenticated, but not provisioned: the application, not Google, decides access.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(1, factory.UserAccess.Lookups);
    }

    [Fact]
    public async Task Entra_tokens_still_work_when_google_is_also_configured()
    {
        await using var factory = CreateGoogleFactory(entraAlsoConfigured: true);
        using var client = Client(factory, Token());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/departments")).StatusCode);
    }

    public static TheoryData<string, string> RejectedGoogleTokens() => new()
    {
        { "issued to another Google client", GoogleToken("1", audience: "someone-else.apps.googleusercontent.com") },
        { "signed by an untrusted key", GoogleToken("1", key: UntrustedKey) },
        { "Google issuer but signed by the Entra key", GoogleToken("1", key: TrustedKey) },
        { "Entra issuer but signed by the Google key", Token(key: GoogleKey, useDefaultKey: false) },
        { "expired", GoogleToken("1", expires: DateTime.UtcNow.AddMinutes(-5)) },
    };

    [Theory]
    [MemberData(nameof(RejectedGoogleTokens))]
    public async Task Invalid_google_tokens_are_401(string reason, string token)
    {
        await using var factory = CreateGoogleFactory(entraAlsoConfigured: true);
        using var client = Client(factory, token);
        var status = (await client.GetAsync("/api/me/sign-in")).StatusCode;
        Assert.True(status == HttpStatusCode.Unauthorized, $"{reason}: expected 401, got {(int)status}");
    }

    [Fact]
    public void Google_account_ids_are_deterministic_and_distinct()
    {
        Assert.Equal(GoogleAccounts.ObjectId("42"), GoogleAccounts.ObjectId("42"));
        Assert.NotEqual(GoogleAccounts.ObjectId("42"), GoogleAccounts.ObjectId("43"));
        Assert.Equal('8', GoogleAccounts.ObjectId("42").ToString()[14]); // RFC 9562 version 8
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

    private static ApiFactory CreateGoogleFactory(bool entraAlsoConfigured)
    {
        var settings = new Dictionary<string, string?> { ["Security:GoogleClientId"] = GoogleClientId };
        if (entraAlsoConfigured)
        {
            settings["Security:TenantId"] = TenantId;
            settings["Security:Audience"] = Audience;
        }

        return new("Production", settings: settings, testServices: services =>
        {
            // Stand-ins for the Entra and https://accounts.google.com/.well-known/openid-configuration metadata.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                metadata.SigningKeys.Add(TrustedKey);
                options.Configuration = metadata;
            });
            services.Configure<JwtBearerOptions>("Google", options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = GoogleAccounts.Issuer };
                metadata.SigningKeys.Add(GoogleKey);
                options.Configuration = metadata;
            });
        });
    }

    /// <summary>A Google ID token: no oid and no scope, only the Google account's sub.</summary>
    private static string GoogleToken(string subject, string audience = GoogleClientId, RsaSecurityKey? key = null,
        DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddHours(1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = GoogleAccounts.Issuer,
            Audience = audience,
            IssuedAt = expiry.AddHours(-1),
            NotBefore = expiry.AddHours(-1),
            Expires = expiry,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject, ["email"] = "demo@gmail.example", ["email_verified"] = true, ["nonce"] = "n",
            },
            SigningCredentials = new SigningCredentials(key ?? GoogleKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

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
