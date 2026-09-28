using System.Reflection;
using Authorization.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Authorization.ContractTests;

public sealed class ApiRouteContractTests
{
    [Fact]
    public void RuntimeAuthorizationController_ExposesRequiredRuntimeRoutes()
    {
        string[] routes = GetRouteTemplates(typeof(RuntimeAuthorizationController));

        Assert.Contains("v1/authorize", routes);
        Assert.Contains("v1/authorize/batch", routes);
    }

    [Fact]
    public void AdminGovernanceController_ExposesRequiredMvpAdminRoutes()
    {
        string[] routes = GetAdminRouteTemplates();

        Assert.Contains("v1/admin/applications", routes);
        Assert.Contains("v1/admin/applications/{applicationId}/roles", routes);
        Assert.Contains("v1/admin/applications/{applicationId}/permissions", routes);
        Assert.Contains("v1/admin/applications/{applicationId}/role-permissions", routes);
        Assert.Contains("v1/admin/applications/{applicationId}/assignments", routes);
        Assert.Contains("v1/admin/applications/{applicationId}/policies", routes);
        Assert.Contains("v1/admin/audit-events", routes);
        Assert.Contains("v1/admin/simulator/authorize", routes);
    }

    // The governance surface is composed of several focused controllers that all
    // share the "v1/admin" route prefix (they derive from GovernanceControllerBase).
    // Collect the route templates across every one of them.
    private static string[] GetAdminRouteTemplates()
    {
        return typeof(GovernanceControllerBase).Assembly
            .GetTypes()
            .Where(type => typeof(GovernanceControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(GetRouteTemplates)
            .ToArray();
    }

    private static string[] GetRouteTemplates(Type controllerType)
    {
        string controllerRoute = controllerType.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
        return controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttributes<HttpMethodAttribute>().FirstOrDefault())
            .Where(attribute => attribute is not null)
            .Select(attribute => Combine(controllerRoute, attribute!.Template ?? string.Empty))
            .ToArray();
    }

    private static string Combine(string prefix, string template)
    {
        return string.Join('/', new[] { prefix, template }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
