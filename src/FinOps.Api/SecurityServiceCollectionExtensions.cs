using FinOps.Application.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
        var scheme = useMock ? DevelopmentAuthenticationHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme;
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
            AddEntraJwtBearer(authentication, settings);
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

    private static void AddEntraJwtBearer(AuthenticationBuilder authentication, SecurityOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.Audience))
        {
            throw new InvalidOperationException(
                "Configure Security:TenantId and Security:Audience for Entra authentication.");
        }

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

    private static bool HasScope(AuthorizationHandlerContext context, string requiredScope) =>
        context.User.FindAll(ClaimNames.Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);
}
