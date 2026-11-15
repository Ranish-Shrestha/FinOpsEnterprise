using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using FinOps.Api.Security;

namespace FinOps.Api;

internal static partial class HttpPipeline
{
    internal const string CorrelationHeader = "X-Correlation-ID";
    internal const string ApiRateLimitPolicy = "api";

    /// <summary>
    /// Accepts a well-formed inbound correlation ID (so callers can stitch logs together) or falls back
    /// to the W3C trace ID. The value is echoed in the response and attached to every log entry's scope.
    /// </summary>
    internal static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var inbound = context.Request.Headers[CorrelationHeader].ToString();
        var correlationId = SafeCorrelationId().IsMatch(inbound)
            ? inbound
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Response.Headers[CorrelationHeader] = correlationId;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("FinOps.Request");
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    });

    /// <summary>API responses are JSON only: never framed, sniffed, cached or allowed to load content.</summary>
    internal static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments(ApiDocumentation.SwaggerPath)
            ? ApiDocumentation.SwaggerContentSecurityPolicy
            : "default-src 'none'; frame-ancestors 'none'";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
        }

        return next(context);
    });

    /// <summary>
    /// Per-caller fixed-window limit for business endpoints. Authenticated callers are partitioned by
    /// Entra object ID, anonymous callers by remote address. Rejections return 429 Problem Details.
    /// </summary>
    internal static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var permitLimit = configuration.GetValue("RateLimiting:PermitLimit", 120);
        var window = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:WindowSeconds", 60));

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(ApiRateLimitPolicy, context =>
            {
                var partition = context.User.FindFirst(ClaimNames.ObjectId)?.Value
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";
                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0
                });
            });
        });
    }

    /// <summary>
    /// Cross-origin access for a SPA hosted elsewhere (Azure Static Web Apps). Only the configured origins
    /// (<c>Cors:AllowedOrigins</c>), only the methods and headers the SPA uses, and no cookies: the API is called with
    /// bearer tokens. With no origins configured, the API is same-origin only.
    /// </summary>
    internal static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        foreach (var origin in origins)
        {
            var valid = Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)
                && (uri.Scheme == Uri.UriSchemeHttps || environment.IsDevelopment());
            if (!valid)
                throw new InvalidOperationException(
                    $"Cors:AllowedOrigins entry '{origin}' must be an origin such as https://app.example.com (https outside Development, no path).");
        }

        return services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT")
            .WithHeaders("Authorization", "Content-Type", "If-Match", CorrelationHeader, "traceparent")
            .WithExposedHeaders("ETag", "Location", CorrelationHeader, "Content-Disposition")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeCorrelationId();
}
