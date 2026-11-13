using System.Text.Json;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Interlock;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07 step 4: the C# approval targets, consent surfaces and approval queue over tests/TestVectors/relay-approval.json.
/// The legacy Kotlin LegacyRelayApprovalParityTest drives the real upstream InterlockTargets / ConsentSurfaces /
/// InterlockQueue over the same vectors.
/// </summary>
[Collection(nameof(ConsentSurfaces))]
public sealed class RelayApprovalParityTests
{
    private static readonly JsonElement V = Repo.Vectors("relay-approval.json");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static string CharCount(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "c";

    [Fact]
    public void Targets_MatchSharedVectors()
    {
        var n = 0;
        foreach (var c in V.GetProperty("targets").EnumerateArray())
        {
            var (target, detail) = InterlockTargets.Of(c.GetProperty("capability").GetString()!, c.GetProperty("args"), CharCount);
            var name = c.GetProperty("name").GetString();
            Assert.True(Str(c, "target") == target, $"{name}: target '{target}'");
            Assert.True(Str(c, "detail") == detail, $"{name}: detail '{detail}'");
            n++;
        }
        Assert.True(n > 40);
        foreach (var c in V.GetProperty("volatile").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("expect").GetBoolean(), InterlockTargets.IsVolatileTarget(c.GetProperty("capability").GetString()!, c.GetProperty("args")));
        }
        foreach (var c in V.GetProperty("volatileFor").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("expect").GetBoolean(),
                InterlockTargets.VolatileFor(c.GetProperty("capability").GetString()!, c.GetProperty("hasTarget").GetBoolean()));
        }
    }

    [Fact]
    public void ConsentSurfaces_MatchSharedVectors()
    {
        var consent = V.GetProperty("consent");
        var uids = consent.GetProperty("uids").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var throwing = consent.GetProperty("throwingUid").GetString();
        var saved = ConsentSurfaces.UidResolver;
        ConsentSurfaces.UidResolver = pkg => pkg == throwing ? throw new InvalidOperationException("scripted") : uids.TryGetValue(pkg, out var u) ? u : null;
        try
        {
            foreach (var c in consent.GetProperty("systemOwned").EnumerateArray())
            {
                Assert.True(c.GetProperty("expect").GetBoolean() == ConsentSurfaces.IsSystemOwned(Str(c, "pkg")), c.GetRawText());
            }
            foreach (var c in consent.GetProperty("consentPackage").EnumerateArray())
            {
                Assert.True(c.GetProperty("expect").GetBoolean() == ConsentSurfaces.IsConsentPackage(Str(c, "pkg")), c.GetRawText());
            }
            foreach (var c in consent.GetProperty("control").EnumerateArray())
            {
                Assert.True(c.GetProperty("expect").GetBoolean() == ConsentSurfaces.LooksLikeConsentControl(Str(c, "pkg"), Str(c, "viewId")), c.GetRawText());
            }
            foreach (var c in consent.GetProperty("fromArgs").EnumerateArray())
            {
                Assert.True(c.GetProperty("expect").GetBoolean() == ConsentSurfaces.LooksLikeConsentFromArgs(c.GetProperty("args")), c.GetRawText());
            }
            foreach (var c in consent.GetProperty("node").EnumerateArray())
            {
                Assert.True(c.GetProperty("expect").GetBoolean() == ConsentSurfaces.LooksLikeConsentNode(Str(c, "pkg"), Str(c, "viewId")), c.GetRawText());
            }
        }
        finally
        {
            ConsentSurfaces.UidResolver = saved;
        }
    }

    private static readonly Dictionary<string, InterlockChoice> Choices =
        Enum.GetValues<InterlockChoice>().ToDictionary(c => c.Wire(), c => c);

    private static string DeadEndWire(ApprovalDeadEnd d) => d switch
    {
        ApprovalDeadEnd.NotificationsBlocked => "NOTIFICATIONS_BLOCKED",
        ApprovalDeadEnd.BackgroundOverlaySkipped => "BACKGROUND_OVERLAY_SKIPPED",
        _ => "QUESTION_CARD_ON_SCREEN",
    };

    private static readonly Dictionary<string, ApprovalDeadEnd> DeadEnds =
        Enum.GetValues<ApprovalDeadEnd>().ToDictionary(DeadEndWire, d => d);

    private static ApprovalPrompt Prompt(JsonElement p) => new(
        p.GetProperty("cap").GetString()!, p.GetProperty("args").GetString()!, p.GetProperty("volatile").GetBoolean(),
        p.TryGetProperty("label", out var l) ? l.GetString() : null, RiskLevel.Low, 1, null, p.GetProperty("session").GetBoolean(),
        p.GetProperty("deadline").GetInt64());

    [Fact]
    public void ChoicesPromptsAndReasons_MatchSharedVectors()
    {
        var noSession = new ApprovalPrompt("ui.node", "a", false, null, RiskLevel.Low, 1, null, false, 0);
        var session = noSession with { AllowsSessionGrant = true };
        Assert.Equal(Choices.Count, V.GetProperty("choices").GetArrayLength());
        foreach (var c in V.GetProperty("choices").EnumerateArray())
        {
            var choice = Choices[c.GetProperty("choice").GetString()!];
            Assert.Equal(c.GetProperty("isAnswer").GetBoolean(), choice.IsAnswer());
            Assert.Equal(c.GetProperty("downgradedNoSession").GetString(), choice.DowngradedFor(noSession).Wire());
            Assert.Equal(c.GetProperty("downgradedSession").GetString(), choice.DowngradedFor(session).Wire());
        }
        foreach (var c in V.GetProperty("sameQuestion").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("expect").GetBoolean(), Prompt(c.GetProperty("a")).SameQuestionAs(Prompt(c.GetProperty("b"))));
        }
        foreach (var c in V.GetProperty("terminates").EnumerateArray())
        {
            var p = noSession with { VolatileTarget = c.GetProperty("volatile").GetBoolean(), AllowsSessionGrant = c.GetProperty("session").GetBoolean() };
            Assert.Equal(c.GetProperty("expect").GetBoolean(), p.TerminatesWhenAnswerDropped);
        }
        foreach (var c in V.GetProperty("reasons").EnumerateArray())
        {
            var set = c.GetProperty("deadEnds").EnumerateArray().Select(d => DeadEnds[d.GetString()!]).ToHashSet();
            Assert.Equal(c.GetProperty("reason").GetString(), InterlockQueue.ReasonFor(set));
        }
    }

    private sealed class RecordingSurface : IApprovalNotificationSurface
    {
        public readonly List<string> Calls = [];

        public bool CanShow() => true;

        public void Show(InterlockQueue.Request request, int waitingCount) => Calls.Add($"show {request.Id} {waitingCount}");

        public void Cancel() => Calls.Add("cancel");

        public void CancelFor(int requestId) => Calls.Add($"cancelFor {requestId}");

        public string LastPostState() => ApprovalPostStates.None;
    }

    /// <summary>C#-only: this app's notification card (mirrors native-bridge RelayApproval.kt, which needs a Context).</summary>
    [Fact]
    public void NotificationContent_MirrorsLegacyCard()
    {
        Assert.Equal("DSH 请求执行 ui.click", ApprovalNotificationContent.Text("ui.click", 0));
        Assert.Equal("DSH 请求执行 sys.shell（另有 2 个请求排队）", ApprovalNotificationContent.Text("sys.shell", 2));
        Assert.Equal([InterlockChoice.AllowOnce, InterlockChoice.AllowSession, InterlockChoice.Deny], ApprovalNotificationContent.Actions);
        // Kotlin: requestId * 8 + choice.ordinal (ALLOW_ONCE 0, ALLOW_SESSION 1, DENY 2)
        Assert.Equal(40, ApprovalNotificationContent.RequestCode(5, InterlockChoice.AllowOnce));
        Assert.Equal(41, ApprovalNotificationContent.RequestCode(5, InterlockChoice.AllowSession));
        Assert.Equal(42, ApprovalNotificationContent.RequestCode(5, InterlockChoice.Deny));
        var codes = Enumerable.Range(1, 50).SelectMany(id => ApprovalNotificationContent.Actions.Select(c => ApprovalNotificationContent.RequestCode(id, c))).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());

        Assert.Equal(InterlockChoice.AllowOnce, ApprovalNotificationContent.ParseReceived(3, "ALLOW_ONCE"));
        Assert.Equal(InterlockChoice.AllowSession, ApprovalNotificationContent.ParseReceived(0, "ALLOW_SESSION"));
        Assert.Equal(InterlockChoice.Deny, ApprovalNotificationContent.ParseReceived(7, "DENY"));
        foreach (var bad in new[] { "UNABLE_TO_SHOW", "EXPIRED", "AWAITING", "WAITING_TURN", "allow_once", "", null })
        {
            Assert.Null(ApprovalNotificationContent.ParseReceived(3, bad));
        }
        Assert.Null(ApprovalNotificationContent.ParseReceived(-1, "ALLOW_ONCE"));

        // routing → surface: only NOTIFICATION posts; every other route (and an empty queue) removes the card
        var env = new Env { CanNotify = true, Clock = 1000 };
        var surface = new RecordingSurface();
        var queue = new InterlockQueue(() => env.Foreground, p => ApprovalNotificationContent.ApplyRouting(p, surface),
            canNotify: () => env.CanNotify, onRevokePresenters: r => surface.CancelFor(r.Id), now: () => env.Clock, inlineWaitMs: 0, idSeed: 0);
        var a = new ApprovalPrompt("contact.read", "a", false, null, RiskLevel.Low, 1, null, true, 100_000);
        var b = a with { ArgsIdentity = "b" };
        Assert.Equal(InterlockChoice.Awaiting, queue.PresentAsync(a, CancellationToken.None).Result);
        Assert.Equal(InterlockChoice.Awaiting, queue.PresentAsync(b, CancellationToken.None).Result);
        queue.Snapshot();
        env.Foreground = true;
        queue.Reroute();
        env.Foreground = false;
        queue.Reroute();
        Assert.True(queue.ResolveCurrent(InterlockChoice.Deny, 1));
        queue.CancelAll();
        Assert.Equal(["show 1 0", "show 1 1", "cancel", "show 1 1", "cancelFor 1", "show 2 0", "cancelFor 2", "cancel"], surface.Calls);
        ApprovalNotificationContent.ApplyRouting(new InterlockQueue.Presentation(null, InterlockChannel.None, 0, new HashSet<ApprovalDeadEnd>()), null);
    }

    private sealed class Env
    {
        public bool Foreground;
        public bool CanNotify;
        public bool Blind;
        public bool OtherCard;
        public bool OverlayCanShow;
        public bool OverlayPresented;
        public long Clock;
    }

    private sealed class FakeOverlay(Env env) : IOverlayApprovalChannel
    {
        public readonly Queue<InterlockChoice> Script = new();
        public int Withdrawn;

        public bool CanShow() => env.OverlayCanShow;

        public Task<InterlockChoice> PresentAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            var choice = Script.Count > 0 ? Script.Dequeue() : InterlockChoice.UnableToShow;
            env.OverlayPresented = choice == InterlockChoice.Awaiting;
            return Task.FromResult(choice);
        }

        public bool IsPresented() => env.OverlayPresented;

        public void Withdraw()
        {
            Withdrawn++;
            env.OverlayPresented = false;
        }

        public Action<ApprovalPrompt, InterlockChoice>? OnParkedChoice { get; set; }

        public Action? OnHidden { get; set; }
    }

    private static string RoutingText(InterlockQueue.Presentation p) =>
        JsonSerializer.Serialize(new object?[]
        {
            p.Request?.Id, p.Via.ToString().ToLowerInvariant(), p.WaitingCount,
            p.DeadEnds.Select(DeadEndWire).Order(StringComparer.Ordinal).ToArray(),
        });

    [Fact]
    public void Queue_MatchesSharedScenarios()
    {
        var scenarios = 0;
        foreach (var sc in V.GetProperty("queue").EnumerateArray())
        {
            RunScenario(sc);
            scenarios++;
        }
        Assert.Equal(5, scenarios);
    }

    private static void RunScenario(JsonElement sc)
    {
        var name = sc.GetProperty("name").GetString();
        var envJson = sc.GetProperty("env");
        bool Flag(string k) => envJson.TryGetProperty(k, out var b) && b.GetBoolean();
        var env = new Env
        {
            Foreground = Flag("foreground"), CanNotify = Flag("canNotify"), OverlayCanShow = Flag("overlayCanShow"),
            Clock = sc.GetProperty("clock").GetInt64(),
        };
        var overlay = Flag("overlay") ? new FakeOverlay(env) : null;
        var routing = new List<string>();
        var revoked = new List<int>();
        var queue = new InterlockQueue(
            isForegroundCapable: () => env.Foreground,
            onRouting: p => routing.Add(RoutingText(p)),
            overlay: overlay,
            overlayBlind: () => env.Blind,
            canNotify: () => env.CanNotify,
            otherCardOnScreen: () => env.OtherCard,
            onRevokePresenters: r => revoked.Add(r.Id),
            now: () => env.Clock,
            inlineWaitMs: sc.GetProperty("inlineWaitMs").GetInt64(),
            idSeed: 0);
        var prompts = sc.GetProperty("prompts").EnumerateObject().ToDictionary(p => p.Name, p => Prompt(p.Value));
        var slots = new Dictionary<string, Task<InterlockChoice>>();
        var i = 0;
        foreach (var st in sc.GetProperty("steps").EnumerateArray())
        {
            var where = $"{name} step {i++} {st.GetProperty("op").GetString()}";
            routing.Clear();
            revoked.Clear();
            if (overlay is not null)
            {
                overlay.Withdrawn = 0;
            }
            object? result = null;
            switch (st.GetProperty("op").GetString())
            {
                case "present":
                {
                    var task = queue.PresentAsync(prompts[st.GetProperty("q").GetString()!], CancellationToken.None);
                    Assert.True(task.Wait(TimeSpan.FromSeconds(5)), where + ": present did not complete");
                    result = task.Result.Wire();
                    break;
                }
                case "start":
                    slots[st.GetProperty("slot").GetString()!] = queue.PresentAsync(prompts[st.GetProperty("q").GetString()!], CancellationToken.None);
                    break;
                case "await":
                {
                    var task = slots[st.GetProperty("slot").GetString()!];
                    Assert.True(task.Wait(TimeSpan.FromSeconds(10)), where + ": await did not complete");
                    result = task.Result.Wire();
                    break;
                }
                case "resolve":
                {
                    var id = st.GetProperty("id");
                    result = queue.ResolveCurrent(Choices[st.GetProperty("choice").GetString()!],
                        id.ValueKind == JsonValueKind.Null ? InterlockQueue.AnyRequest : id.GetInt32());
                    break;
                }
                case "expire":
                    result = queue.ExpireIfCurrent(st.GetProperty("id").GetInt32());
                    break;
                case "parked":
                    Assert.NotNull(overlay);
                    overlay.OnParkedChoice!(prompts[st.GetProperty("q").GetString()!], Choices[st.GetProperty("choice").GetString()!]);
                    break;
                case "hidden":
                    Assert.NotNull(overlay);
                    overlay.OnHidden!();
                    break;
                case "reroute":
                    queue.Reroute();
                    break;
                case "snapshot":
                {
                    var snap = queue.Snapshot();
                    var want = st.GetProperty("snapshot");
                    var displayed = want.GetProperty("displayed");
                    Assert.True((displayed.ValueKind == JsonValueKind.Null ? null : (int?)displayed.GetInt32()) == snap.Displayed?.Id, where + ": displayed");
                    Assert.True(want.GetProperty("waiting").GetInt32() == snap.WaitingCount, where + ": waiting");
                    break;
                }
                case "cancelAll":
                    queue.CancelAll();
                    break;
                case "clock":
                    env.Clock += st.GetProperty("advance").GetInt64();
                    break;
                case "overlayScript":
                    Assert.NotNull(overlay);
                    foreach (var r in st.GetProperty("results").EnumerateArray())
                    {
                        overlay.Script.Enqueue(Choices[r.GetString()!]);
                    }
                    break;
                case "env":
                    foreach (var p in st.GetProperty("set").EnumerateObject())
                    {
                        var b = p.Value.GetBoolean();
                        switch (p.Name)
                        {
                            case "foreground": env.Foreground = b; break;
                            case "canNotify": env.CanNotify = b; break;
                            case "overlayBlind": env.Blind = b; break;
                            case "otherCard": env.OtherCard = b; break;
                            case "overlayCanShow": env.OverlayCanShow = b; break;
                            default: throw new InvalidOperationException(p.Name);
                        }
                    }
                    break;
                default:
                    throw new InvalidOperationException(where);
            }
            if (st.TryGetProperty("result", out var want2))
            {
                var expected = want2.ValueKind switch
                {
                    JsonValueKind.True => (object)true,
                    JsonValueKind.False => false,
                    _ => want2.GetString(),
                };
                Assert.True(Equals(expected, result), $"{where}: result {result} (want {expected})");
            }
            var wantRouting = st.GetProperty("routing").EnumerateArray().Select(r => JsonSerializer.Serialize(r)).ToList();
            Assert.True(wantRouting.SequenceEqual(routing), $"{where}: routing [{string.Join(", ", routing)}] want [{string.Join(", ", wantRouting)}]");
            Assert.True(st.GetProperty("revoked").EnumerateArray().Select(r => r.GetInt32()).SequenceEqual(revoked), $"{where}: revoked [{string.Join(",", revoked)}]");
            Assert.True(st.GetProperty("withdrawn").GetInt32() == (overlay?.Withdrawn ?? 0), $"{where}: withdrawn {overlay?.Withdrawn}");
            if (st.TryGetProperty("reason", out var reason))
            {
                Assert.True(reason.ValueKind == JsonValueKind.Null ? queue.LastDeadEndReason() is null : reason.GetString() == queue.LastDeadEndReason(),
                    $"{where}: reason '{queue.LastDeadEndReason()}'");
            }
        }
        Assert.All(slots.Values, t => Assert.True(t.IsCompleted, name + ": a started present never finished"));
    }

    [Fact]
    public void ApprovalTokens_AnswerOnceWithTheRightToken()
    {
        var tokens = new ApprovalTokens();
        var t1 = tokens.Issue(1);
        Assert.Matches("^[0-9a-f]{32}$", t1);
        Assert.Equal(t1, tokens.Issue(1));
        Assert.NotEqual(t1, tokens.Issue(2));
        Assert.False(tokens.Consume(1, null));
        Assert.False(tokens.Consume(1, ""));
        Assert.False(tokens.Consume(1, tokens.Issue(2)));
        Assert.False(tokens.Consume(1, t1.ToUpperInvariant()));
        Assert.False(tokens.Consume(3, t1));
        Assert.True(tokens.Consume(1, t1));
        Assert.False(tokens.Consume(1, t1));
        var t3 = tokens.Issue(3);
        tokens.Revoke(3);
        Assert.False(tokens.Consume(3, t3));
        var t2 = tokens.Issue(2);
        tokens.Clear();
        Assert.False(tokens.Consume(2, t2));
        Assert.NotEqual(t1, tokens.Issue(1));
    }

    [Fact]
    public void ConfirmPage_OnlyForAllowsAndOnlyToKnownReceivers()
    {
        Assert.True(ApprovalNotificationContent.NeedsConfirmation(InterlockChoice.AllowOnce));
        Assert.True(ApprovalNotificationContent.NeedsConfirmation(InterlockChoice.AllowSession));
        Assert.False(ApprovalNotificationContent.NeedsConfirmation(InterlockChoice.Deny));
        Assert.Equal(("dshmobile.nativebridge.RelayApprovalReceiver", "dshmobile.nativebridge.RELAY_APPROVAL"),
            ApprovalNotificationContent.ConfirmTarget("legacy"));
        Assert.Equal(("dshmobile.app.RelayApprovalReceiver", "dshmobile.app.RELAY_APPROVAL"), ApprovalNotificationContent.ConfirmTarget("csharp"));
        foreach (var bad in new[] { null, "", "Legacy", "dshmobile.app.RelayApprovalReceiver", "csharp " })
        {
            Assert.Null(ApprovalNotificationContent.ConfirmTarget(bad));
        }
        Assert.Null(ApprovalNotificationContent.ParseReceived(1, "UNABLE_TO_SHOW"));
        Assert.Null(ApprovalNotificationContent.ParseReceived(-1, "ALLOW_ONCE"));
        Assert.Equal(InterlockChoice.AllowSession, ApprovalNotificationContent.ParseReceived(0, "ALLOW_SESSION"));
    }
}
