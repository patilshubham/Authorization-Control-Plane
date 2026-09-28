using Authorization.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Authorization.Api.Authorization;

public sealed class DelegatedAdminAuthorizationHandler : AuthorizationHandler<DelegatedAdminRequirement>
{
    private readonly DelegatedAdminAuthorizationService authorizationService;

    public DelegatedAdminAuthorizationHandler(DelegatedAdminAuthorizationService authorizationService)
    {
        this.authorizationService = authorizationService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DelegatedAdminRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext
            || !httpContext.Request.RouteValues.TryGetValue("applicationId", out object? routeValue)
            || routeValue is null)
        {
            return;
        }

        string applicationId = routeValue.ToString()!;

        // Cheap, claims-only checks first (platform + application-scoped roles).
        if (authorizationService.IsAuthorized(context.User, applicationId, requirement.Capability))
        {
            context.Succeed(requirement);
            return;
        }

        // Tenant-scoped delegated admin: only when the caller actually holds a tenant role do we
        // pay for a lookup to resolve the application's owning tenant, then re-check.
        if (!authorizationService.HasAnyTenantRole(context.User))
        {
            return;
        }

        AuthorizationDbContext dbContext = httpContext.RequestServices.GetRequiredService<AuthorizationDbContext>();
        string? tenantId = await dbContext.Applications
            .AsNoTracking()
            .Where(application => application.ApplicationId == applicationId)
            .Join(
                dbContext.Tenants.AsNoTracking(),
                application => application.TenantRefId,
                tenant => tenant.Id,
                (application, tenant) => tenant.TenantId)
            .FirstOrDefaultAsync();

        if (authorizationService.IsAuthorizedForTenant(context.User, tenantId, requirement.Capability))
        {
            context.Succeed(requirement);
        }
    }
}
