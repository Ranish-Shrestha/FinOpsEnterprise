using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FinOps.Api.Security;

/// <summary>
/// Development-only identity: trusts an <c>X-Development-User</c> object ID header. Startup refuses to
/// register this scheme outside Development. The object ID must still match a provisioned user, so the
/// same application authorization path runs as for Entra tokens.
/// </summary>
internal sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SecurityOptions> security)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "DevelopmentMock";
    internal const string Header = "X-Development-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || !Guid.TryParse(value.ToString(), out var objectId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimNames.ObjectId, objectId.ToString()),
                new Claim(ClaimNames.Scope, security.Value.ApiScope)
            ],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
