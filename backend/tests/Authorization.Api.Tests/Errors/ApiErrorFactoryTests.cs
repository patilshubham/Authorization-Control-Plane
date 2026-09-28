using Authorization.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Authorization.Api.Tests.Errors;

public sealed class ApiErrorFactoryTests
{
    [Fact]
    public void ValidationFailed_UsesCanonicalEnvelopeWithDetailsAndCorrelationId()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-validation",
        };
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("applicationId", "The applicationId field is required.");

        ApiErrorEnvelope envelope = ApiErrorFactory.ValidationFailed(context, modelState);

        Assert.Equal(ApiErrorFactory.ValidationErrorCode, envelope.Error.Code);
        Assert.Equal("The request is invalid.", envelope.Error.Message);
        Assert.Equal("trace-validation", envelope.CorrelationId);
        ApiErrorDetail detail = Assert.Single(envelope.Error.Details!);
        Assert.Equal("applicationId", detail.Field);
        Assert.Equal("The applicationId field is required.", detail.Message);
    }
}