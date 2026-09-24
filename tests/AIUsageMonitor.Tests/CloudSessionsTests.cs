using System.Net;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Sessions;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CloudSessionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 21, 45, 0, TimeSpan.Zero);

    // The shape the Claude Code CLI reads from GET /v1/code/sessions (teleport list).
    private const string SessionsJson = """
        {"data":[
          {"id":"session_01work","title":"Raccolta subagenti","status":"active","worker_status":"running","environment_kind":"anthropic_cloud",
           "created_at":"2026-09-24T21:33:59Z","last_event_at":"2026-09-24T21:44:22Z","config":{"model":"claude-opus-5-5"},
           "external_metadata":{"usage":{"input_tokens":562,"output_tokens":21246,"cache_read_tokens":2576282,"cache_write_tokens":99384,"cost_usd":1.73}}},
          {"id":"session_01wait","title":"Deploy","worker_status":"requires_action","last_event_at":"2026-09-24T21:40:00Z",
           "external_metadata":{"pending_action":{"tool_name":"Bash","request_id":"r1"}}},
          {"id":"session_01done","title":"Fix null checks","worker_status":"idle","last_event_at":"2026-09-24T19:57:23Z",
           "post_turn_summary":{"status_detail":"PR #18 updated"}},
          {"id":"session_01old","title":"Last week","worker_status":"idle","last_event_at":"2026-09-17T10:00:00Z"},
          {"id":"session_01arch","title":"Archived","status":"archived","worker_status":"idle","last_event_at":"2026-09-24T21:00:00Z"},
          {"id":"session_01bridge","title":"Remote Control","worker_status":"running","environment_kind":"bridge","last_event_at":"2026-09-24T21:44:00Z"},
          {"title":"no id"},
          {"id":"session_01notime","worker_status":"running"}
        ]}
        """;

    // The shape of GET /v1/code/triggers: every routine with its latest run.
    private const string TriggersJson = """
        {"data":[
          {"id":"trig_1","name":"AI Daily News","enabled":true,"last_run":{"status":"ROUTINE_RUN_STATUS_RUNNING","fired_at":"2026-09-24T21:40:00Z","session_id":"cse_01news","failure_reason":"ROUTINE_RUN_FAILURE_REASON_UNSPECIFIED"}},
          {"id":"trig_2","name":"Monitor","enabled":true,"last_run":{"status":"ROUTINE_RUN_STATUS_FAILED","fired_at":"2026-09-24T18:00:00Z","finished_at":"2026-09-24T18:04:00Z","session_id":"cse_01mon","failure_reason":"ROUTINE_RUN_FAILURE_REASON_TIMEOUT"}},
          {"id":"trig_3","name":"Never ran","enabled":true},
          {"id":"trig_4","name":"Also a session","last_run":{"status":"ROUTINE_RUN_STATUS_SUCCEEDED","fired_at":"2026-09-24T21:00:00Z","finished_at":"2026-09-24T21:02:00Z","session_id":"cse_01done"}}
        ],"has_more":false}
        """;

    [Fact]
    public void The_sessions_list_is_read_leniently()
    {
        var sessions = CloudSessionParser.ParseSessions(SessionsJson).ToDictionary(s => s.Id);
        Assert.Equal(["session_01arch", "session_01done", "session_01old", "session_01wait", "session_01work"], sessions.Keys.Order());

        var work = sessions["session_01work"];
        Assert.Equal(CloudSessionStatus.Working, work.Status);
        Assert.Equal("Raccolta subagenti", work.Title);
        Assert.Equal(SessionOrigin.Cloud, work.Origin);
        Assert.Equal("claude-opus-5-5", work.Model);
        Assert.Equal(new CloudUsage(562, 21246, 2576282, 99384), work.Usage);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T21:44:22Z"), work.LastActivity);

        Assert.Equal(CloudSessionStatus.NeedsInput, sessions["session_01wait"].Status);
        Assert.Equal("Permesso richiesto: Bash", sessions["session_01wait"].Message);
        Assert.Equal(CloudSessionStatus.Idle, sessions["session_01done"].Status);
        Assert.Equal("PR #18 updated", sessions["session_01done"].Message);
        Assert.Equal(CloudSessionStatus.Archived, sessions["session_01arch"].Status);
    }

    [Fact]
    public void The_older_enum_spelling_of_the_status_is_understood()
    {
        var json = """[{"id":"session_x","session_status":"SESSION_STATUS_RUNNING","updated_at":"2026-09-24T21:44:22.410984Z","session_context":{"model":"claude-opus-5-5"}}]""";
        var s = Assert.Single(CloudSessionParser.ParseSessions(json));
        Assert.Equal(CloudSessionStatus.Working, s.Status);
        Assert.Equal("claude-opus-5-5", s.Model);
    }

    [Fact]
    public void Routine_runs_are_read_and_merged_with_the_sessions()
    {
        var runs = CloudSessionParser.ParseRoutineRuns(TriggersJson).ToDictionary(s => s.Id);
        Assert.Equal(3, runs.Count);
        Assert.Equal(CloudSessionStatus.Working, runs["cse_01news"].Status);
        Assert.Equal("AI Daily News", runs["cse_01news"].Title);
        Assert.Equal(SessionOrigin.Routine, runs["cse_01news"].Origin);
        Assert.Equal(CloudSessionStatus.Failed, runs["cse_01mon"].Status);
        Assert.Equal("Routine non riuscita (timeout)", runs["cse_01mon"].Message);
        Assert.Equal(DateTimeOffset.Parse("2026-09-24T18:04:00Z"), runs["cse_01mon"].LastActivity);

        var merged = CloudSessionParser.Merge(CloudSessionParser.ParseSessions(SessionsJson), runs.Values.ToList()).ToDictionary(s => s.Id);
        // "cse_01done" and "session_01done" are the same session: one entry, the richer one, marked as a routine.
        Assert.False(merged.ContainsKey("cse_01done"));
        Assert.Equal(SessionOrigin.Routine, merged["session_01done"].Origin);
        Assert.Equal("Fix null checks", merged["session_01done"].Title);
        Assert.Equal(SessionOrigin.Routine, merged["cse_01news"].Origin);
    }

    [Fact]
    public void The_web_url_uses_the_session_spelling_of_the_id()
    {
        Assert.Equal("https://claude.ai/code/session_01news", CloudSession.WebUrl("cse_01news"));
        Assert.Equal("https://claude.ai/code/session_01E9", CloudSession.WebUrl("session_01E9"));
    }

    private static CloudSession Cloud(string id, CloudSessionStatus status, DateTimeOffset last, string? title = "T",
        SessionOrigin origin = SessionOrigin.Cloud, string? message = null) => new(id, title, status, last, origin, message);

    private static SessionTracker Apply(SessionTracker tracker, IEnumerable<HookEvent> events)
    {
        foreach (var e in events) tracker.Apply(e);
        return tracker;
    }

    [Fact]
    public void The_feed_shows_recent_and_active_sessions_and_follows_their_status()
    {
        var feed = new CloudSessionFeed();
        var tracker = new SessionTracker(new FakeClock(Now));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        Apply(tracker, feed.Diff(
        [
            Cloud("session_a", CloudSessionStatus.Working, Now.AddMinutes(-1), "Raccolta"),
            Cloud("session_b", CloudSessionStatus.Idle, Now.AddMinutes(-5), "Fix", message: "PR aggiornata"),
            Cloud("session_old", CloudSessionStatus.Idle, Now.AddHours(-7)),
            Cloud("session_done", CloudSessionStatus.Idle, Now.AddMinutes(-20)),     // finished: shown only 10 minutes
            Cloud("session_stuck", CloudSessionStatus.Working, Now.AddHours(-25)),
            Cloud("session_arch", CloudSessionStatus.Archived, Now)
        ], true, Now));

        var sessions = tracker.Sessions.ToDictionary(s => s.SessionId);
        Assert.Equal(["session_a", "session_b"], sessions.Keys.Order());
        Assert.Equal("Raccolta", sessions["session_a"].DisplayName);
        Assert.Equal(SessionOrigin.Cloud, sessions["session_a"].Origin);
        Assert.Equal(SessionPhase.Working, sessions["session_a"].Phase);
        Assert.Equal("finito", sessions["session_b"].PhaseLabel);
        Assert.Equal("PR aggiornata", sessions["session_b"].Message);
        Assert.Equal(Now.AddMinutes(-5), sessions["session_b"].LastEventAt);
        Assert.DoesNotContain(changes, c => c.Session.Phase == SessionPhase.Idle && c.PreviousPhase == SessionPhase.Working);

        // Unchanged: nothing to say. Then a turn ends and another waits for the user.
        Assert.Empty(feed.Diff([Cloud("session_a", CloudSessionStatus.Working, Now, "Raccolta"), Cloud("session_b", CloudSessionStatus.Idle, Now.AddMinutes(-5), "Fix")], true, Now));
        Apply(tracker, feed.Diff([Cloud("session_a", CloudSessionStatus.Idle, Now, "Raccolta"), Cloud("session_b", CloudSessionStatus.NeedsInput, Now, "Fix", message: "Permesso richiesto: Bash")], true, Now));
        sessions = tracker.Sessions.ToDictionary(s => s.SessionId);
        Assert.Equal(SessionPhase.Idle, sessions["session_a"].Phase);
        Assert.Equal(SessionPhase.Working, changes.Last(c => c.Session.SessionId == "session_a").PreviousPhase);
        Assert.Equal(SessionPhase.NeedsInput, sessions["session_b"].Phase);
        Assert.Equal("Permesso richiesto: Bash", sessions["session_b"].Message);

        // "session_b" leaves the list: it is ended.
        Apply(tracker, feed.Diff([Cloud("session_a", CloudSessionStatus.Idle, Now, "Raccolta")], true, Now));
        Assert.Equal("session_a", Assert.Single(tracker.Sessions).SessionId);
    }

    [Fact]
    public void A_failed_run_is_an_error_and_routines_that_could_not_be_read_are_kept()
    {
        var feed = new CloudSessionFeed();
        var tracker = new SessionTracker(new FakeClock(Now));
        Apply(tracker, feed.Diff([Cloud("cse_run", CloudSessionStatus.Failed, Now, "Monitor", SessionOrigin.Routine, "Routine non riuscita")], true, Now));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Error, s.Phase);
        Assert.Equal(SessionOrigin.Routine, s.Origin);

        Assert.Empty(feed.Diff([], routinesKnown: false, Now));
        Assert.Single(tracker.Sessions);
        Apply(tracker, feed.Diff([], routinesKnown: true, Now));
        Assert.Empty(tracker.Sessions);
    }

    [Fact]
    public void The_first_title_renames_an_untitled_session_and_the_id_spelling_does_not_split_it()
    {
        var feed = new CloudSessionFeed();
        var tracker = new SessionTracker(new FakeClock(Now));
        Apply(tracker, feed.Diff([Cloud("session_n", CloudSessionStatus.Working, Now, title: null)], true, Now));
        Assert.Equal("session_", tracker.Sessions.Single().DisplayName);   // cwd-less: the first 8 characters of the id

        Apply(tracker, feed.Diff([Cloud("cse_n", CloudSessionStatus.Working, Now, "Nuova funzione")], true, Now));
        var s = Assert.Single(tracker.Sessions);
        Assert.Equal("session_n", s.SessionId);
        Assert.Equal("Nuova funzione", s.DisplayName);
        Assert.Equal(SessionPhase.Working, s.Phase);
    }

    [Fact]
    public void Clear_ends_every_session_on_show()
    {
        var feed = new CloudSessionFeed();
        var tracker = new SessionTracker(new FakeClock(Now));
        Apply(tracker, feed.Diff([Cloud("session_a", CloudSessionStatus.Working, Now)], true, Now));
        Assert.True(feed.HasSessions);
        Apply(tracker, feed.Clear(Now));
        Assert.Empty(tracker.Sessions);
        Assert.False(feed.HasSessions);
    }

    [Fact]
    public void The_token_source_hands_over_the_usage_a_cloud_session_reports()
    {
        using var dir = new TempDir();
        var feed = new CloudSessionFeed();
        feed.Diff([new CloudSession("session_a", "T", CloudSessionStatus.Working, Now, SessionOrigin.Cloud, null, "claude-opus-5-5",
            new CloudUsage(10, 20, 30, 40))], true, Now);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(Now), feed);
        var session = new SessionState(AgentKind.Claude, "session_a", "T", null, SessionPhase.Working, null, Now, Now, Origin: SessionOrigin.Cloud);

        Assert.Equal(new TokenUsage(10, 20, 30, 40), source.SessionTokens(session));
        var entry = Assert.Single(source.SessionLedger(session)!.Entries);
        Assert.Equal("claude-opus-5-5", entry.Key.Model);
        Assert.Equal(new LedgerTokens(10, 20, 30, 0, 40), entry.Tokens);
        Assert.Null(source.SessionTokens(session with { SessionId = "session_unknown" }));
    }

    private static (ClaudeCloudSessionsClient Client, FakeHttpMessageHandler Http) Client(TempDir dir, Func<HttpRequestMessage, HttpResponseMessage> responder,
        long? expiresAt = null, bool organization = true)
    {
        var expires = expiresAt ?? Now.AddHours(1).ToUnixTimeMilliseconds();
        dir.File(Path.Combine(".claude", ".credentials.json"),
            $$$"""{"claudeAiOauth":{"accessToken":"sk-ant-oat-test","expiresAt":{{{expires}}},"subscriptionType":"max"}}""");
        if (organization) dir.File(".claude.json", """{"oauthAccount":{"organizationUuid":"org-123","emailAddress":"x@y"},"projects":{}}""");
        var http = new FakeHttpMessageHandler(responder);
        return (new ClaudeCloudSessionsClient(new AppPaths(dir.Path, dir.Sub("local")), new HttpClient(http), new FakeClock(Now), "AIUsageMonitor/test"), http);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_client_reads_sessions_and_routines_with_the_oauth_token()
    {
        using var dir = new TempDir();
        var (client, http) = Client(dir, request => request.RequestUri == ClaudeCloudSessionsClient.TriggersUri
            ? Json(HttpStatusCode.OK, TriggersJson)
            : Json(HttpStatusCode.OK, SessionsJson));

        var result = await client.FetchAsync();

        Assert.Equal(CloudFetchStatus.Ok, result.Status);
        Assert.True(result.RoutinesKnown);
        Assert.Contains(result.Sessions, s => s.Id == "cse_01news");
        Assert.Equal(2, http.Requests.Count);
        var sessions = http.Requests[0];
        Assert.Equal(ClaudeCloudSessionsClient.SessionsUri, sessions.RequestUri);
        Assert.Equal("Bearer sk-ant-oat-test", sessions.Headers.Authorization!.ToString());
        Assert.Equal("2023-06-01", sessions.Headers.GetValues("anthropic-version").Single());
        Assert.False(sessions.Headers.Contains("anthropic-beta"));
        var triggers = http.Requests[1];
        Assert.Equal(ClaudeCloudSessionsClient.TriggersBeta, triggers.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("org-123", triggers.Headers.GetValues("x-organization-uuid").Single());
    }

    [Fact]
    public async Task Without_an_organization_or_with_a_failing_routines_call_only_the_sessions_are_listed()
    {
        using (var dir = new TempDir())
        {
            var (client, http) = Client(dir, _ => Json(HttpStatusCode.OK, SessionsJson), organization: false);
            var result = await client.FetchAsync();
            Assert.Equal(CloudFetchStatus.Ok, result.Status);
            Assert.False(result.RoutinesKnown);
            Assert.Single(http.Requests);
        }
        using (var dir = new TempDir())
        {
            var (client, _) = Client(dir, request => request.RequestUri == ClaudeCloudSessionsClient.TriggersUri
                ? Json(HttpStatusCode.InternalServerError, "{}")
                : Json(HttpStatusCode.OK, SessionsJson));
            var result = await client.FetchAsync();
            Assert.Equal(CloudFetchStatus.Ok, result.Status);
            Assert.False(result.RoutinesKnown);
            Assert.Contains("HTTP 500", result.Detail);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CloudFetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CloudFetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, CloudFetchStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CloudFetchStatus.Error)]
    public async Task Http_failures_are_classified(HttpStatusCode status, CloudFetchStatus expected)
    {
        using var dir = new TempDir();
        var (client, _) = Client(dir, _ => Json(status, "{}"));
        var result = await client.FetchAsync();
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public async Task No_request_is_made_without_a_valid_token()
    {
        using var dir = new TempDir();
        var (client, http) = Client(dir, _ => Json(HttpStatusCode.OK, SessionsJson), expiresAt: Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        Assert.Equal(CloudFetchStatus.NoCredentials, (await client.FetchAsync()).Status);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task The_poller_applies_the_first_read_silently_then_toasts_and_clears_when_switched_off()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(Now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock);
        pump.Start();

        IReadOnlyList<CloudSession> next = [Cloud("session_a", CloudSessionStatus.NeedsInput, Now, "Deploy")];
        TimeSpan? interval = TimeSpan.FromSeconds(60);
        var applied = 0;
        var poller = new CloudSessionPoller(_ => Task.FromResult(new CloudFetchResult(CloudFetchStatus.Ok, next, true)),
            new CloudSessionFeed(), pump, clock, () => interval);
        poller.Applied += () => applied++;

        Assert.Equal(TimeSpan.FromSeconds(60), await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(SessionPhase.NeedsInput, tracker.Sessions.Single().Phase);
        Assert.Empty(changes);                                     // already waiting when the app started: no toast
        Assert.Equal(1, applied);

        next = [Cloud("session_a", CloudSessionStatus.Working, Now, "Deploy")];
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(SessionPhase.Working, Assert.Single(changes).Session.Phase);

        interval = null;
        Assert.Equal(poller.OffCheckEvery, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Empty(tracker.Sessions);
        Assert.Equal(SessionChangeKind.Removed, changes.Last().Kind);
    }

    [Fact]
    public async Task The_poller_backs_off_after_a_rejection()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(Now);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), new SessionTracker(clock), paths, clock);
        var logged = new List<string>();
        var poller = new CloudSessionPoller(_ => Task.FromResult(CloudFetchResult.Failed(CloudFetchStatus.Unauthorized, "HTTP 401")),
            new CloudSessionFeed(), pump, clock, () => TimeSpan.FromSeconds(60)) { OnInfo = logged.Add };

        Assert.Equal(poller.BackoffAfterRejection, await poller.PollOnceAsync(CancellationToken.None));
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal("Sessioni cloud: Unauthorized (HTTP 401)", Assert.Single(logged));
    }
}
