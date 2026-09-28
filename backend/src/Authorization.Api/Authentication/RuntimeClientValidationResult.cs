namespace Authorization.Api.Authentication;

public sealed record RuntimeClientValidationResult(
    bool Succeeded,
    string? ApplicationId,
    string? SubjectType,
    string? SubjectIdentifier,
    string? ErrorCode)
{
    public static RuntimeClientValidationResult Success(string applicationId, string subjectType, string subjectIdentifier)
    {
        return new RuntimeClientValidationResult(true, applicationId, subjectType, subjectIdentifier, null);
    }

    public static RuntimeClientValidationResult Failure(string errorCode)
    {
        return new RuntimeClientValidationResult(false, null, null, null, errorCode);
    }
}