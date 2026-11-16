using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace FinOps.Api.Security;

/// <summary>
/// Direct "Sign in with Google": the API accepts Google ID tokens issued to this app's Google OAuth client.
/// Google identifies an account by <c>sub</c> (a string); the application identifies users by a GUID object ID, so a
/// Google account is given a stable GUID derived from its issuer and <c>sub</c>. Authorization still comes from
/// the provisioned profile for that GUID, exactly as for Entra. See docs/decisions/0009-google-sign-in.md.
/// </summary>
public static class GoogleAccounts
{
    public const string Issuer = "https://accounts.google.com";
    internal const string SchemeName = "Google";

    /// <summary>
    /// The application object ID for a Google account: the first 16 bytes of SHA-256(issuer | sub), stamped as an
    /// RFC 9562 version 8 (custom) GUID. Deterministic, so the same Google account always maps to the same user.
    /// </summary>
    public static Guid ObjectId(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Issuer}|{subject}"), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Google issues ID tokens with either form of its issuer.</summary>
    internal static bool IsGoogleIssuer(string? issuer) => issuer is Issuer or "accounts.google.com";

    /// <summary>
    /// After Google's signature, issuer, audience and lifetime checks pass: adds the derived object ID and the API
    /// scope. The audience is this app's own Google client, so a valid token was issued for FinOps only, which is
    /// what the delegated scope means for an Entra token. Any <c>oid</c> or <c>scp</c> already present is replaced.
    /// </summary>
    internal static void AddApplicationClaims(ClaimsPrincipal? principal, string apiScope)
    {
        if (principal?.Identity is not ClaimsIdentity identity) return;
        var subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject)) throw new SecurityTokenSubjectMissingException();
        foreach (var claim in identity.FindAll(c => c.Type is ClaimNames.ObjectId or ClaimNames.Scope).ToList())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(ClaimNames.ObjectId, ObjectId(subject).ToString()));
        identity.AddClaim(new Claim(ClaimNames.Scope, apiScope));
    }

    private sealed class SecurityTokenSubjectMissingException()
        : Microsoft.IdentityModel.Tokens.SecurityTokenValidationException("The Google token has no sub claim.");
}
