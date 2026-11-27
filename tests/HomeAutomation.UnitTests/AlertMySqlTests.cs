using Dapper;
using HomeAutomation.Application;
using HomeAutomation.Domain;
using HomeAutomation.Infrastructure.MySql;
using MySqlConnector;

namespace HomeAutomation.UnitTests;

/// <summary>
/// Rules, incidents and notifications on the disposable <c>homeautomation_alerts_test</c> schema. Each test creates its own properties.
/// Telemetry goes through the real <see cref="TelemetryWriter"/>. The runtime account has only <c>SELECT, INSERT, UPDATE</c>.
/// No provider is contacted and no device command exists anywhere on this path.
/// </summary>
public sealed class AlertMySqlTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 13, 20, 0, TimeSpan.Zero);

    [AlertMySqlFact]
    public async Task AGridOutageOpensAsWarningEscalatesDeescalatesAndResolvesAsOneIncident()
    {
        var f = await FixtureAsync();
        var setup = (await Rules(f).TemplatesAsync(f.Owner, f.Property, default))!.GridOutageSetup!;
        Assert.Equal(SignalAggregation.All, setup[0].Definition.Children[0].Condition!.Aggregation);
        Assert.Equal(ManagementStatus.Done, (await Rules(f).CreateManyAsync(f.Owner, f.Property, new(setup), default)).Status);
        var evaluator = Evaluator(f);

        // 13:20 grid lost while the battery is healthy: one Warning after the 15-second confirmation.
        await GridAsync(f, 0, T0);
        await SocAsync(f, 78, T0);
        await EvaluateAsync(f, evaluator, T0);
        Assert.Empty(await IncidentsAsync(f));
        await EvaluateAsync(f, evaluator, T0.AddSeconds(20));
        var incident = Assert.Single(await IncidentsAsync(f));
        Assert.Equal(("active", AlertSeverity.Warning, "Grid power unavailable"), (incident.Status, incident.Severity, incident.Title));
        await EvaluateAsync(f, evaluator, T0.AddSeconds(30));
        await GridAsync(f, 0, T0);
        await EvaluateAsync(f, evaluator, T0.AddSeconds(40));
        Assert.Single(await NotificationsAsync(f, f.Owner));
        Assert.Equal(3, await ScalarAsync(f, "SELECT COUNT(*) FROM notification_recipients r JOIN notifications n ON n.id = r.notification_id WHERE n.property_id = @Property"));
        Assert.Equal(3, await ScalarAsync(f, "SELECT COUNT(*) FROM notification_deliveries d JOIN notifications n ON n.id = d.notification_id WHERE n.property_id = @Property AND d.channel = 'browser' AND d.status = 'pending'"));

        var center = Center(f);
        Assert.Equal(ManagementStatus.Forbidden, (await center.AcknowledgeAsync(f.Viewer, f.Property, incident.Id, default)).Status);
        Assert.Equal(ManagementStatus.Done, (await center.AcknowledgeAsync(f.Operator, f.Property, incident.Id, default)).Status);

        // 13:25 SOC falls below 50%: the same incident escalates to Critical and needs a new acknowledgment.
        var t1 = T0.AddMinutes(5);
        await GridAsync(f, 0, t1);
        await SocAsync(f, 49, t1);
        await EvaluateAsync(f, evaluator, t1);
        await EvaluateAsync(f, evaluator, t1.AddSeconds(61));
        incident = Assert.Single(await IncidentsAsync(f));
        Assert.Equal((AlertSeverity.Critical, AlertSeverity.Warning, AlertSeverity.Critical, null),
            (incident.Severity, incident.OriginalSeverity, incident.MaxSeverity, incident.AcknowledgedAt));

        // 52% stays Critical (recovery above 55%); 58% de-escalates to Warning after the 2-minute confirmation.
        var t2 = t1.AddMinutes(5);
        await GridAsync(f, 0, t2);
        await SocAsync(f, 52, t2);
        await EvaluateAsync(f, evaluator, t2);
        await EvaluateAsync(f, evaluator, t2.AddSeconds(130));
        Assert.Equal(AlertSeverity.Critical, Assert.Single(await IncidentsAsync(f)).Severity);
        var t3 = t2.AddMinutes(5);
        await GridAsync(f, 0, t3);
        await SocAsync(f, 58, t3);
        await EvaluateAsync(f, evaluator, t3);
        Assert.Equal(AlertSeverity.Critical, Assert.Single(await IncidentsAsync(f)).Severity);
        await EvaluateAsync(f, evaluator, t3.AddSeconds(121));
        Assert.Equal(AlertSeverity.Warning, Assert.Single(await IncidentsAsync(f)).Severity);

        // 13:40 grid restored: resolved after a minute, with an Info recovery notification.
        var t4 = t3.AddMinutes(5);
        await GridAsync(f, 228, t4);
        await EvaluateAsync(f, evaluator, t4);
        Assert.Equal("active", Assert.Single(await IncidentsAsync(f)).Status);
        await EvaluateAsync(f, evaluator, t4.AddSeconds(61));
        incident = Assert.Single(await IncidentsAsync(f));
        Assert.Equal(("resolved", "recovered", AlertSeverity.Critical), (incident.Status, incident.Resolution, incident.MaxSeverity));
        Assert.All(incident.Rules, rule => Assert.False(rule.Active));

        var detail = (await center.IncidentAsync(f.Viewer, f.Property, incident.Id, default))!;
        Assert.Equal(["opened", "acknowledged", "ruleActivated", "escalated", "ruleCleared", "deescalated", "ruleCleared", "resolved"],
            detail.Timeline.Select(item => item.Kind));
        Assert.Equal(T0, detail.Timeline[0].ObservedAt);
        Assert.Equal(T0.AddSeconds(20), detail.Timeline[0].OccurredAt);
        Assert.Contains(detail.Timeline[0].Detail!.Evidence!.SelectMany(condition => condition.Signals), signal => signal.Number == 0 &&
            signal.ObservedAt == T0 && signal.Label.Contains("AC input voltage"));
        var escalation = detail.Timeline.Single(item => item.Kind == "escalated");
        Assert.Equal((AlertSeverity.Warning, AlertSeverity.Critical), (escalation.FromSeverity, escalation.ToSeverity));
        Assert.Contains(escalation.Detail!.Evidence!.SelectMany(condition => condition.Signals), signal => signal.Number == 49);
        Assert.Equal((t4.AddSeconds(61) - T0.AddSeconds(20)).TotalSeconds, detail.Timeline[^1].Detail!.DurationSeconds);
        Assert.False(detail.CanAcknowledge);

        var notifications = await NotificationsAsync(f, f.Owner);
        Assert.Equal([NotificationKind.Resolved, NotificationKind.Escalated, NotificationKind.Activated], notifications.Select(item => item.Kind));
        Assert.Equal([AlertSeverity.Info, AlertSeverity.Critical, AlertSeverity.Warning], notifications.Select(item => item.Severity));
        Assert.Contains("Recovered after", notifications[0].Body);
        Assert.Contains("49%", notifications[1].Body);
        Assert.Single(await IncidentsAsync(f, deviceId: f.InverterA));
        Assert.Empty(await IncidentsAsync(f, deviceId: f.Room));
        Assert.Single((await center.NotificationsAsync(f.Viewer, new(f.Property, Severity: AlertSeverity.Critical), default)).Items);
        Assert.Empty((await center.NotificationsAsync(f.Outsider, new(f.Property), default)).Items);
    }

    [AlertMySqlFact]
    public async Task AStaleBatteryReadingNeverCompletesACriticalCompositeButTheGridWarningStillOpens()
    {
        var f = await FixtureAsync(socAt: T0.AddDays(-1), soc: 45);
        var setup = (await Rules(f).TemplatesAsync(f.Owner, f.Property, default))!.GridOutageSetup!;
        await Rules(f).CreateManyAsync(f.Owner, f.Property, new(setup), default);
        await GridAsync(f, 0, T0);
        var evaluator = Evaluator(f);
        await EvaluateAsync(f, evaluator, T0);
        await EvaluateAsync(f, evaluator, T0.AddSeconds(90));
        var incident = Assert.Single(await IncidentsAsync(f));
        Assert.Equal((AlertSeverity.Warning, AlertSeverity.Warning), (incident.Severity, incident.MaxSeverity));
        var critical = (await Rules(f).ListAsync(f.Owner, f.Property, default))!.Rules.Single(rule => rule.Severity == AlertSeverity.Critical);
        Assert.Equal((RulePhase.Inactive, Truth.Unknown), (critical.Status.Phase, critical.Status.Result));
    }

    [AlertMySqlFact]
    public async Task LateDuplicateAndReplayedTelemetryNeitherReopensNorRenotifies()
    {
        var f = await FixtureAsync();
        await Rules(f).CreateAsync(f.Owner, f.Property, (await Rules(f).TemplatesAsync(f.Owner, f.Property, default))!.GridOutageSetup![0], default);
        var evaluator = Evaluator(f);
        await GridAsync(f, 0, T0);
        await EvaluateAsync(f, evaluator, T0);
        await EvaluateAsync(f, evaluator, T0.AddSeconds(20));
        await GridAsync(f, 230, T0.AddMinutes(5));
        await EvaluateAsync(f, evaluator, T0.AddMinutes(5));
        await EvaluateAsync(f, evaluator, T0.AddMinutes(6).AddSeconds(5));
        Assert.Equal("resolved", Assert.Single(await IncidentsAsync(f)).Status);
        var events = await ScalarAsync(f, "SELECT COUNT(*) FROM alert_incident_events WHERE property_id = @Property");

        // A delayed 0 V sample observed before the restoration, then the restoration sample again.
        await GridAsync(f, 0, T0.AddMinutes(4));
        await GridAsync(f, 230, T0.AddMinutes(5));
        foreach (var seconds in new[] { 400, 420, 500 }) await EvaluateAsync(f, evaluator, T0.AddSeconds(seconds));
        Assert.Equal("resolved", Assert.Single(await IncidentsAsync(f)).Status);
        Assert.Equal(events, await ScalarAsync(f, "SELECT COUNT(*) FROM alert_incident_events WHERE property_id = @Property"));
        Assert.Equal(2, (await NotificationsAsync(f, f.Owner)).Count);
    }

    [AlertMySqlFact]
    public async Task ARestartedEvaluatorResumesTheActiveIncidentAndConcurrentEvaluationsOpenOnlyOne()
    {
        var f = await FixtureAsync();
        await Rules(f).CreateAsync(f.Owner, f.Property, (await Rules(f).TemplatesAsync(f.Owner, f.Property, default))!.GridOutageSetup![0], default);
        await GridAsync(f, 0, T0);
        await EvaluateAsync(f, Evaluator(f), T0);
        f.Clock.Now = T0.AddSeconds(20);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Evaluator(f).EvaluateAsync(f.Property, default)));
        Assert.Single(await IncidentsAsync(f));
        Assert.Equal(1, await ScalarAsync(f, "SELECT COUNT(*) FROM alert_incident_events WHERE property_id = @Property AND kind = 'opened'"));
        Assert.Single(await NotificationsAsync(f, f.Owner));

        // A new evaluator instance has an empty cache, as after a restart; the outage continues in the same incident.
        await GridAsync(f, 0, T0.AddMinutes(5));
        await EvaluateAsync(f, Evaluator(f), T0.AddMinutes(5));
        await EvaluateAsync(f, Evaluator(f), T0.AddMinutes(10));
        Assert.Equal("active", Assert.Single(await IncidentsAsync(f)).Status);
        Assert.Single(await NotificationsAsync(f, f.Owner));
    }

    [AlertMySqlFact]
    public async Task RuleRevisionsEscalateDisablingClosesAndDeletionKeepsHistory()
    {
        var f = await FixtureAsync();
        var rules = Rules(f);
        var input = Battery(f, "Battery below 80%", AlertSeverity.Warning, 80);
        var created = await rules.CreateAsync(f.Owner, f.Property, input, default);
        var id = created.Id!.Value;
        var evaluator = Evaluator(f);
        await EvaluateAsync(f, evaluator, T0);
        Assert.Equal(AlertSeverity.Warning, Assert.Single(await IncidentsAsync(f)).Severity);

        Assert.Equal(ManagementStatus.Done, (await rules.UpdateAsync(f.Operator, f.Property, id,
            input with { Severity = AlertSeverity.Critical, ExpectedRevision = 1 }, default)).Status);
        Assert.Equal(ManagementStatus.Conflict, (await rules.UpdateAsync(f.Owner, f.Property, id, input with { ExpectedRevision = 1 }, default)).Status);
        await EvaluateAsync(f, evaluator, T0.AddMinutes(1));
        var escalated = Assert.Single(await IncidentsAsync(f));
        Assert.Equal(AlertSeverity.Critical, escalated.Severity);
        Assert.Equal(2, escalated.Rules.Single().Revision);

        Assert.Equal(ManagementStatus.Done, (await rules.SetEnabledAsync(f.Owner, f.Property, id, new(false, 2), default)).Status);
        await EvaluateAsync(f, evaluator, T0.AddMinutes(2));
        var closed = Assert.Single(await IncidentsAsync(f));
        Assert.Equal(("resolved", "ruleDisabled"), (closed.Status, closed.Resolution));
        Assert.Contains("rule was disabled", (await NotificationsAsync(f, f.Owner))[0].Body);

        Assert.Equal(ManagementStatus.Done, (await rules.DeleteAsync(f.Owner, f.Property, id, 3, default)).Status);
        Assert.Empty((await rules.ListAsync(f.Owner, f.Property, default))!.Rules);
        Assert.Null(await rules.GetAsync(f.Owner, f.Property, id, default));
        var history = (await Center(f).IncidentAsync(f.Viewer, f.Property, closed.Id, default))!;
        Assert.Equal("Battery below 80%", history.Incident.Rules.Single().RuleName);
        Assert.Equal(3, (await NotificationsAsync(f, f.Viewer)).Count);
        Assert.Equal(["deleted", "disabled", "updated", "created"],
            (await rules.RevisionsAsync(f.Owner, f.Property, id, default))!.Select(revision => revision.Action));

        // Re-enabling after the delete is impossible; a duplicate starts disabled.
        var second = (await rules.CreateAsync(f.Owner, f.Property, input, default)).Id!.Value;
        var copy = await rules.DuplicateAsync(f.Owner, f.Property, second, default);
        Assert.False((await rules.GetAsync(f.Owner, f.Property, copy.Id!.Value, default))!.Enabled);
        Assert.Equal("Copy of Battery below 80%", (await rules.GetAsync(f.Owner, f.Property, copy.Id!.Value, default))!.Name);
    }

    [AlertMySqlFact]
    public async Task RulesRejectOtherPropertiesSignalsAndFollowPropertyRoles()
    {
        var f = await FixtureAsync();
        var rules = Rules(f);
        var foreign = new AlertRuleInput("Other property", null, true, AlertSeverity.Warning, null, null,
            new RuleGroup(LogicalOperator.And, [new(new RuleCondition("c1",
                [new SignalRef(SignalKind.Measurement, DeviceId: f.OtherDevice, EndpointKey: "main", Metric: Metric.Temperature)],
                SignalAggregation.Single, ConditionOperator.LessThan, 10, Unit: "°C"))]), 0, 0);
        var rejected = await rules.CreateAsync(f.Owner, f.Property, foreign, default);
        Assert.Equal(ManagementStatus.Invalid, rejected.Status);
        Assert.Contains("not currently in this property", rejected.Message);
        Assert.Equal(ManagementStatus.Invalid, (await rules.CreateAsync(f.Owner, f.Property, Battery(f, "Wrong unit", AlertSeverity.Warning, 40) with
        {
            Definition = new RuleGroup(LogicalOperator.And, [new(new RuleCondition("c1", [Soc(f)], SignalAggregation.Single,
                ConditionOperator.LessThan, 40, Unit: "V"))]),
        }, default)).Status);
        Assert.Equal(ManagementStatus.Forbidden, (await rules.CreateAsync(f.Viewer, f.Property, Battery(f, "Viewer", AlertSeverity.Info, 40), default)).Status);
        Assert.Equal(ManagementStatus.NotFound, (await rules.CreateAsync(f.Outsider, f.Property, Battery(f, "Outsider", AlertSeverity.Info, 40), default)).Status);
        Assert.Null(await rules.ListAsync(f.Outsider, f.Property, default));
        Assert.Null(await Center(f).IncidentsAsync(f.Outsider, f.Property, new(), default));
        Assert.False((await rules.ListAsync(f.Viewer, f.Property, default))!.CanManage);
        Assert.True((await rules.ListAsync(f.Operator, f.Property, default))!.CanManage);
        Assert.Equal(ManagementStatus.Done, (await rules.CreateAsync(f.Operator, f.Property, Battery(f, "Operator", AlertSeverity.Info, 40), default)).Status);

        // A device moved away leaves the rule in place with a warning; its conditions evaluate as unknown.
        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("UPDATE devices SET retired_at_utc = @at WHERE id = @Battery", new { at = T0.UtcDateTime, f.Battery });
        var moved = (await rules.ListAsync(f.Owner, f.Property, default))!.Rules.Single();
        Assert.Contains(moved.Warnings, warning => warning.Contains("moved"));
        await EvaluateAsync(f, Evaluator(f), T0);
        Assert.Equal(Truth.Unknown, (await rules.GetAsync(f.Owner, f.Property, moved.Id, default))!.Status.Result);
        Assert.Equal(ManagementStatus.Done, (await rules.SetEnabledAsync(f.Owner, f.Property, moved.Id, new(false, moved.Revision), default)).Status);
        var refused = await rules.SetEnabledAsync(f.Owner, f.Property, moved.Id, new(true, moved.Revision + 1), default);
        Assert.Equal(ManagementStatus.Invalid, refused.Status);
        Assert.Contains("Edit the rule before enabling it", refused.Message);
    }

    [AlertMySqlFact]
    public async Task APreviewEvaluatesTheLatestTelemetryWithoutAnySideEffect()
    {
        var f = await FixtureAsync();
        await GridAsync(f, 0, T0);
        var setup = (await Rules(f).TemplatesAsync(f.Viewer, f.Property, default))!.GridOutageSetup!;
        var before = await CountsAsync(f);
        var preview = (await Rules(f).PreviewAsync(f.Viewer, f.Property, setup[1], default))!;
        Assert.True(preview.Valid);
        Assert.Equal(Truth.False, preview.Result);
        Assert.Equal([Truth.True, Truth.False], preview.Conditions.Select(condition => condition.Result));
        Assert.Equal(T0, preview.Conditions[0].Signals[0].ObservedAt);
        Assert.Equal(before, await CountsAsync(f));
        var invalid = (await Rules(f).PreviewAsync(f.Viewer, f.Property, setup[1] with { Name = "" }, default))!;
        Assert.False(invalid.Valid);
        Assert.Equal(before, await CountsAsync(f));
    }

    [AlertMySqlFact]
    public async Task TheCatalogTemplatesPreviewAndStoredRulesShareOneSignalIdentityAndDistinctLabels()
    {
        var f = await FixtureAsync(soc: 38);
        await WriteAsync(f, "daikin", "altherma", "Heatpump", DeviceKind.HeatPump, "climateControl", Capability.TemperatureMeasurement,
            Metric.OutdoorTemperature, 4, T0.AddMinutes(-1), model: "Altherma");
        await WriteAsync(f, "ewelink", "heatpump-switch", "Heatpump", DeviceKind.SmartSwitch, "main", Capability.VoltageMeasurement,
            Metric.Voltage, 231, T0.AddMinutes(-1), model: "PSF-X67");
        var rules = Rules(f);
        var catalog = (await rules.CatalogAsync(f.Viewer, f.Property, default))!;

        // Keys are the canonical identity, in the JSON spelling the browser derives from a stored SignalRef.
        Assert.All(catalog, entry => Assert.Equal(AlertSignals.Key(entry.Signal), entry.Key));
        Assert.Equal(catalog.Count, catalog.Select(entry => entry.Key).Distinct().Count());
        var soc = catalog.Single(entry => entry.Key == $"m:d{f.Battery}:battery:batterySoc");
        Assert.Equal(("Battery · Battery SOC", SignalValueType.Number, "%", 38d), (soc.Label, soc.ValueType, soc.Unit, soc.Number));
        Assert.Contains(ConditionOperator.LessThan, soc.Operators);
        Assert.Contains(catalog, entry => entry.Key == $"m:d{f.InverterA}:ac:gridVoltage" && entry.Unit == "V");

        // Two devices with the same name are distinct signals with distinguishable labels; nothing is merged.
        var offline = catalog.Where(entry => entry.Signal.EventCode == "DEVICE_OFFLINE" && entry.Label.StartsWith("Heatpump", StringComparison.Ordinal))
            .Select(entry => entry.Label).Order().ToArray();
        Assert.Equal(["Heatpump (Altherma) went offline", "Heatpump (PSF-X67) went offline"], offline);
        Assert.Equal(catalog.Count, catalog.Select(entry => entry.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Templates only use signals the catalog offers, and the grid setup reads with names, not identifiers.
        var keys = catalog.Select(entry => entry.Key).ToHashSet();
        var templates = (await rules.TemplatesAsync(f.Owner, f.Property, default))!;
        var drafts = templates.Templates.Where(template => template.Draft is not null).Select(template => template.Draft!)
            .Concat(templates.GridOutageSetup!).ToArray();
        Assert.Contains(templates.Templates, template => template.Code == "battery-low" && template.Available);
        Assert.All(drafts.SelectMany(draft => AlertSignals.Conditions(draft.Definition)).SelectMany(condition => condition.Signals),
            signal => Assert.Contains(AlertSignals.Key(signal), keys));

        // Preview evidence carries the same labels as the catalog for the same keys.
        var heatpump = catalog.Single(entry => entry.Label == "Heatpump (PSF-X67) went offline");
        var probe = templates.GridOutageSetup![1] with
        {
            Definition = new RuleGroup(LogicalOperator.And, [
                .. templates.GridOutageSetup[1].Definition.Children,
                new(new RuleCondition("c9", [heatpump.Signal], SignalAggregation.Single, ConditionOperator.EventOccurred, WindowSeconds: 900)),
            ]),
        };
        var labels = catalog.ToDictionary(entry => entry.Key, entry => entry.Label);
        var preview = (await rules.PreviewAsync(f.Viewer, f.Property, probe, default))!;
        Assert.True(preview.Valid);
        Assert.All(preview.Conditions.SelectMany(condition => condition.Signals), evidence =>
            Assert.Equal(labels[AlertSignals.Key(evidence.Signal)], evidence.Label));

        // A stored rule reads back with the same identity.
        Assert.Equal(ManagementStatus.Done, (await rules.CreateAsync(f.Owner, f.Property, templates.GridOutageSetup[0] with { Enabled = false }, default)).Status);
        var stored = (await rules.ListAsync(f.Viewer, f.Property, default))!.Rules.Single();
        Assert.All(AlertSignals.Conditions(stored.Definition).SelectMany(condition => condition.Signals), signal => Assert.Contains(AlertSignals.Key(signal), keys));
    }

    [AlertMySqlFact]
    public async Task ABatteryWarningIsItsOwnIncidentAndAnExplicitPolicySuppressesOnlyItsNotifications()
    {
        var f = await FixtureAsync();
        var templates = (await Rules(f).TemplatesAsync(f.Owner, f.Property, default))!;
        await Rules(f).CreateAsync(f.Owner, f.Property, templates.GridOutageSetup![0], default);
        var battery = templates.Templates.Single(template => template.Code == "battery-low").Draft!;
        await Rules(f).CreateAsync(f.Owner, f.Property, battery with
        {
            Notifications = new NotificationPolicy(SuppressWhileActive: "grid.outage"),
        }, default);
        await GridAsync(f, 0, T0);
        await SocAsync(f, 30, T0);
        var evaluator = Evaluator(f);
        await EvaluateAsync(f, evaluator, T0);
        await EvaluateAsync(f, evaluator, T0.AddSeconds(20));
        await EvaluateAsync(f, evaluator, T0.AddSeconds(130));
        var incidents = await IncidentsAsync(f);
        Assert.Equal(["battery.low", "grid.outage"], incidents.Select(item => item.CorrelationKey).Order());
        var notifications = await NotificationsAsync(f, f.Owner);
        var suppressed = notifications.Single(item => item.IncidentId == incidents.Single(incident => incident.CorrelationKey == "battery.low").Id);
        Assert.Equal(("suppressed:grid.outage", false), (suppressed.SuppressedReason, suppressed.InApp));
        Assert.All(suppressed.Deliveries, delivery => Assert.Equal("suppressed", delivery.Status));
        Assert.Null(notifications.Single(item => item.IncidentId != suppressed.IncidentId).SuppressedReason);
    }

    [AlertMySqlFact]
    public async Task DeliveriesReachEachRecipientsChannelsOnceAndUnknownChannelsAreRecordedAsUnsupported()
    {
        var f = await FixtureAsync();
        await Center(f).SavePreferencesAsync(f.Viewer, NotificationPreferences.Default with
        {
            Warning = new(true, false), Critical = new(false, true, true),
        }, default);
        Assert.True((await Center(f).PreferencesAsync(f.Viewer, default)).Critical.InApp);
        await Rules(f).CreateAsync(f.Owner, f.Property, (await Rules(f).TemplatesAsync(f.Owner, f.Property, default))!.GridOutageSetup![0], default);
        await GridAsync(f, 0, T0);
        var evaluator = Evaluator(f);
        await EvaluateAsync(f, evaluator, T0);
        f.Clock.Now = T0.AddSeconds(20);
        var result = await evaluator.EvaluateAsync(f.Property, default);
        Assert.True(result.IncidentsChanged);
        var notification = Assert.Single(result.Notifications);

        var browser = new RecordingChannel("browser");
        var push = new RecordingChannel("push");
        var live = new RecordingLive();
        await Dispatcher(f, live, browser).DispatchAsync(result, default);
        Assert.Equal([f.Property], live.Properties);
        Assert.Equal(new[] { f.Operator, f.Owner, f.Viewer }.Order(), live.Users.Order());
        Assert.Equal(new[] { f.Operator, f.Owner }.Order(), browser.Messages.Select(message => message.UserId).Order());
        Assert.All(browser.Messages, message => Assert.Equal(("Grid power unavailable", AlertSeverity.Warning), (message.Title, message.Severity)));
        await Dispatcher(f, live, browser).DispatchAsync(result, default);
        Assert.Equal(2, browser.Messages.Count);

        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("""
                INSERT INTO notification_deliveries (notification_id, user_id, channel, status, created_at_utc, updated_at_utc)
                VALUES (@notification, @Owner, 'push', 'pending', @at, @at), (@notification, @Viewer, 'push', 'pending', @at, @at)
                """, new { notification, f.Owner, f.Viewer, at = f.Clock.Now.UtcDateTime });
        await Dispatcher(f, live, browser).DeliverPendingAsync([notification], default);
        await Dispatcher(f, live, browser, push).DeliverPendingAsync(null, default);
        Assert.Equal(["unsupported", "unsupported"], await StatusesAsync(f, notification, "push"));
        Assert.Empty(push.Messages);

        var center = Center(f);
        Assert.Equal(ManagementStatus.Done, (await center.ReportDeliveryAsync(f.Owner, notification, new("browser", "shown", null), default)).Status);
        Assert.Equal(ManagementStatus.NotFound, (await center.ReportDeliveryAsync(f.Viewer, notification, new("browser", "shown", null), default)).Status);
        Assert.Equal(ManagementStatus.Invalid, (await center.ReportDeliveryAsync(f.Operator, notification, new("browser", "notShown", "spam"), default)).Status);
        Assert.Equal(ManagementStatus.Done, (await center.ReportDeliveryAsync(f.Operator, notification, new("browser", "notShown", "visible"), default)).Status);
        Assert.Equal(["delivered", "notShown"], await StatusesAsync(f, notification, "browser"));

        // A new pending push attempt reaches a registered channel exactly once.
        await using (var db = await f.Admin.OpenAsync(default))
            await db.ExecuteAsync("""
                INSERT INTO notification_deliveries (notification_id, user_id, channel, status, created_at_utc, updated_at_utc)
                VALUES (@notification, @Operator, 'push', 'pending', @at, @at)
                """, new { notification, f.Operator, at = f.Clock.Now.UtcDateTime });
        await Task.WhenAll(Dispatcher(f, live, push).DeliverPendingAsync(null, default), Dispatcher(f, live, push).DeliverPendingAsync(null, default));
        Assert.Equal(f.Operator, Assert.Single(push.Messages).UserId);
    }

    [AlertMySqlFact]
    public async Task ReadStateRemindersAndActiveCriticalVisibilityAreIndependent()
    {
        var f = await FixtureAsync();
        await Rules(f).CreateAsync(f.Owner, f.Property, Battery(f, "Battery critical", AlertSeverity.Critical, 80) with
        {
            Notifications = new NotificationPolicy(ReminderMinutes: 5),
        }, default);
        var evaluator = Evaluator(f);
        await EvaluateAsync(f, evaluator, T0);
        await SocAsync(f, 70, T0.AddMinutes(4));
        await EvaluateAsync(f, evaluator, T0.AddMinutes(4));
        Assert.Single(await NotificationsAsync(f, f.Owner));
        await SocAsync(f, 70, T0.AddMinutes(6));
        await EvaluateAsync(f, evaluator, T0.AddMinutes(6));
        await EvaluateAsync(f, evaluator, T0.AddMinutes(7));
        var notifications = await NotificationsAsync(f, f.Owner);
        Assert.Equal([NotificationKind.Reminder, NotificationKind.Activated], notifications.Select(item => item.Kind));

        var center = Center(f);
        var summary = await center.SummaryAsync(f.Owner, default);
        Assert.Equal((2, 2), (summary.Unread, summary.UnreadCritical));
        Assert.Equal(2, await center.MarkReadAsync(f.Owner, new(null, All: true, PropertyId: f.Property), default));
        summary = await center.SummaryAsync(f.Owner, default);
        Assert.Equal((0, 0), (summary.Unread, summary.UnreadCritical));
        Assert.Contains(summary.ActiveCritical, incident => incident.PropertyId == f.Property);
        Assert.Equal(2, (await center.SummaryAsync(f.Viewer, default)).UnreadCritical);
        Assert.DoesNotContain((await center.SummaryAsync(f.Outsider, default)).ActiveCritical, incident => incident.PropertyId == f.Property);
    }

    private static AlertRuleInput Battery(Fixture f, string name, AlertSeverity severity, double below) => new(name, null, true, severity, null, null,
        new RuleGroup(LogicalOperator.And, [new(new RuleCondition("c1", [Soc(f)], SignalAggregation.Single, ConditionOperator.LessThan, below, Unit: "%",
            FreshnessSeconds: 900))]), 0, 0);

    private static SignalRef Soc(Fixture f) => new(SignalKind.Measurement, DeviceId: f.Battery, EndpointKey: "battery", Metric: Metric.BatterySoc);

    private static async Task<Fixture> FixtureAsync(DateTimeOffset? socAt = null, double soc = 78)
    {
        var admin = Open(AdminVariable);
        await new Migrations(admin).ApplyAsync(default);
        await using var db = await admin.OpenAsync(default);
        var prefix = Guid.NewGuid().ToString("N")[..8];
        async Task<long> PropertyAsync(string name) => await db.ExecuteScalarAsync<long>("""
            INSERT INTO properties (site_key, name, time_zone) VALUES (@key, @name, 'Europe/Sofia'); SELECT LAST_INSERT_ID();
            """, new { key = Guid.NewGuid().ToString("N"), name });
        var property = await PropertyAsync("Countryside House " + prefix);
        var other = await PropertyAsync("Lake House " + prefix);
        var users = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        foreach (var user in users)
            await db.ExecuteAsync("""
                INSERT INTO identity_users (id, email, normalized_email, password_hash, security_stamp, concurrency_stamp)
                VALUES (@user, CONCAT(@user, '@example.test'), UPPER(CONCAT(@user, '@example.test')), 'x', 'x', 'x')
                """, new { user });
        await db.ExecuteAsync("""
            INSERT INTO property_memberships (property_id, user_id, role_code)
            VALUES (@property, @owner, 'Owner'), (@property, @operator, 'Operator'), (@property, @viewer, 'Viewer'), (@other, @outsider, 'Owner')
            """, new { property, other, owner = users[0], @operator = users[1], viewer = users[2], outsider = users[3] });

        var f = new Fixture(admin, Open(RuntimeVariable),
            property, other, users[0], users[1], users[2], users[3], prefix, new MutableClock(T0));
        await GridAsync(f, 230, T0.AddMinutes(-1));
        await SocAsync(f, soc, socAt ?? T0.AddMinutes(-1));
        await WriteAsync(f, "tuya", "room", "Living room", DeviceKind.Sensor, "main", Capability.TemperatureMeasurement, Metric.Temperature, 21, T0.AddMinutes(-1));
        await WriteAsync(f, "tuya", "lake", "Lake sensor", DeviceKind.Sensor, "main", Capability.TemperatureMeasurement, Metric.Temperature, 8,
            T0.AddMinutes(-1), other);
        var ids = (await db.QueryAsync<(string Name, long Id)>("SELECT name, id FROM devices WHERE property_id IN (@property, @other)",
            new { property, other })).ToDictionary(row => row.Name, row => row.Id);
        f = f with
        {
            InverterA = ids["Inverter A"], InverterB = ids["Inverter B"], Battery = ids["Battery"], Room = ids["Living room"], OtherDevice = ids["Lake sensor"],
        };
        return f;
    }

    private static async Task GridAsync(Fixture f, double volts, DateTimeOffset at)
    {
        await WriteAsync(f, "growatt", "inverter-a", "Inverter A", DeviceKind.Inverter, "ac", Capability.VoltageMeasurement, Metric.GridVoltage, volts, at);
        await WriteAsync(f, "growatt", "inverter-b", "Inverter B", DeviceKind.Inverter, "ac", Capability.VoltageMeasurement, Metric.GridVoltage, volts, at);
    }

    private static Task SocAsync(Fixture f, double soc, DateTimeOffset at) =>
        WriteAsync(f, "growatt", "battery", "Battery", DeviceKind.Battery, "battery", Capability.BatteryLevel, Metric.BatterySoc, soc, at);

    private static async Task WriteAsync(Fixture f, string provider, string external, string name, DeviceKind kind, string endpoint,
        Capability capability, Metric metric, double value, DateTimeOffset at, long? property = null, string model = "Model")
    {
        var id = $"{f.Prefix}-{external}";
        var device = new DiscoveredDevice(id, name, kind, "", "Fixture", model, [new DeviceEndpoint(endpoint, endpoint, [capability])]);
        var batch = new ObservationBatch(at, [new DeviceObservation(id, Connectivity.Online, null,
            [new EndpointObservation(endpoint, [Sample.Numeric(metric, at, value)])], at)], []);
        await new TelemetryWriter(f.Runtime, f.Clock).StoreCloudDeviceAsync(provider, property ?? f.Property, null, device, batch, default);
    }

    private static async Task EvaluateAsync(Fixture f, AlertEvaluator evaluator, DateTimeOffset at)
    {
        f.Clock.Now = at;
        await evaluator.EvaluateAsync(f.Property, default);
    }

    private static async Task<IReadOnlyList<IncidentView>> IncidentsAsync(Fixture f, long? deviceId = null) =>
        (await Center(f).IncidentsAsync(f.Owner, f.Property, new IncidentQuery(DeviceId: deviceId), default))!.Incidents;

    private static async Task<IReadOnlyList<NotificationView>> NotificationsAsync(Fixture f, string user) =>
        (await Center(f).NotificationsAsync(user, new NotificationQuery(f.Property), default)).Items;

    private static async Task<long> ScalarAsync(Fixture f, string sql)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return await db.ExecuteScalarAsync<long>(sql, new { f.Property });
    }

    private static async Task<string> CountsAsync(Fixture f)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return string.Join(",", await db.QueryAsync<long>("""
            SELECT COUNT(*) FROM alert_rule_state WHERE property_id = @Property UNION ALL
            SELECT COUNT(*) FROM alert_incidents WHERE property_id = @Property UNION ALL
            SELECT COUNT(*) FROM notifications WHERE property_id = @Property UNION ALL
            SELECT COUNT(*) FROM alert_rules WHERE property_id = @Property UNION ALL
            SELECT COUNT(*) FROM alert_evaluation_locks WHERE property_id = @Property
            """, new { f.Property }));
    }

    private static async Task<string[]> StatusesAsync(Fixture f, long notification, string channel)
    {
        await using var db = await f.Admin.OpenAsync(default);
        return (await db.QueryAsync<string>("""
            SELECT status FROM notification_deliveries WHERE notification_id = @notification AND channel = @channel ORDER BY status
            """, new { notification, channel })).ToArray();
    }

    private static AlertRuleStore Rules(Fixture f) => new(f.Runtime, f.Clock);
    private static NotificationStore Center(Fixture f) => new(f.Runtime, f.Clock);
    private static AlertEvaluator Evaluator(Fixture f) => new(f.Runtime, f.Clock, new AlertOptions());
    private static NotificationDispatcher Dispatcher(Fixture f, IAlertLiveNotifier live, params INotificationChannel[] channels) =>
        new(f.Runtime, channels, live, f.Clock, new AlertOptions());

    private const string AdminVariable = "HOMEAUTOMATION_ALERTS_TEST_MYSQL";
    private const string RuntimeVariable = "HOMEAUTOMATION_ALERTS_TEST_RUNTIME";

    private static Database Open(string variable)
    {
        var configured = Environment.GetEnvironmentVariable(variable) ?? Environment.GetEnvironmentVariable(AdminVariable)
            ?? throw new InvalidOperationException("A disposable alerts MySQL test database is required.");
        var builder = new MySqlConnectionStringBuilder(configured);
        Assert.Equal("homeautomation_alerts_test", builder.Database);
        return new Database(builder.ConnectionString);
    }

    private sealed record Fixture(Database Admin, Database Runtime, long Property, long Other, string Owner, string Operator, string Viewer,
        string Outsider, string Prefix, MutableClock Clock, long InverterA = 0, long InverterB = 0, long Battery = 0, long Room = 0, long OtherDevice = 0);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingChannel(string name) : INotificationChannel
    {
        public string Name => name;
        public List<NotificationMessage> Messages { get; } = [];
        public Task<ChannelResult> DeliverAsync(NotificationMessage message, CancellationToken ct)
        {
            lock (Messages) Messages.Add(message);
            return Task.FromResult(new ChannelResult("sent"));
        }
    }

    private sealed class RecordingLive : IAlertLiveNotifier
    {
        public List<long> Properties { get; } = [];
        public List<string> Users { get; } = [];
        public Task IncidentsChangedAsync(long propertyId, CancellationToken ct) { Properties.Add(propertyId); return Task.CompletedTask; }
        public Task NotificationsChangedAsync(IReadOnlyCollection<string> users, CancellationToken ct)
        {
            Users.Clear();
            Users.AddRange(users);
            return Task.CompletedTask;
        }
    }

    private sealed class AlertMySqlFact : FactAttribute
    {
        public AlertMySqlFact()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminVariable)))
                Skip = "Set HOMEAUTOMATION_ALERTS_TEST_MYSQL for a disposable homeautomation_alerts_test schema.";
        }
    }
}
