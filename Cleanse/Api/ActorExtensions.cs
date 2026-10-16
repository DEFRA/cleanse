using System.Security.Claims;
using Cleanse.Auth;
using Cleanse.Core.Domain;

namespace Cleanse.Api;

public static class ActorExtensions
{
    /// <summary>
    /// Builds the audit actor from the authenticated principal. A caller cannot supply this, so
    /// the audit trail records who actually made the call rather than who claimed to.
    /// </summary>
    public static Actor ToActor(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var kind = principal.FindFirst(CleanseClaims.CallerKind)?.Value;
        var name = principal.FindFirst(CleanseClaims.ServiceName)?.Value
                   ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? principal.Identity?.Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            return Actor.System;
        }

        return kind == CleanseClaims.ServiceCaller
            ? Actor.Machine(name)
            : Actor.Human(name);
    }
}
