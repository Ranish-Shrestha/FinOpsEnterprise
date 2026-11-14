namespace FinOps.Api.Security;
public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    public string TenantId { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ApiScope { get; set; } = "FinOps.Access";
    public bool UseDevelopmentMockAuthentication { get; set; }
}
