using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeAutomation.Api;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeAutomation.IntegrationTests;

/// <summary>Routing, antiforgery, the membership filter, the schema gate, status mapping and live refresh of the alert routes.</summary>
public sealed class AlertApiTests(ApiTests.Host host) : IClassFixture<ApiTests.Host>
{
    private static readonly AlertRuleInput Rule = new("Grid power lost", null, true, AlertSeverity.Warning, "grid.outage", null,
        new RuleGroup(LogicalOperator.And, [new(new RuleCondition("c1",
            [new SignalRef(SignalKind.Measurement, DeviceId: 7, EndpointKey: "ac", Metric: Metric.GridVoltage)], SignalAggregation.Single,
            ConditionOperator.LessThanOrEqual, 10, Unit: "V"))]), 15, 60);

    /// <summary>The API reads enums as camelCase strings only, as the frontend sends them.</summary>
    private static readonly JsonSerializerOptions Json = Options();

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private HttpClient Client(string? user, bool ready = true, Live? live = null)
    {
        var client = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAlertRules>();
            services.AddSingleton<IAlertRules, FakeRules>();
            services.RemoveAll<INotificationCenter>();
            services.AddSingleton<INotificationCenter, FakeCenter>();
            services.RemoveAll<IAlertLiveNotifier>();
            services.AddSingleton<IAlertLiveNotifier>(live ?? new Live());
            services.RemoveAll<IAlertSchema>();
            services.AddSingleton<IAlertSchema>(new Schema(ready));
        })).CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    private static async Task CsrfAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
    }

    [Fact]
    public async Task RoutesNeedAuthenticationMembershipAndMigration021()
    {
        using var anonymous = Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/properties/1/alert-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/notifications/summary")).StatusCode);
        using var viewer = Client("viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/properties/1/alert-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/alert-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync("/api/v1/properties/2/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/properties/1/incidents?status=active&severity=critical&deviceId=7")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/properties/1/incidents?severity=2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/properties/1/incidents?status=open")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/notifications?kind=everything")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/notifications?kind=escalated&severity=critical&unread=true")).StatusCode);

        using var pending = Client("viewer", ready: false);
        using var unavailable = await pending.GetAsync("/api/v1/properties/1/alert-rules");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("schemaPending", await unavailable.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await pending.GetAsync("/api/v1/notifications/summary")).StatusCode);
        // Other routes stay available before migration 021.
        Assert.Equal(HttpStatusCode.OK, (await pending.GetAsync("/api/v1/properties/1/rooms")).StatusCode);
    }

    [Fact]
    public async Task MutationsNeedTheAntiforgeryTokenMapOutcomesAndRefreshOpenBrowsers()
    {
        var live = new Live();
        using var editor = Client("dashboard-editor", live: live);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostAsJsonAsync("/api/v1/properties/1/alert-rules", Rule, Json)).StatusCode);
        Assert.DoesNotContain(FakeRules.Calls, call => call.StartsWith("create:", StringComparison.Ordinal));
        await CsrfAsync(editor);
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsJsonAsync("/api/v1/properties/1/alert-rules", Rule, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/v1/properties/1/alert-rules", Rule with { Name = "forbidden" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostAsJsonAsync("/api/v1/properties/1/alert-rules", Rule with { Name = "invalid" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await editor.PutAsJsonAsync("/api/v1/properties/1/alert-rules/4", Rule with { ExpectedRevision = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync("/api/v1/properties/1/alert-rules/4", Rule with { ExpectedRevision = 2 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/v1/properties/1/alert-rules/4/enabled", new AlertEnabledRequest(false, 3), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await editor.PostAsync("/api/v1/properties/1/alert-rules/4/duplicate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.DeleteAsync("/api/v1/properties/1/alert-rules/4")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.DeleteAsync("/api/v1/properties/1/alert-rules/4?revision=4")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/v1/properties/1/alert-rules/preview", Rule, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync("/api/v1/properties/1/incidents/9/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.PostAsync("/api/v1/properties/2/incidents/9/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/v1/notifications/read", new MarkReadRequest([1, 2]), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostAsJsonAsync("/api/v1/notifications/5/delivery",
            new DeliveryOutcomeRequest("sms", "shown", null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/v1/notifications/5/delivery",
            new DeliveryOutcomeRequest("browser", "shown", null), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync("/api/v1/notifications/preferences", NotificationPreferences.Default, Json)).StatusCode);

        Assert.Contains("create:1:Grid power lost", FakeRules.Calls);
        Assert.Contains("delete:1:4:4", FakeRules.Calls);
        // Successful changes refresh the property's open views; refused ones do not.
        Assert.Equal(6, live.Properties.Count(property => property == 1));
        Assert.Equal(["dashboard-editor"], live.Users.Distinct());
    }

    private sealed class Schema(bool ready) : IAlertSchema
    {
        public Task<bool> ReadyAsync(CancellationToken ct) => Task.FromResult(ready);
    }

    private sealed class Live : IAlertLiveNotifier
    {
        public ConcurrentQueue<long> Properties { get; } = new();
        public ConcurrentQueue<string> Users { get; } = new();
        public Task IncidentsChangedAsync(long propertyId, CancellationToken ct) { Properties.Enqueue(propertyId); return Task.CompletedTask; }
        public Task NotificationsChangedAsync(IReadOnlyCollection<string> users, CancellationToken ct)
        {
            foreach (var user in users) Users.Enqueue(user);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRules : IAlertRules
    {
        public static readonly ConcurrentBag<string> Calls = [];
        private static ManagementResult Outcome(string name, long id) => name switch
        {
            "forbidden" => ManagementResult.Fail(ManagementStatus.Forbidden, "Only Owners, Admins and Operators."),
            "invalid" => ManagementResult.Fail(ManagementStatus.Invalid, "Add at least one condition."),
            _ => ManagementResult.Done(id),
        };

        public Task<AlertRuleList?> ListAsync(string user, long property, CancellationToken ct) => Task.FromResult<AlertRuleList?>(new(false, []));
        public Task<AlertRuleView?> GetAsync(string user, long property, long rule, CancellationToken ct) => Task.FromResult<AlertRuleView?>(null);
        public Task<IReadOnlyList<AlertRuleRevision>?> RevisionsAsync(string user, long property, long rule, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AlertRuleRevision>?>([]);
        public Task<IReadOnlyList<SignalCatalogEntry>?> CatalogAsync(string user, long property, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignalCatalogEntry>?>([]);
        public Task<AlertTemplateList?> TemplatesAsync(string user, long property, CancellationToken ct) =>
            Task.FromResult<AlertTemplateList?>(new([], null));
        public Task<AlertRulePreview?> PreviewAsync(string user, long property, AlertRuleInput input, CancellationToken ct) =>
            Task.FromResult<AlertRulePreview?>(new(true, [], Truth.Unknown, [], DateTimeOffset.UnixEpoch));
        public Task<ManagementResult> CreateAsync(string user, long property, AlertRuleInput input, CancellationToken ct)
        {
            Calls.Add($"create:{property}:{input.Name}");
            return Task.FromResult(Outcome(input.Name, 4));
        }
        public Task<ManagementResult> CreateManyAsync(string user, long property, AlertRuleBatchRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(5));
        public Task<ManagementResult> UpdateAsync(string user, long property, long rule, AlertRuleInput input, CancellationToken ct) =>
            Task.FromResult(input.ExpectedRevision == 1 ? ManagementResult.Fail(ManagementStatus.Conflict, "Changed.") : ManagementResult.Done(rule));
        public Task<ManagementResult> SetEnabledAsync(string user, long property, long rule, AlertEnabledRequest request, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(rule));
        public Task<ManagementResult> DuplicateAsync(string user, long property, long rule, CancellationToken ct) => Task.FromResult(ManagementResult.Done(6));
        public Task<ManagementResult> DeleteAsync(string user, long property, long rule, int expectedRevision, CancellationToken ct)
        {
            Calls.Add($"delete:{property}:{rule}:{expectedRevision}");
            return Task.FromResult(ManagementResult.Done(rule));
        }
    }

    private sealed class FakeCenter : INotificationCenter
    {
        public Task<IncidentList?> IncidentsAsync(string user, long property, IncidentQuery query, CancellationToken ct) =>
            Task.FromResult<IncidentList?>(new(false, []));
        public Task<IncidentDetail?> IncidentAsync(string user, long property, long incident, CancellationToken ct) => Task.FromResult<IncidentDetail?>(null);
        public Task<ManagementResult> AcknowledgeAsync(string user, long property, long incident, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done(incident));
        public Task<NotificationPage> NotificationsAsync(string user, NotificationQuery query, CancellationToken ct) =>
            Task.FromResult(new NotificationPage([], null));
        public Task<NotificationSummary> SummaryAsync(string user, CancellationToken ct) => Task.FromResult(new NotificationSummary(0, 0, 0, []));
        public Task<int> MarkReadAsync(string user, MarkReadRequest request, CancellationToken ct) => Task.FromResult(request.Ids?.Count ?? 0);
        public Task<ManagementResult> ReportDeliveryAsync(string user, long notification, DeliveryOutcomeRequest request, CancellationToken ct) =>
            Task.FromResult(request.Channel == "browser" ? ManagementResult.Done(notification) : ManagementResult.Fail(ManagementStatus.Invalid, "Browser only."));
        public Task<NotificationPreferences> PreferencesAsync(string user, CancellationToken ct) => Task.FromResult(NotificationPreferences.Default);
        public Task<ManagementResult> SavePreferencesAsync(string user, NotificationPreferences preferences, CancellationToken ct) =>
            Task.FromResult(ManagementResult.Done());
    }
}
