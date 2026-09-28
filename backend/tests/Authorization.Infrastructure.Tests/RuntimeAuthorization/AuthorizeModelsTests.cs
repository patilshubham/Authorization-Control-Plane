using Authorization.Infrastructure.RuntimeAuthorization;

namespace Authorization.Infrastructure.Tests.RuntimeAuthorization;

public sealed class AuthorizeModelsTests
{
    [Fact]
    public void DecisionRecord_CarriesCorrelationAndDecisionMetadata()
    {
        var record = new DecisionRecord(
            "decision-1",
            "finance-app",
            "USER",
            "bob@local.test",
            "invoice",
            "invoice-1",
            "approve",
            new Dictionary<string, object?> { ["amount"] = 100 },
            true,
            null,
            ["invoice-approver"],
            ["invoice.approve"],
            ["allow-invoice-approval-under-limit"],
            [new AuthorizeObligation("require_mfa", null)],
            DateTimeOffset.UtcNow,
            "correlation-1");

        Assert.Equal("decision-1", record.DecisionId);
        Assert.Equal("correlation-1", record.CorrelationId);
        Assert.Contains("invoice.approve", record.MatchedPermissions);
        Assert.Contains(record.Obligations, obligation => obligation.Id == "require_mfa");
    }
}
