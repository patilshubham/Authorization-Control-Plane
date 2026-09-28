using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Authorization.Api.Errors;

public static class ApiErrorFactory
{
    public const string InternalErrorCode = "INTERNAL_ERROR";
    public const string ValidationErrorCode = "VALIDATION_FAILED";

    public static ApiErrorEnvelope InternalServerError(HttpContext httpContext)
    {
        return new ApiErrorEnvelope(
            new ApiError(InternalErrorCode, "An unexpected error occurred."),
            httpContext.TraceIdentifier);
    }

    public static ApiErrorEnvelope ValidationFailed(HttpContext httpContext, ModelStateDictionary modelState)
    {
        var details = modelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiErrorDetail(
                string.IsNullOrWhiteSpace(entry.Key) ? null : entry.Key,
                string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The request is invalid." : error.ErrorMessage,
                ValidationErrorCode)))
            .ToArray();

        return new ApiErrorEnvelope(
            new ApiError(ValidationErrorCode, "The request is invalid.", details),
            httpContext.TraceIdentifier);
    }
}