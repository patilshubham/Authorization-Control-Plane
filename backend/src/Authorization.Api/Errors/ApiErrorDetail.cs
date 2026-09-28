using System.Text.Json.Serialization;

namespace Authorization.Api.Errors;

public sealed record ApiErrorDetail(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);