using Microsoft.AspNetCore.Mvc;

namespace Authorization.Api.Errors;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCanonicalErrorEnvelope(this IServiceCollection services)
    {
        services.Configure<MvcOptions>(options =>
        {
            options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ApiErrorEnvelope), StatusCodes.Status401Unauthorized));
            options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ApiErrorEnvelope), StatusCodes.Status403Forbidden));
            options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ApiErrorEnvelope), StatusCodes.Status422UnprocessableEntity));
            options.Filters.Add(new ProducesResponseTypeAttribute(typeof(ApiErrorEnvelope), StatusCodes.Status500InternalServerError));
        });

        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var envelope = ApiErrorFactory.ValidationFailed(context.HttpContext, context.ModelState);
                return new UnprocessableEntityObjectResult(envelope);
            };
        });

        return services;
    }
}