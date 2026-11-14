// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/direct/IntentTemplates.kt
// Registered in docs/UPSTREAMS.md. The closed sys.intent template table: a fixed system action per template and a
// whitelist of keys and ranges. The agent never names an action or an extra (a free-form Intent is wider than a shell).

using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshMobile.Runtime.Automation.Relay.Exec.Direct;

public static partial class IntentTemplates
{
    public const string KeyTemplate = "template";
    public const string KeyHour = "hour";
    public const string KeyMinute = "minute";
    public const string KeyMessage = "message";
    public const string KeyLength = "length";
    public const string KeyPage = "page";
    public const string KeyPackage = "package";
    public const string KeyNumber = "number";
    public const string KeyUrl = "url";

    public const string AlarmSet = "alarm.set";
    public const string TimerSet = "timer.set";
    public const string AlarmShow = "alarm.show";
    public const string TimerShow = "timer.show";
    public const string SettingsOpen = "settings.open";
    public const string AppInfo = "app.info";
    public const string Dial = "dial";
    public const string WebOpen = "web.open";

    public static readonly IReadOnlyList<string> Names = [AlarmSet, TimerSet, AlarmShow, TimerShow, SettingsOpen, AppInfo, Dial, WebOpen];

    /// <summary>Kotlin <c>setOf</c> keeps insertion order; the messages print it.</summary>
    public static readonly IReadOnlyList<string> SettingsPages = ["wifi", "bluetooth", "display", "location", "sound", "apn", "developer", "nfc"];

    private const int MaxMessageChars = 100;
    private const int MinLengthSeconds = 1;
    private const int MaxLengthSeconds = 24 * 60 * 60;
    private const int MaxUrlChars = 512;
    private const int MaxNumberChars = 32;

    [GeneratedRegex("^[0-9+*#()., -]{1,32}$")]
    private static partial Regex DialNumber();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+){1,}$")]
    private static partial Regex PackageName();

    /// <summary>Validated parameters; which ones are set depends on <see cref="Template"/> (-1 / null otherwise).</summary>
    public sealed record Params(
        string Template,
        int Hour = -1,
        int Minute = -1,
        int LengthSeconds = -1,
        string? Message = null,
        string? Page = null,
        string? PackageName = null,
        string? Number = null,
        string? Url = null);

    public abstract record Outcome
    {
        public sealed record Ok(Params Params) : Outcome;

        public sealed record Bad(string Reason) : Outcome;
    }

    public static Outcome Validate(JsonElement args)
    {
        var template = OrgJson.KotlinTrim(OrgJson.OptString(args, KeyTemplate));
        if (template.Length == 0)
        {
            return new Outcome.Bad($"missing arg: {KeyTemplate}");
        }
        if (!Names.Contains(template))
        {
            return new Outcome.Bad($"unknown template: {template}, accepted: [{string.Join(", ", Names)}]");
        }
        var rawMessage = OrgJson.OptString(args, KeyMessage);
        var message = OrgJson.KotlinTrim(rawMessage).Length == 0 ? null : OrgJson.KotlinTrim(rawMessage);
        if (message is not null && message.Length > MaxMessageChars)
        {
            return new Outcome.Bad($"{KeyMessage} must be at most {MaxMessageChars} characters, got {message.Length}");
        }
        return template switch
        {
            AlarmSet => ValidateAlarm(args, message),
            TimerSet => ValidateTimer(args, message),
            AlarmShow or TimerShow => new Outcome.Ok(new Params(template, Message: message)),
            SettingsOpen => ValidateSettings(args),
            AppInfo => ValidateAppInfo(args),
            Dial => ValidateDial(args),
            _ => ValidateWeb(args),
        };
    }

    private static Outcome ValidateAlarm(JsonElement args, string? message)
    {
        if (!args.TryGetProperty(KeyHour, out _))
        {
            return new Outcome.Bad($"missing arg: {KeyHour}");
        }
        if (!args.TryGetProperty(KeyMinute, out _))
        {
            return new Outcome.Bad($"missing arg: {KeyMinute}");
        }
        var hour = OrgJson.OptInt(args, KeyHour, -1);
        var minute = OrgJson.OptInt(args, KeyMinute, -1);
        // Out of range is refused, never folded: 25:00 must not land as 01:00 the next day.
        if (hour is < 0 or > 23)
        {
            return new Outcome.Bad($"{KeyHour} must be within 0..23, got {hour}");
        }
        if (minute is < 0 or > 59)
        {
            return new Outcome.Bad($"{KeyMinute} must be within 0..59, got {minute}");
        }
        return new Outcome.Ok(new Params(AlarmSet, Hour: hour, Minute: minute, Message: message));
    }

    private static Outcome ValidateTimer(JsonElement args, string? message)
    {
        if (!args.TryGetProperty(KeyLength, out _))
        {
            return new Outcome.Bad($"missing arg: {KeyLength}");
        }
        var seconds = OrgJson.OptInt(args, KeyLength, -1);
        if (seconds is < MinLengthSeconds or > MaxLengthSeconds)
        {
            return new Outcome.Bad($"{KeyLength} must be within {MinLengthSeconds}..{MaxLengthSeconds} seconds, got {seconds}");
        }
        return new Outcome.Ok(new Params(TimerSet, LengthSeconds: seconds, Message: message));
    }

    private static Outcome ValidateSettings(JsonElement args)
    {
        var page = OrgJson.KotlinTrim(OrgJson.OptString(args, KeyPage)).ToLowerInvariant();
        if (page.Length == 0)
        {
            return new Outcome.Bad($"missing arg: {KeyPage}, accepted: [{string.Join(", ", SettingsPages)}]");
        }
        if (!SettingsPages.Contains(page))
        {
            return new Outcome.Bad($"{KeyPage} '{page}' is not one of [{string.Join(", ", SettingsPages)}]");
        }
        return new Outcome.Ok(new Params(SettingsOpen, Page: page));
    }

    private static Outcome ValidateAppInfo(JsonElement args)
    {
        var pkg = OrgJson.KotlinTrim(OrgJson.OptString(args, KeyPackage));
        if (pkg.Length == 0)
        {
            return new Outcome.Bad($"missing arg: {KeyPackage}");
        }
        if (!PackageName().IsMatch(pkg))
        {
            return new Outcome.Bad($"{KeyPackage} is not a package name: {pkg}");
        }
        return new Outcome.Ok(new Params(AppInfo, PackageName: pkg));
    }

    private static Outcome ValidateDial(JsonElement args)
    {
        var number = OrgJson.KotlinTrim(OrgJson.OptString(args, KeyNumber));
        if (number.Length == 0)
        {
            return new Outcome.Bad($"missing arg: {KeyNumber}");
        }
        if (!DialNumber().IsMatch(number))
        {
            return new Outcome.Bad($"{KeyNumber} may only contain digits and + * # ( ) . - , up to {MaxNumberChars} characters");
        }
        return new Outcome.Ok(new Params(Dial, Number: number));
    }

    private static Outcome ValidateWeb(JsonElement args)
    {
        var url = OrgJson.KotlinTrim(OrgJson.OptString(args, KeyUrl));
        if (url.Length == 0)
        {
            return new Outcome.Bad($"missing arg: {KeyUrl}");
        }
        if (url.Length > MaxUrlChars)
        {
            return new Outcome.Bad($"{KeyUrl} must be at most {MaxUrlChars} characters, got {url.Length}");
        }
        // Only http/https: file:, content:, intent: would turn "open a page" into opening private content or any component.
        var cut = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = (cut < 0 ? url : url[..cut]).ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return new Outcome.Bad($"{KeyUrl} must start with http:// or https://, got scheme '{scheme}'");
        }
        if (url.Any(OrgJson.KotlinIsWhitespace))
        {
            return new Outcome.Bad($"{KeyUrl} must not contain whitespace");
        }
        return new Outcome.Ok(new Params(WebOpen, Url: url));
    }
}
