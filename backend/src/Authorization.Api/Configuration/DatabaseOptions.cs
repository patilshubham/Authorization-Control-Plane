using System.ComponentModel.DataAnnotations;

namespace Authorization.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required]
    public string Postgres { get; init; } = string.Empty;
}