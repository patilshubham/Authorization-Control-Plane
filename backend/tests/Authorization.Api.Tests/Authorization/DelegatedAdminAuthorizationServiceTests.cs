using System.Security.Claims;
using Authorization.Api.Authorization;

namespace Authorization.Api.Tests.Authorization;

public sealed class DelegatedAdminAuthorizationServiceTests
{
    private readonly DelegatedAdminAuthorizationService authorizationService = new();

    [Fact]
    public void IsAuthorized_AllowsApplicationAdminToManageRolesForScopedApplication()
    {
        ClaimsPrincipal user = CreateUser(appRole: DelegatedAdminRoles.ApplicationAdmin, applicationId: "finance-app");

        bool result = authorizationService.IsAuthorized(user, "finance-app", DelegatedAdminCapability.ManageRoles);

        Assert.True(result);
    }

    [Fact]
    public void IsAuthorized_DeniesCrossApplicationDelegatedAdminAccess()
    {
        ClaimsPrincipal user = CreateUser(appRole: DelegatedAdminRoles.ApplicationAdmin, applicationId: "finance-app");

        bool result = authorizationService.IsAuthorized(user, "crm-app", DelegatedAdminCapability.ManageRoles);

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorized_DeniesReadOnlyViewerFromManaging()
    {
        ClaimsPrincipal user = CreateUser(appRole: DelegatedAdminRoles.ReadOnlyViewer, applicationId: "finance-app");

        bool result = authorizationService.IsAuthorized(user, "finance-app", DelegatedAdminCapability.ManagePolicies);

        Assert.False(result);
    }

    [Fact]
    public void IsAuthorized_AllowsReadOnlyViewerReadOnlyView()
    {
        ClaimsPrincipal user = CreateUser(appRole: DelegatedAdminRoles.ReadOnlyViewer, applicationId: "finance-app");

        bool result = authorizationService.IsAuthorized(user, "finance-app", DelegatedAdminCapability.ReadOnlyView);

        Assert.True(result);
    }

    [Fact]
    public void IsAuthorized_AllowsPlatformReadOnlyViewerToViewAuditOnly()
    {
        ClaimsPrincipal user = CreateUser(platformRole: DelegatedAdminRoles.PlatformReadOnlyViewer);

        Assert.True(authorizationService.IsAuthorized(user, "finance-app", DelegatedAdminCapability.ViewAudit));
        Assert.False(authorizationService.IsAuthorized(user, "finance-app", DelegatedAdminCapability.ManageRoles));
    }

    [Fact]
    public void IsAuthorized_AllowsPlatformSuperAdminForAllApplications()
    {
        ClaimsPrincipal user = CreateUser(platformRole: DelegatedAdminRoles.PlatformSuperAdmin);

        bool result = authorizationService.IsAuthorized(user, "finance-app", DelegatedAdminCapability.ManagePolicies);

        Assert.True(result);
    }

    private static ClaimsPrincipal CreateUser(string? appRole = null, string? applicationId = null, string? platformRole = null)
    {
        var claims = new List<Claim>();

        if (appRole is not null && applicationId is not null)
        {
            claims.Add(new Claim(
                DelegatedAdminClaimTypes.AppRole,
                DelegatedAdminAuthorizationService.CreateApplicationRoleValue(applicationId, appRole)));
        }

        if (platformRole is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, platformRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }
}