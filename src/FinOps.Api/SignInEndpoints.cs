using System.Security.Claims;
using FinOps.Api.Security;

namespace FinOps.Api;

/// <summary>How the caller signed in, read from their validated token. Informational: it grants nothing.</summary>
/// <param name="IdentityProvider">Who authenticated the user, for example "Microsoft Entra ID" or "Google".</param>
/// <param name="Methods">The token's <c>amr</c> values, such as <c>pwd</c>, <c>mfa</c> or <c>fed</c>. Empty when not reported.</param>
/// <param name="MultiFactor">True when <c>amr</c> says MFA was performed; null when the token doesn't say.</param>
public sealed record SignInDetailsDto(string IdentityProvider, IReadOnlyList<string> Methods, bool? MultiFactor);

internal static class SignInEndpoints
{
    /// <summary>The tenant Microsoft uses for personal (consumer) accounts invited as guests.</summary>
    private const string ConsumerTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    internal static void MapSignInEndpoints(this RouteGroupBuilder api)
    {
        // Needs only an authenticated caller (the fallback policy), not a provisioned profile, so it also explains
        // the sign-in of someone who has no access yet.
        api.MapGet("/me/sign-in", (ClaimsPrincipal user) => Results.Ok(Describe(user)))
            .WithName("GetSignInDetails").WithTags("Identity")
            .WithSummary("How the caller signed in: identity provider and authentication methods (amr), from the validated token.")
            .Produces<SignInDetailsDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Entra puts the authentication methods in <c>amr</c> and, for guests from another provider, that provider in
    /// <c>idp</c>; when <c>idp</c> is absent, the token's own issuer (this tenant) authenticated the user.
    /// </summary>
    internal static SignInDetailsDto Describe(ClaimsPrincipal user)
    {
        if (user.Identity?.AuthenticationType == DevelopmentAuthenticationHandler.SchemeName)
        {
            return new SignInDetailsDto("Development persona (mock, no real sign-in)", [], null);
        }

        var methods = user.FindAll("amr").Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        bool? multiFactor = methods.Count == 0 ? null : methods.Any(m => m is "mfa" or "ngcmfa");
        return new SignInDetailsDto(ProviderName(user.FindFirst("idp")?.Value, user.FindFirst("iss")?.Value), methods, multiFactor);
    }

    private static string ProviderName(string? idp, string? issuer)
    {
        if (string.IsNullOrEmpty(idp) || string.Equals(idp, issuer, StringComparison.OrdinalIgnoreCase)) return "Microsoft Entra ID";
        if (idp.Contains("google.com", StringComparison.OrdinalIgnoreCase)) return "Google";
        if (idp.Contains("live.com", StringComparison.OrdinalIgnoreCase) || idp.Contains(ConsumerTenant, StringComparison.OrdinalIgnoreCase))
            return "Microsoft personal account";
        if (idp.Contains("sts.windows.net", StringComparison.OrdinalIgnoreCase) || idp.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Entra ID (another organization)";
        if (idp.Contains("mail", StringComparison.OrdinalIgnoreCase)) return "Email one-time passcode";
        return idp;
    }
}
