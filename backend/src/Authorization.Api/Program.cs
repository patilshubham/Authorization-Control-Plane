using Authorization.Api.Authentication;
using Authorization.Api.Configuration;
using Authorization.Api.Errors;
using Authorization.Api.Observability;
using Authorization.Api.Startup;
using Authorization.Ai;
using Authorization.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId
        | ActivityTrackingOptions.SpanId
        | ActivityTrackingOptions.ParentId;
});
builder.Logging.AddJsonConsole(options =>
{
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions
    {
        Indented = builder.Environment.IsDevelopment(),
    };
});

// Add services to the container.

builder.Services.AddAuthorizationControlPlaneConfiguration(builder.Configuration);
builder.Services.AddAuthorizationControlPlaneAuthentication();
builder.Services.AddAuthorizationObservability();
builder.Services.AddAuthorizationInfrastructure(builder.Configuration);
builder.Services.AddAuthorizationAi(builder.Configuration);
builder.Services.AddScoped<Authorization.Api.Ai.DecisionDiagnosticsBuilder>();
builder.Services.AddScoped<Authorization.Api.Ai.ImpactAnalysisBuilder>();
builder.Services.AddScoped<Authorization.Api.Ai.ConfigAdvisorBuilder>();
builder.Services.AddScoped<Authorization.Api.Ai.AccessSearchExecutor>();
builder.Services.AddScoped<Authorization.Api.Ai.SodAnalysisBuilder>();
builder.Services.AddScoped<Authorization.Api.Ai.AccessReviewBuilder>();
builder.Services.AddScoped<Authorization.Api.Ai.SubjectPseudonymizer>();
builder.Services.AddScoped<Authorization.Api.Ai.AuditNarrativeBuilder>();
// AI usage observability: a singleton usage sink shared with the (singleton) chat transport, plus a
// scoped recorder that persists one metadata-only invocation row per model call.
builder.Services.AddSingleton<Authorization.Api.Ai.AiUsageAccumulator>();
builder.Services.AddSingleton<Authorization.Ai.IAiUsageObserver>(sp =>
    sp.GetRequiredService<Authorization.Api.Ai.AiUsageAccumulator>());
builder.Services.AddScoped<Authorization.Api.Ai.AiInvocationRecorder>();
builder.Services.AddScoped<Authorization.Api.Ai.AiUsageBuilder>();
builder.Services.AddScoped<Authorization.Api.Ai.AiPromptLogRecorder>();
builder.Services.AddControllers();
builder.Services.AddCanonicalErrorEnvelope();

// Cross-origin access for the local admin portal (and any additional configured origins).
string[] corsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:5173"];
const string PortalCorsPolicy = "PortalCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(PortalCorsPolicy, policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();
await app.UseDevelopmentDatabaseSetupAsync();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors(PortalCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains(DatabaseReadinessHealthCheck.ReadyTag),
});
app.MapControllers();

app.Run();

public partial class Program;
