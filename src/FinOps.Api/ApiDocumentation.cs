using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace FinOps.Api;

/// <summary>
/// The OpenAPI document (<c>/openapi/v1.json</c>) and Swagger UI (<c>/swagger</c>). Both exist only in Development.
/// The document describes how to authenticate, so "Authorize" in Swagger UI works: an Entra access token always,
/// and the Development persona header when mock authentication is on. A committed copy lives in docs/api/openapi.json,
/// and a test fails when it drifts from the code.
/// </summary>
internal static class ApiDocumentation
{
    internal const string BearerScheme = "EntraBearer";
    internal const string DevelopmentUserScheme = "DevelopmentUser";
    internal const string SwaggerPath = "/swagger";

    /// <summary>Swagger UI needs its own scripts, inline styles and data-URI icons; nothing from other origins.</summary>
    internal const string SwaggerContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    internal static IServiceCollection AddFinOpsOpenApi(this IServiceCollection services, bool mockAuthentication) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "FinOps Enterprise API";
                document.Info.Description =
                    "Expenses, approvals, budgets, reports and notifications. Every `/api` call needs a caller: " +
                    "the token identifies them, and their roles, permissions and department memberships come from " +
                    "the application's database. Writes to an expense need `If-Match` with the ETag from the last read.";
                // The UI and clients call the origin they loaded the document from; no host is baked in.
                document.Servers?.Clear();

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "A Microsoft Entra access token for this API with the `FinOps.Access` scope.",
                };
                if (mockAuthentication)
                {
                    document.Components.SecuritySchemes[DevelopmentUserScheme] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.ApiKey,
                        In = ParameterLocation.Header,
                        Name = "X-Development-User",
                        Description =
                            "**Development only.** The object ID of a synthetic persona from `seed-demo-data`:\n\n" +
                            "| Persona | Object ID |\n| --- | --- |\n" +
                            "| Demo Employee (Engineering) | `20000000-0000-0000-0000-000000000001` |\n" +
                            "| Demo Manager (Finance) | `20000000-0000-0000-0000-000000000002` |\n" +
                            "| Demo Administrator | `20000000-0000-0000-0000-000000000003` |\n" +
                            "| Demo Former Employee (inactive) | `20000000-0000-0000-0000-000000000004` |\n" +
                            "| Demo Finance Officer | `20000000-0000-0000-0000-000000000005` |\n" +
                            "| Demo Engineering Manager | `20000000-0000-0000-0000-000000000006` |",
                    };
                }

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
                {
                    return Task.CompletedTask;
                }

                // Separate requirements mean "either scheme".
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(BearerScheme, context.Document)] = [],
                });
                if (mockAuthentication)
                {
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(DevelopmentUserScheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });
        });

    /// <summary>
    /// Serves the Swagger UI page. Call it before authentication: the API's fallback policy (authenticated caller with
    /// the scope) also applies to requests that reach the authorization middleware without an endpoint.
    /// </summary>
    internal static WebApplication UseFinOpsSwaggerUi(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return app;

        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "FinOps Enterprise API v1");
            options.RoutePrefix = SwaggerPath.TrimStart('/');
            options.DocumentTitle = "FinOps Enterprise API";
            options.DisplayRequestDuration();
            options.EnableTryItOutByDefault();
        });
        return app;
    }

    internal static WebApplication MapFinOpsOpenApi(this WebApplication app)
    {
        if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();
        return app;
    }
}
