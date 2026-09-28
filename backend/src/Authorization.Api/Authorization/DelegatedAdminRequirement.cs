using Microsoft.AspNetCore.Authorization;

namespace Authorization.Api.Authorization;

public sealed class DelegatedAdminRequirement : IAuthorizationRequirement
{
    public DelegatedAdminRequirement(DelegatedAdminCapability capability)
    {
        Capability = capability;
    }

    public DelegatedAdminCapability Capability { get; }
}