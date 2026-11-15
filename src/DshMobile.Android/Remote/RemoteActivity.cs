using Android.Content;
using Android.Text;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.Core.Contracts;
using DshMobile.Runtime;
using DshMobile.Runtime.Remote;

namespace DshMobile.App.Remote;

/// <summary>
/// Remote DSH host over SSH (TASK-012, ADR-0006). Shows the phone's SSH public key (private key stays in Android
/// Keystore), edits one host profile, connects over C# SSH.NET with explicit host-key confirmation (ADR-0012), starts /
/// stops the remote <c>dsh web</c> (ADR-0013) and reports what the remote really offers. Remote never gets this phone's
/// ui_*.
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

        var save = Make(Resource.String.remote_save, Save);
        var connect = Make(Resource.String.remote_connect, () => _ = ConnectAsync());
        var start = Make(Resource.String.remote_start, StartDsh);
        var stop = Make(Resource.String.remote_stop, StopDsh);
        var disconnect = Make(Resource.String.remote_disconnect, () =>
        {
            CsRemoteConnection.Clear();
            _status.Text = GetString(Resource.String.remote_cs_disconnected);
            _bridge.Note("remote disconnect");
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

    /// <summary>The SSH identity (Keystore key created on first use) and the stored profiles.</summary>
    private async Task LoadAsync()
    {
        Enable(false);
        SshIdentityFacts id;
        try
        {
            id = await Task.Run(() =>
            {
                try
                {
                    AndroidSshKeySource.Ensure();
                }
                catch (Java.Lang.Exception e)
                {
                    return new SshIdentityFacts(false, null, null, null, null, $"create key: {e.GetType().Name}: {e.Message}");
                }
                return SshIdentity.Probe(new AndroidSshKeySource());
            });
        }
        finally
        {
            Enable(true);
        }
        if (id is { PublicKeyLine: { } line, Fingerprint: { } fp })
        {
            var hw = id.HardwareBacked switch { true => GetString(Resource.String.remote_key_hw), false => GetString(Resource.String.remote_key_sw), null => "?" };
            _identity.Text = $"{GetString(Resource.String.remote_key_heading)}（{hw}）\n{line}\n{fp}"
                + (id.SelfTestVerified == false ? "\n" + Fmt(Resource.String.remote_key_selftest_failed, id.Error ?? "?") : "");
        }
        else
        {
            _identity.Text = Fmt(Resource.String.remote_key_unavailable, id.Error ?? "no key");
        }
        // The public line is what the user copies into authorized_keys anyway (not a secret).
        _bridge.Note($"remote identity: present={id.KeyPresent} hw={id.HardwareBacked?.ToString() ?? "?"} selftest={id.SelfTestVerified?.ToString() ?? "-"} key={id.PublicKeyLine ?? "-"} {id.Fingerprint ?? ""}");
        var hosts = CsRemoteHosts.List(this);
        if (hosts.Count > 0)
        {
            Select(CsRemoteHosts.ToDto(hosts[^1]));
        }
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

    private void Save()
    {
        if (!int.TryParse(_port.Text, out var port) || !int.TryParse(_dshPort.Text, out var dshPort))
        {
            _status.Text = GetString(Resource.String.remote_bad_port);
            return;
        }
        var req = new RemoteHostRequest(_selected?.Id, _label.Text ?? "", (_host.Text ?? "").Trim(), port, (_user.Text ?? "").Trim(), dshPort);
        try
        {
            var cs = CsRemoteHosts.Save(this, req);
            _bridge.Note($"remote.hosts.save: {(req.Id is null ? "new" : "updated")} host, pin {(cs.PinnedFingerprint is null ? "none" : "kept")}");
            Select(CsRemoteHosts.ToDto(cs));
            _status.Text = GetString(cs.PinnedFingerprint is null ? Resource.String.remote_saved_unpinned : Resource.String.remote_saved_pinned);
        }
        catch (RemoteProfileException e)
        {
            _status.Text = $"remote.hosts.save FAILED {RuntimeErrorCodes.BadRequest}: {e.Message}";
        }
    }

    /// <summary>
    /// C# connect (ADR-0012): SSH.NET connects to the stored profile with its pin and the Keystore key and runs the probe.
    /// An unknown key is refused and shown; the user may pin exactly that key after comparing it, then C# connects again.
    /// </summary>
    private async Task ConnectAsync()
    {
        if (_selected is null)
        {
            _status.Text = GetString(Resource.String.remote_save_first);
            return;
        }
        var hostId = _selected.Id;
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
            // The key this attempt was shown is the only one a following trust may pin.
            CsRemoteHosts.Presented(profile, attempt);
            _bridge.Note($"remote connect: state={attempt.State} code={attempt.Code}");
            if (CsRemoteConnection.Status() is { } st)
            {
                ShowCs(st);
            }
            if (attempt is { Code: RemoteCodes.HostKeyUnknown, Fingerprint: { } fp })
            {
                ConfirmFingerprint(CsRemoteHosts.ToDto(profile), fp, attempt.KeyType);
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

    /// <summary>Start / stop of the remote DSH on the open C# session (ADR-0013).</summary>
    private void StartDsh() => RemoteDsh(start: true);

    private void StopDsh() => RemoteDsh(start: false);

    private void RemoteDsh(bool start)
    {
        if (CsRemoteConnection.Session is { } cs)
        {
            _ = CsDshAsync(cs, start);
            return;
        }
        _status.Text = GetString(Resource.String.remote_not_connected);
    }

    private async Task CsDshAsync(RemoteSshSession session, bool start)
    {
        Enable(false);
        try
        {
            var d = await Task.Run(() => start ? session.StartDshAsync() : session.StopDshAsync());
            _bridge.Note($"remote {(start ? "start" : "stop")}: state={d.State} code={d.Code} http={d.HttpUnauthenticated?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} still={d.RemoteDshStillRunning?.ToString() ?? "-"} token={d.HasLaunchToken}");
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

    /// <summary>
    /// The user compares the fingerprint with the server's own (ssh-keygen -lf) before trusting it. Only the key the last
    /// C# attempt was shown can be pinned; then C# connects again.
    /// </summary>
    private void ConfirmFingerprint(RemoteHost host, string fingerprint, string? keyType)
    {
        new AlertDialog.Builder(this)
            .SetTitle(Resource.String.remote_trust_title)!
            .SetMessage(Fmt(Resource.String.remote_trust_message, $"{host.User}@{host.Host}:{host.Port}", keyType ?? "?", fingerprint))!
            .SetPositiveButton(Resource.String.remote_trust_yes, async (_, _) =>
            {
                try
                {
                    var all = CsRemoteHosts.Trust(this, host.Id, fingerprint);
                    _bridge.Note("remote.trust: pinned the key the connection was shown");
                    if (all.FirstOrDefault(p => p.Id == host.Id) is { } pinned)
                    {
                        Select(CsRemoteHosts.ToDto(pinned));
                        await ConnectAsync();
                    }
                }
                catch (RemoteProfileException e)
                {
                    _status.Text = $"remote.trust FAILED {RuntimeErrorCodes.BadRequest}: {e.Message}";
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
            .SetPositiveButton(Resource.String.remote_remove, (_, _) =>
            {
                CsRemoteConnection.Clear(); // removing a host also disconnects it
                CsRemoteHosts.Remove(this, h.Id);
                _bridge.Note("remote.hosts.remove");
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

    /// <summary>The profile list exactly as stored (SharedPreferences, same file and format as DSHBox RemoteHosts).</summary>
    private string? StoredHosts() => CsRemoteHosts.Stored(this);

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
