namespace FinOps.Api.Security;
public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public string TenantId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ApiScope { get; set; } = "FinOps.Access";
    public bool UseDevelopmentMockAuthentication { get; set; }

    /// <summary>
    /// Optional: the Google OAuth client ID (<c>….apps.googleusercontent.com</c>). When set, Google ID tokens issued
    /// to that client are accepted as well as, or instead of, Entra tokens. A public identifier, not a secret.
    /// </summary>
    public string GoogleClientId { get; set; } = string.Empty;

    internal bool EntraConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(Audience);
    internal bool GoogleConfigured => !string.IsNullOrWhiteSpace(GoogleClientId);
}
