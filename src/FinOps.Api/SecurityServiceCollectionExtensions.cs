using FinOps.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinOps.Api.Security;

internal static class SecurityServiceCollectionExtensions
{
    internal static IServiceCollection AddFinOpsSecurity(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var section = configuration.GetSection(SecurityOptions.SectionName);
        var settings = section.Get<SecurityOptions>() ?? new();
        services.Configure<SecurityOptions>(section);

        if (settings.UseDevelopmentMockAuthentication && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Development mock authentication cannot be enabled outside Development.");
        }

        var useMock = environment.IsDevelopment() && settings.UseDevelopmentMockAuthentication;
        var scheme = useMock ? DevelopmentAuthenticationHandler.SchemeName
            : !settings.GoogleConfigured ? JwtBearerDefaults.AuthenticationScheme
            : settings.EntraConfigured ? TokenSelectorScheme
            : GoogleAccounts.SchemeName;
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = scheme;
            options.DefaultChallengeScheme = scheme;
        });

        if (useMock)
        {
            authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                DevelopmentAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            if (!settings.EntraConfigured && !settings.GoogleConfigured)
            {
                throw new InvalidOperationException(
                    "Configure Security:TenantId and Security:Audience for Entra authentication, "
                    + "and/or Security:GoogleClientId for Google sign-in.");
            }

            if (settings.EntraConfigured) AddEntraJwtBearer(authentication, settings);
            if (settings.GoogleConfigured) AddGoogleJwtBearer(authentication, settings);
            if (settings.EntraConfigured && settings.GoogleConfigured)
            {
                // Picks a validator from the token's unverified issuer. The chosen scheme then verifies everything,
                // so a forged issuer only selects a validator that rejects the token.
                authentication.AddPolicyScheme(TokenSelectorScheme, "Entra or Google", options =>
                    options.ForwardDefaultSelector = context =>
                        GoogleAccounts.IsGoogleIssuer(UnverifiedIssuer(context.Request.Headers.Authorization.ToString()))
                            ? GoogleAccounts.SchemeName
                            : JwtBearerDefaults.AuthenticationScheme);
            }
        }

        var requireScope = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context => HasScope(context, settings.ApiScope))
            .Build();

        var authorization = services.AddAuthorizationBuilder().SetFallbackPolicy(requireScope);

        // One policy per catalogued permission, named after the permission itself.
        foreach (var permission in ApplicationPermissions.All)
        {
            authorization.AddPolicy(permission, policy => policy
                .Combine(requireScope)
                .AddRequirements(new PermissionRequirement(permission)));
        }

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUserAccess>();
        return services;
    }

    private const string TokenSelectorScheme = "EntraOrGoogle";

    private static void AddEntraJwtBearer(AuthenticationBuilder authentication, SecurityOptions settings)
    {
        authentication.AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
            options.Audience = settings.Audience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        });
    }

    /// <summary>Google ID tokens for this app's Google OAuth client. See <see cref="GoogleAccounts"/>.</summary>
    private static void AddGoogleJwtBearer(AuthenticationBuilder authentication, SecurityOptions settings)
    {
        authentication.AddJwtBearer(GoogleAccounts.SchemeName, options =>
        {
            options.Authority = GoogleAccounts.Issuer;
            options.Audience = settings.GoogleClientId;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = [GoogleAccounts.Issuer, "accounts.google.com"],
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    GoogleAccounts.AddApplicationClaims(context.Principal, settings.ApiScope);
                    return Task.CompletedTask;
                }
            };
        });
    }

    /// <summary>The <c>iss</c> of a bearer token, read without validation. Used only to choose a validator.</summary>
    private static string? UnverifiedIssuer(string authorization)
    {
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[prefix.Length..].Trim();
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(token)) return null;
        try { return handler.ReadJsonWebToken(token).Issuer; }
        catch (ArgumentException) { return null; }
    }

    private static bool HasScope(AuthorizationHandlerContext context, string requiredScope) =>
        context.User.FindAll(ClaimNames.Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);
}
