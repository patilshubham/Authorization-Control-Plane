using Authorization.Api.Contracts;

namespace Authorization.Api.Configuration;

/// <summary>
/// Server-owned presentation/runtime knobs surfaced to the portal via <c>GET /v1/config</c>.
/// The client treats the server as the single source of truth for these values instead of
/// hardcoding them, so operators can tune pagination, cache freshness, reporting windows and
/// role display labels per deployment without a frontend rebuild. Bound from the <c>Portal</c>
/// configuration section; every value has a safe default matching historical behaviour.
/// </summary>
public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    public PortalPaginationOptions Pagination { get; init; } = new();

    public PortalCacheOptions Cache { get; init; } = new();

    public PortalUiOptions Ui { get; init; } = new();

    /// <summary>
    /// Friendly display labels for well-known actor roles (keyed by the normalised, lower-cased
    /// role identifier). Unknown roles are humanised client-side, so this only needs entries that
    /// benefit from a curated label.
    /// </summary>
    public Dictionary<string, string> RoleLabels { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["platformsuperadmin"] = "Platform Super Admin",
        ["platformreadonlyviewer"] = "Platform Read-only Viewer",
        ["applicationadmin"] = "Application Admin",
        ["readonlyviewer"] = "Read-only Viewer",
    };
}

/// <summary>Pagination defaults and allowed page-size choices for portal tables.</summary>
public sealed class PortalPaginationOptions
{
    public int DefaultPageSize { get; init; } = PageRequest.DefaultPageSize;

    public int MaxPageSize { get; init; } = PageRequest.MaxPageSize;

    public int[] PageSizeOptions { get; init; } = [10, 25, 50, 100];
}

/// <summary>Client cache freshness windows (milliseconds) for React Query staleness.</summary>
public sealed class PortalCacheOptions
{
    /// <summary>Default staleness for most list/detail queries.</summary>
    public int DefaultStaleMs { get; init; } = 30_000;

    /// <summary>Shorter staleness for volatile data (e.g. assignments, decisions).</summary>
    public int VolatileStaleMs { get; init; } = 10_000;

    /// <summary>Longer staleness for rarely-changing runtime config/feature flags.</summary>
    public int ConfigStaleMs { get; init; } = 300_000;
}

/// <summary>Miscellaneous UI windows/limits owned by the server.</summary>
public sealed class PortalUiOptions
{
    /// <summary>Selectable trailing-day windows for AI usage reporting.</summary>
    public int[] AiReportingWindows { get; init; } = [7, 30, 90];

    /// <summary>Number of trailing days rendered in activity trend charts.</summary>
    public int ActivityTrendDays { get; init; } = 14;

    /// <summary>Bounded page size used when pulling audit history for feeds/heatmaps.</summary>
    public int AuditPageSize { get; init; } = PageRequest.MaxPageSize;
}
