using System.Text.Json.Serialization;

namespace Authorization.Api.Errors;

public sealed record ApiError(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyCollection<ApiErrorDetail>? Details = null);