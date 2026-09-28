namespace Authorization.Api.Errors;

public sealed record ApiErrorEnvelope(ApiError Error, string CorrelationId);