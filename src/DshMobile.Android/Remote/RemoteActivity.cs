using Android.Content;
using Android.Text;
using Android.Views;
using DshMobile.App.Bridge;
using DshMobile.App.LocalNetwork;
using DshMobile.App.Ui;
using DshMobile.Application.LocalNetwork;
using DshMobile.Core.Contracts;
using DshMobile.Runtime;
using DshMobile.Runtime.Remote;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;

namespace DshMobile.App.Remote;

/// <summary>
/// Remote DSH host over SSH (TASK-012, ADR-0006). Shows the phone's SSH public key (private key stays in Android
/// Keystore), edits one host profile, connects over C# SSH.NET with explicit host-key confirmation (ADR-0012), starts /
/// stops the remote <c>dsh web</c> (ADR-0013) and reports what the remote really offers. Remote never gets this phone's
/// ui_*. ADR-0021: the same steps as a Material 3 page (connection, host profile, public key).
/// </summary>
[Activity(Label = "@string/remote_title", Exported = false, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class RemoteActivity : DshActivity
{
    private BridgeHost _bridge = null!;
    private TextView _key = null!;
    private TextView _keyMeta = null!;
    private string? _publicKeyLine;
    private TextView _statusTitle = null!;
    private ImageView _statusIcon = null!;
    private TextView _statusText = null!;
    private LinearProgressIndicator _progress = null!;
    private EditText _label = null!, _host = null!, _port = null!, _user = null!, _dshPort = null!;
    private MaterialButton _connect = null!, _disconnect = null!, _start = null!, _stop = null!, _remove = null!;
    private Button[] _buttons = [];
    private RemoteHost? _selected;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetScreen(Resource.Layout.activity_remote, Resource.String.remote_title);
        _bridge = DshMobileApplication.Current.Bridge;
        _progress = FindViewById<LinearProgressIndicator>(Resource.Id.remote_progress)!;
        _key = FindViewById<TextView>(Resource.Id.remote_key)!;
        _keyMeta = FindViewById<TextView>(Resource.Id.remote_key_meta)!;
        _statusTitle = FindViewById<TextView>(Resource.Id.remote_status_title)!;
        _statusIcon = FindViewById<ImageView>(Resource.Id.remote_status_icon)!;
        _statusText = FindViewById<TextView>(Resource.Id.remote_status)!;
        _label = FindViewById<EditText>(Resource.Id.remote_label)!;
        _host = FindViewById<EditText>(Resource.Id.remote_host)!;
        _port = FindViewById<EditText>(Resource.Id.remote_port)!;
        _user = FindViewById<EditText>(Resource.Id.remote_user)!;
        _dshPort = FindViewById<EditText>(Resource.Id.remote_dsh_port)!;

        var copy = Wire(Resource.Id.remote_copy_key, CopyKey);
        var save = Wire(Resource.Id.remote_save, Save);
        _connect = Wire(Resource.Id.remote_connect, () => _ = ConnectAsync());
        _start = Wire(Resource.Id.remote_start, StartDsh);
        _stop = Wire(Resource.Id.remote_stop, StopDsh);
        _disconnect = Wire(Resource.Id.remote_disconnect, () =>
        {
            CsRemoteConnection.Clear();
            SetStatus(GetString(Resource.String.remote_cs_disconnected));
            _bridge.Note("remote disconnect");
        });
        _remove = Wire(Resource.Id.remote_remove, ConfirmRemove);
        _buttons = [copy, save, _connect, _start, _stop, _disconnect, _remove];
        ShowConnection();
    }

    /// <summary>The status block; <see cref="ShowConnection"/> keeps the title and buttons in step with the session.</summary>
    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusText.Visibility = text.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
        ShowConnection();
    }

    private void ShowConnection()
    {
        var connected = CsRemoteConnection.Session is not null;
        _statusTitle.SetText(connected ? Resource.String.remote_connected_title : Resource.String.remote_not_connected_title);
        _statusIcon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(connected
            ? Rows.Color(_statusIcon, Resource.Attribute.colorPrimary)
            : Rows.Color(_statusIcon, Resource.Attribute.colorOnSurfaceVariant)));
        _connect.Visibility = connected ? ViewStates.Gone : ViewStates.Visible;
        _disconnect.Visibility = connected ? ViewStates.Visible : ViewStates.Gone;
        _start.Visibility = connected ? ViewStates.Visible : ViewStates.Gone;
        _stop.Visibility = connected ? ViewStates.Visible : ViewStates.Gone;
        _remove.Visibility = _selected is null ? ViewStates.Gone : ViewStates.Visible;
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
            _publicKeyLine = line;
            _key.Text = line;
            _keyMeta.Text = $"{fp} · {hw}"
                + (id.SelfTestVerified == false ? "\n" + Fmt(Resource.String.remote_key_selftest_failed, id.Error ?? "?") : "");
        }
        else
        {
            _publicKeyLine = null;
            _key.Text = "";
            _keyMeta.Text = Fmt(Resource.String.remote_key_unavailable, id.Error ?? "no key");
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
        _remove.Visibility = ViewStates.Visible;
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
            SetStatus(GetString(Resource.String.remote_bad_port));
            return;
        }
        var req = new RemoteHostRequest(_selected?.Id, _label.Text ?? "", (_host.Text ?? "").Trim(), port, (_user.Text ?? "").Trim(), dshPort);
        try
        {
            var cs = CsRemoteHosts.Save(this, req);
            _bridge.Note($"remote.hosts.save: {(req.Id is null ? "new" : "updated")} host, pin {(cs.PinnedFingerprint is null ? "none" : "kept")}");
            Select(CsRemoteHosts.ToDto(cs));
            SetStatus(GetString(cs.PinnedFingerprint is null ? Resource.String.remote_saved_unpinned : Resource.String.remote_saved_pinned));
        }
        catch (RemoteProfileException e)
        {
            SetStatus($"remote.hosts.save FAILED {RuntimeErrorCodes.BadRequest}: {e.Message}");
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
            SetStatus(GetString(Resource.String.remote_save_first));
            return;
        }
        var hostId = _selected.Id;
        CsRemoteConnection.Clear();
        var profile = RemoteHostProfile.ParseStored(StoredHosts()).FirstOrDefault(p => p.Id == hostId);
        if (profile is null)
        {
            SetStatus(GetString(Resource.String.remote_save_first));
            return;
        }
        SetStatus(GetString(Resource.String.remote_cs_connecting));
        Enable(false);
        try
        {
            // ADR-0032: a private literal (192.168.x, .local, ...) cannot be reached on Android 17 without the LAN
            // permission; ask first so the user sees a prompt instead of a 30 s timeout. A public host is not gated,
            // and a denial still falls through to the real connect so the failure is the platform's, not ours.
            var lan = LocalNetworkDecision.NotRequired;
            if (LocalNetworkPolicy.IsPrivateHost(profile.Host))
            {
                lan = await LocalNetworkAccess.EnsureAsync(this, profile.Host).ConfigureAwait(true);
            }
            var connector = CsRemoteConnection.Connector();
            var attempt = await Task.Run(() => connector.ConnectAsync(profile));
            CsRemoteConnection.Set(profile, attempt);
            // The key this attempt was shown is the only one a following trust may pin.
            CsRemoteHosts.Presented(profile, attempt);
            _bridge.Note($"remote connect: state={attempt.State} code={attempt.Code}"
                + (lan == LocalNetworkDecision.NeedsPermission ? $" lan={LocalNetworkCodes.PermissionDenied}" : string.Empty));
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
            SetStatus(Fmt(Resource.String.remote_cs_failed, $"{e.GetType().Name}: {e.Message}"));
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
        SetStatus(GetString(Resource.String.remote_not_connected));
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
            SetStatus(Fmt(Resource.String.remote_cs_failed, $"{e.GetType().Name}: {e.Message}"));
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
        SetStatus(extra + "\n" + _statusText.Text);
    }

    /// <summary>
    /// The user compares the fingerprint with the server's own (ssh-keygen -lf) before trusting it. Only the key the last
    /// C# attempt was shown can be pinned; then C# connects again.
    /// </summary>
    private void ConfirmFingerprint(RemoteHost host, string fingerprint, string? keyType)
    {
        new MaterialAlertDialogBuilder(this)
            .SetIcon(Resource.Drawable.ic_key)!
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
                    SetStatus($"remote.trust FAILED {RuntimeErrorCodes.BadRequest}: {e.Message}");
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
        new MaterialAlertDialogBuilder(this)
            .SetIcon(Resource.Drawable.ic_delete)!
            .SetTitle(Fmt(Resource.String.remote_remove_confirm, h.Label))!
            .SetPositiveButton(Resource.String.remote_remove, (_, _) =>
            {
                CsRemoteConnection.Clear(); // removing a host also disconnects it
                CsRemoteHosts.Remove(this, h.Id);
                _bridge.Note("remote.hosts.remove");
                _selected = null;
                ShowConnection();
                SetStatus(GetString(Resource.String.remote_removed));
            })!
            .SetNegativeButton(Android.Resource.String.Cancel, (_, _) => { })!
            .Show();
    }

    private void CopyKey()
    {
        var text = _publicKeyLine;
        if (text is null || !text.StartsWith("ecdsa-sha2-nistp256 ", StringComparison.Ordinal))
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
        SetStatus(string.Join('\n', lines));
    }

    private string Fmt(int resId, params string[] args) =>
        GetString(resId, args.Select(a => (Java.Lang.Object)new Java.Lang.String(a)).ToArray()) ?? "";

    private MaterialButton Wire(int id, Action onClick)
    {
        var b = FindViewById<MaterialButton>(id)!;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Enable(bool on)
    {
        _progress.Visibility = on ? ViewStates.Gone : ViewStates.Visible;
        foreach (var b in _buttons)
        {
            b.Enabled = on;
        }
    }
}
