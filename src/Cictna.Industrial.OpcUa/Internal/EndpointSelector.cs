using Cictna.Industrial.Core;

namespace Cictna.Industrial.OpcUa.Internal;

/// <summary>
/// Chooses the server endpoint to connect to, from GetEndpoints and the client options.
/// </summary>
/// <remarks>
/// <para>
/// The client selects the endpoint itself (rather than letting open62541 do it during connect)
/// for three reasons: open62541's own selection reopens the channel to switch security, which
/// does not work together with our explicit reconnection policy; the selected endpoint pins the
/// server certificate; and when nothing fits, the error can say exactly what the server offers,
/// which is what an engineer on site needs to fix the configuration.
/// </para>
/// <para>
/// Rules: only UA TCP endpoints; the security mode and policy must match the options when they
/// are set; "any" mode skips secure endpoints when the client has no certificate; the endpoint
/// must accept the configured user identity. Among the matches the highest SecurityLevel wins
/// (the server's own ranking), then the stronger mode.
/// </para>
/// </remarks>
internal static class EndpointSelector
{
    private const string UaTcp = "http://opcfoundation.org/UA-Profile/Transport/uatcp-uasc-uabinary";

    public static EndpointDescription Select(IReadOnlyList<EndpointDescription> endpoints, OpcUaClientOptions options)
    {
        var tokenType = options.Identity is UserIdentity.UserNameIdentity ? UserTokenType.UserName : UserTokenType.Anonymous;

        var candidates = endpoints
            .Where(e => e.TransportProfileUri is null || e.TransportProfileUri.Length == 0 || e.TransportProfileUri == UaTcp)
            .Where(e => options.SecurityMode == MessageSecurityMode.Invalid || e.SecurityMode == options.SecurityMode)
            .Where(e => options.SecurityPolicyUri is null || e.SecurityPolicyUri == options.SecurityPolicyUri)
            .Where(e => options.Certificate is not null || e.SecurityMode == MessageSecurityMode.None)
            .ToList();

        if (candidates.Count == 0)
        {
            var reason = options.Certificate is null && options.SecurityMode is MessageSecurityMode.Sign or MessageSecurityMode.SignAndEncrypt
                ? "a certificate is required for " + options.SecurityMode
                : $"none matches mode {options.SecurityMode} and policy {Short(options.SecurityPolicyUri) ?? "any"}";
            throw new OpcUaException(StatusCodes.BadSecurityModeRejected,
                $"No usable endpoint: {reason}. The server offers {Describe(endpoints)}");
        }

        var withIdentity = candidates.Where(e => e.UserIdentityTokens.Any(t => t.TokenType == tokenType)).ToList();
        if (withIdentity.Count == 0)
        {
            throw new OpcUaException(StatusCodes.BadIdentityTokenInvalid,
                $"No matching endpoint accepts {tokenType} identities. The server offers {Describe(candidates)}");
        }

        return withIdentity
            .OrderByDescending(e => e.SecurityLevel)
            .ThenByDescending(e => e.SecurityMode)
            .First();
    }

    private static string Describe(IEnumerable<EndpointDescription> endpoints)
    {
        var parts = endpoints
            .Select(e => $"{e.SecurityMode}/{Short(e.SecurityPolicyUri)} ({string.Join(",", e.UserIdentityTokens.Select(t => t.TokenType).Distinct())})")
            .Distinct()
            .ToList();
        return parts.Count == 0 ? "no endpoints" : string.Join("; ", parts);
    }

    private static string? Short(string? policyUri) => policyUri is null ? null : policyUri[(policyUri.LastIndexOf('#') + 1)..];
}
