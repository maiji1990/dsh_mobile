using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Android.Content;
using Android.Text;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Application.Shadow;
using DshMobile.Core.Bridge;
using DshMobile.Runtime.Remote;

namespace DshMobile.App.Remote;

/// <summary>
/// Remote DSH host over SSH (TASK-012, ADR-0006). Shows the phone's SSH public key (private key stays in Android
/// Keystore), edits one host profile, connects with explicit host-key confirmation, starts/stops the remote
/// <c>dsh web</c> and reports what the remote really offers. Remote never gets this phone's ui_* / browser_*.
/// </summary>
[Activity(Label = "@string/remote_title", Exported = false)]
public sealed class RemoteActivity : Activity
{
    private BridgeHost _bridge = null!;
    private TextView _identity = null!;
    private TextView _status = null!;
    private EditText _label = null!, _host = null!, _port = null!, _user = null!, _dshPort = null!;
    private Button[] _buttons = [];
    private RemoteHost? _selected;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bridge = DshMobileApplication.Current.Bridge;

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 16, 24, 16);
        root.AddView(new TextView(this) { Text = GetString(Resource.String.remote_explain) });

        _identity = new TextView(this) { TextSize = 12 };
        _identity.SetTextIsSelectable(true);
        _identity.SetPadding(0, 12, 0, 12);
        root.AddView(_identity);
        var copy = Make(Resource.String.remote_copy_key, CopyKey);

        _label = Field(root, Resource.String.remote_label, InputTypes.ClassText);
        _host = Field(root, Resource.String.remote_host, InputTypes.ClassText | InputTypes.TextVariationUri);
        _port = Field(root, Resource.String.remote_port, InputTypes.ClassNumber);
        _user = Field(root, Resource.String.remote_user, InputTypes.ClassText);
        _dshPort = Field(root, Resource.String.remote_dsh_port, InputTypes.ClassNumber);
        _port.Text = "22";
        _dshPort.Text = "3091";

        var save = Make(Resource.String.remote_save, () => _ = SaveAsync());
        var connect = Make(Resource.String.remote_connect, () => _ = ConnectAsync());
        // PR-09 second segment (ADR-0012): long-press = opt-in C# (SSH.NET) connect; short press stays legacy.
        connect.LongClick += (_, _) => _ = CsConnectAsync();
        var start = Make(Resource.String.remote_start, StartDsh);
        var stop = Make(Resource.String.remote_stop, StopDsh);
        var disconnect = Make(Resource.String.remote_disconnect, () =>
        {
            CsRemoteConnection.Clear();
            _ = Status(BridgeMethods.RemoteDisconnect, null, TimeSpan.FromSeconds(30));
        });
        var remove = Make(Resource.String.remote_remove, ConfirmRemove);
        root.AddView(Row(copy, save));
        root.AddView(Row(connect, disconnect));
        root.AddView(Row(start, stop));
        root.AddView(Row(remove, new View(this)));
        _buttons = [copy, save, connect, start, stop, disconnect, remove];

        _status = new TextView(this) { AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        _status.SetTextIsSelectable(true);
        _status.SetPadding(0, 16, 0, 16);
        root.AddView(_status);

        var scroll = new ScrollView(this);
        scroll.SetFitsSystemWindows(true);
        scroll.AddView(root);
        SetContentView(scroll);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var id = await Call(BridgeMethods.RemoteIdentity, null, BridgeJsonContext.Default.RemoteIdentityInfo, TimeSpan.FromSeconds(30));
        if (id is not null)
        {
            var hw = id.HardwareBacked switch { true => GetString(Resource.String.remote_key_hw), false => GetString(Resource.String.remote_key_sw), null => "?" };
            _identity.Text = $"{GetString(Resource.String.remote_key_heading)}（{hw}）\n{id.PublicKey}\n{id.Fingerprint}";
            // PR-09 shadow (read-only): C# reads the same Keystore key and signs one labelled challenge inside Keystore.
            var shadow = _bridge.RemoteShadow;
            _ = Task.Run(() => shadow.Identity(id, () => SshIdentity.Probe(new AndroidSshKeySource())));
        }
        var list = await Call(BridgeMethods.RemoteHostsList, null, BridgeJsonContext.Default.RemoteHostList, TimeSpan.FromSeconds(10));
        if (list is not null)
        {
            Shadow(s => s.Listed(list.Hosts, StoredHosts()));
        }
        if (list is { Hosts.Length: > 0 })
        {
            Select(list.Hosts[^1]);
        }
        await Status(BridgeMethods.RemoteStatus, null, TimeSpan.FromSeconds(10));
        if (CsRemoteConnection.Status() is { } cs)
        {
            ShowCs(cs);
        }
    }

    private void Select(RemoteHost h)
    {
        _selected = h;
        _label.Text = h.Label;
        _host.Text = h.Host;
        _port.Text = h.Port.ToString();
        _user.Text = h.User;
        _dshPort.Text = h.DshPort.ToString();
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(_port.Text, out var port) || !int.TryParse(_dshPort.Text, out var dshPort))
        {
            _status.Text = GetString(Resource.String.remote_bad_port);
            return;
        }
        var req = new RemoteHostRequest(_selected?.Id, _label.Text ?? "", (_host.Text ?? "").Trim(), port, (_user.Text ?? "").Trim(), dshPort);
        var payload = BridgeCodec.ToPayload(req, BridgeJsonContext.Default.RemoteHostRequest);
        var before = StoredHosts();
        var (resp, saved) = await CallRaw(BridgeMethods.RemoteHostsSave, payload, BridgeJsonContext.Default.RemoteHost, TimeSpan.FromSeconds(10));
        if (resp is not null)
        {
            var after = StoredHosts();
            Shadow(s => s.Saved(payload, before, new RemoteLegacyOutcome<RemoteHost>(resp.Ok, resp.Code, resp.Message, saved), after));
        }
        if (saved is not null)
        {
            Select(saved);
            _status.Text = GetString(saved.PinnedFingerprint is null ? Resource.String.remote_saved_unpinned : Resource.String.remote_saved_pinned);
        }
    }

    private async Task ConnectAsync()
    {
        if (_selected is null)
        {
            _status.Text = GetString(Resource.String.remote_save_first);
            return;
        }
        CsRemoteConnection.Clear();
        var hostId = _selected.Id;
        // The profile (and its pin) the legacy connect is about to use: the C# shadow connection uses the same one.
        var profile = RemoteHostProfile.ParseStored(StoredHosts()).FirstOrDefault(p => p.Id == hostId);
        var s = await Status(BridgeMethods.RemoteConnect, HostId(hostId), TimeSpan.FromMinutes(3));
        if (s is not null)
        {
            var shadow = _bridge.RemoteShadow;
            if (shadow.ConnectEnabled)
            {
                var connector = CsRemoteConnection.Connector();
                _ = Task.Run(() => shadow.ConnectedAsync(s, profile, (p, ct) => connector.ConnectAsync(p, ct)));
            }
        }
        if (s is { Code: RemoteCodes.HostKeyUnknown, Fingerprint: { } fp })
        {
            ConfirmFingerprint(_selected, fp, s.KeyType);
        }
    }

    /// <summary>
    /// Opt-in C# connect (ADR-0012): the legacy connection is closed first (one remote at a time), then SSH.NET connects
    /// to the stored profile with the same pin and Keystore key and runs the same probe. C# never pins: an unknown key is
    /// only shown, and confirming it stays with the legacy flow (short press).
    /// </summary>
    private async Task CsConnectAsync()
    {
        if (_selected is null)
        {
            _status.Text = GetString(Resource.String.remote_save_first);
            return;
        }
        var hostId = _selected.Id;
        await Call(BridgeMethods.RemoteDisconnect, null, BridgeJsonContext.Default.RemoteStatusInfo, TimeSpan.FromSeconds(30));
        CsRemoteConnection.Clear();
        var profile = RemoteHostProfile.ParseStored(StoredHosts()).FirstOrDefault(p => p.Id == hostId);
        if (profile is null)
        {
            _status.Text = GetString(Resource.String.remote_save_first);
            return;
        }
        _status.Text = GetString(Resource.String.remote_cs_connecting);
        Enable(false);
        try
        {
            var connector = CsRemoteConnection.Connector();
            var attempt = await Task.Run(() => connector.ConnectAsync(profile));
            CsRemoteConnection.Set(profile, attempt);
            _bridge.Note($"remote C# connect: state={attempt.State} code={attempt.Code}");
            if (CsRemoteConnection.Status() is { } st)
            {
                ShowCs(st);
            }
        }
        catch (Exception e)
        {
            _status.Text = Fmt(Resource.String.remote_cs_failed, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Enable(true);
        }
    }

    /// <summary>Start of the remote DSH: on the C# session when one is open (ADR-0013), otherwise legacy + shadow check.</summary>
    private void StartDsh()
    {
        if (CsRemoteConnection.Session is { } cs)
        {
            _ = CsDshAsync(cs, start: true);
            return;
        }
        _ = LegacyDshAsync(BridgeMethods.RemoteStart, TimeSpan.FromMinutes(3));
    }

    private void StopDsh()
    {
        if (CsRemoteConnection.Session is { } cs)
        {
            _ = CsDshAsync(cs, start: false);
            return;
        }
        _ = LegacyDshAsync(BridgeMethods.RemoteStop, TimeSpan.FromMinutes(1));
    }

    private async Task CsDshAsync(RemoteSshSession session, bool start)
    {
        Enable(false);
        try
        {
            var d = await Task.Run(() => start ? session.StartDshAsync() : session.StopDshAsync());
            _bridge.Note($"remote C# {(start ? "start" : "stop")}: state={d.State} code={d.Code} http={d.HttpUnauthenticated?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} still={d.RemoteDshStillRunning?.ToString() ?? "-"} token={d.HasLaunchToken}");
            if (CsRemoteConnection.Status(d) is { } st)
            {
                ShowCs(st);
            }
        }
        catch (Exception e)
        {
            _status.Text = Fmt(Resource.String.remote_cs_failed, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Enable(true);
        }
    }

    /// <summary>
    /// Legacy start / stop; then the PR-09 shadow (ADR-0013) checks the verdict through its own C# connection and forward
    /// to the same remote port — read-only, it never starts or stops a remote process.
    /// </summary>
    private async Task LegacyDshAsync(string method, TimeSpan deadline)
    {
        var hostId = _selected?.Id;
        var profile = hostId is null ? null : RemoteHostProfile.ParseStored(StoredHosts()).FirstOrDefault(p => p.Id == hostId);
        var s = await Status(method, null, deadline);
        var shadow = _bridge.RemoteShadow;
        if (s is null || !shadow.DshEnabled)
        {
            return;
        }
        var connector = CsRemoteConnection.Connector();
        _ = Task.Run(() => shadow.DshObservedAsync(method, s, profile, async (p, ct) =>
        {
            var a = await connector.ConnectSessionAsync(p, ct);
            using var session = a.Session ?? throw new InvalidOperationException($"C# connect {a.Code}: {a.Message}");
            return await session.ProbeHttpAsync(p.DshPort, ct);
        }));
    }

    private void ShowCs(RemoteStatusInfo s)
    {
        Show(s);
        var extra = GetString(Resource.String.remote_cs_marker);
        if (s.Code == RemoteCodes.HostKeyUnknown && s.Fingerprint is { } fp)
        {
            extra += $"\n{s.KeyType ?? "?"} {fp}\n{GetString(Resource.String.remote_cs_unknown_hint)}";
        }
        _status.Text = extra + "\n" + _status.Text;
    }

    /// <summary>The user compares the fingerprint with the server's own (ssh-keygen -lf) before trusting it.</summary>
    private void ConfirmFingerprint(RemoteHost host, string fingerprint, string? keyType)
    {
        new AlertDialog.Builder(this)
            .SetTitle(Resource.String.remote_trust_title)!
            .SetMessage(Fmt(Resource.String.remote_trust_message, $"{host.User}@{host.Host}:{host.Port}", keyType ?? "?", fingerprint))!
            .SetPositiveButton(Resource.String.remote_trust_yes, async (_, _) =>
            {
                var before = StoredHosts();
                var (resp, trusted) = await CallRaw(BridgeMethods.RemoteTrust,
                    BridgeCodec.ToPayload(new RemoteTrustRequest(host.Id, fingerprint), BridgeJsonContext.Default.RemoteTrustRequest),
                    BridgeJsonContext.Default.RemoteHostList, TimeSpan.FromSeconds(10));
                if (resp is not null)
                {
                    var after = StoredHosts();
                    Shadow(s => s.Trusted(host.Id, fingerprint, before, new RemoteLegacyOutcome<RemoteHostList>(resp.Ok, resp.Code, resp.Message, trusted), after));
                }
                if (trusted?.Hosts.FirstOrDefault(h => h.Id == host.Id) is { } h)
                {
                    Select(h);
                    await ConnectAsync();
                }
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void ConfirmRemove()
    {
        if (_selected is not { } h)
        {
            return;
        }
        new AlertDialog.Builder(this)
            .SetMessage(Fmt(Resource.String.remote_remove_confirm, h.Label))!
            .SetPositiveButton(Resource.String.remote_remove, async (_, _) =>
            {
                CsRemoteConnection.Clear(); // legacy remove also disconnects the removed host
                var before = StoredHosts();
                var (resp, left) = await CallRaw(BridgeMethods.RemoteHostsRemove, HostId(h.Id), BridgeJsonContext.Default.RemoteHostList, TimeSpan.FromSeconds(30));
                if (resp is not null)
                {
                    var after = StoredHosts();
                    Shadow(s => s.Removed(h.Id, before, new RemoteLegacyOutcome<RemoteHostList>(resp.Ok, resp.Code, resp.Message, left), after));
                }
                _selected = null;
                _status.Text = GetString(Resource.String.remote_removed);
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void CopyKey()
    {
        var text = _identity.Text?.Split('\n').FirstOrDefault(l => l.StartsWith("ecdsa-sha2-nistp256 ", StringComparison.Ordinal));
        if (text is null)
        {
            return;
        }
        var cm = (Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
        cm.PrimaryClip = ClipData.NewPlainText("ssh public key", text);
        Toast.MakeText(this, Resource.String.remote_key_copied, ToastLength.Short)!.Show();
    }

    private static JsonElement HostId(string id) =>
        BridgeCodec.ToPayload(new RemoteHostIdRequest(id), BridgeJsonContext.Default.RemoteHostIdRequest);

    private async Task<RemoteStatusInfo?> Status(string method, JsonElement? payload, TimeSpan deadline)
    {
        var s = await Call(method, payload, BridgeJsonContext.Default.RemoteStatusInfo, deadline);
        if (s is not null)
        {
            Show(s);
            _bridge.Note($"{method}: state={s.State} code={s.Code}");
            var stored = StoredHosts();
            Shadow(sh => sh.StatusObserved(method, s, stored));
        }
        return s;
    }

    /// <summary>The profile list exactly as the legacy RemoteHosts stores it (same SharedPreferences instance).</summary>
    private string? StoredHosts() =>
        GetSharedPreferences(RemoteHostProfile.PreferencesName, FileCreationMode.Private)!.GetString(RemoteHostProfile.PreferencesKey, null);

    /// <summary>PR-09 shadow comparisons run off the UI thread; inputs are captured by the caller.</summary>
    private void Shadow(Action<RemoteHostsShadow> compare)
    {
        var shadow = _bridge.RemoteShadow;
        _ = Task.Run(() => compare(shadow));
    }

    private void Show(RemoteStatusInfo s)
    {
        var lines = new List<string> { $"{GetString(Resource.String.remote_state)}：{s.State} · {s.Code}{(s.Label is { } l ? " · " + l : "")}" };
        if (s.Message is { } m)
        {
            lines.Add(m);
        }
        if (s.Probe is { } p)
        {
            lines.Add($"node：{(p.Node ? p.NodeVersion : GetString(Resource.String.remote_missing))}");
            lines.Add($"dsh：{(p.Dsh ? p.DshVersion : GetString(Resource.String.remote_missing))}");
        }
        if (s.LocalPort is { } lp)
        {
            lines.Add($"{GetString(Resource.String.remote_forward)}：127.0.0.1:{lp} → remote 127.0.0.1:{s.DshPort}");
        }
        if (s.HttpUnauthenticated is { } http)
        {
            lines.Add(Fmt(Resource.String.remote_http, http.ToString()));
        }
        lines.Add(GetString(s.HasLaunchToken ? Resource.String.remote_token_yes : Resource.String.remote_token_no));
        if (s.RemoteDshStillRunning is { } still)
        {
            lines.Add(GetString(still ? Resource.String.remote_still_running : Resource.String.remote_stopped));
        }
        if (s.Code == RemoteCodes.HostKeyChanged)
        {
            lines.Add(GetString(Resource.String.remote_key_changed));
        }
        if (s.Code == RemoteCodes.AuthFailed)
        {
            lines.Add(GetString(Resource.String.remote_auth_hint));
        }
        _status.Text = string.Join('\n', lines);
    }

    private async Task<T?> Call<T>(string method, JsonElement? payload, JsonTypeInfo<T> type, TimeSpan deadline) where T : class =>
        (await CallRaw(method, payload, type, deadline)).Value;

    /// <summary>The bridge response (for the shadow, failures included) and its payload when it succeeded.</summary>
    private async Task<(BridgeResponse? Response, T? Value)> CallRaw<T>(string method, JsonElement? payload, JsonTypeInfo<T> type, TimeSpan deadline) where T : class
    {
        Enable(false);
        try
        {
            var resp = await _bridge.Client.ExecuteAsync(method, payload, deadline);
            if (!resp.Ok)
            {
                _status.Text = $"{method} FAILED {resp.Code}: {resp.Message}";
                return (resp, null);
            }
            return (resp, BridgeCodec.ReadPayload(resp, type));
        }
        finally
        {
            Enable(true);
        }
    }

    private string Fmt(int resId, params string[] args) =>
        GetString(resId, args.Select(a => (Java.Lang.Object)new Java.Lang.String(a)).ToArray()) ?? "";

    private EditText Field(LinearLayout root, int hint, InputTypes type)
    {
        var e = new EditText(this) { Hint = GetString(hint), InputType = type };
        e.SetSingleLine(true);
        root.AddView(e);
        return e;
    }

    private LinearLayout Row(View a, View b)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.AddView(a, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        row.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        return row;
    }

    private Button Make(int text, Action onClick)
    {
        var b = new Button(this);
        b.SetText(text);
        b.SetMinHeight((int)(48 * Resources!.DisplayMetrics!.Density));
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Enable(bool on)
    {
        foreach (var b in _buttons)
        {
            b.Enabled = on;
        }
    }
}
