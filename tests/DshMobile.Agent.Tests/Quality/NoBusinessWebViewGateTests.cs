using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DshMobile.Agent.Tests.Quality;

/// <summary>
/// PR-09（N-09-03）永久 CI 门禁：
/// 全仓源码、布局 XML、Manifest、项目依赖与净构建 APK 产物中禁止存在业务 WebView 或已移除的 WebUI 宿主栈。
/// </summary>
public sealed partial class NoBusinessWebViewGateTests
{
    public const string VerifyApkPathEnvVar = "DSHM_VERIFY_APK_PATH";

    private static readonly string[] ForbiddenCodeTokens =
    [
        "Android.Webkit.WebView",
        "WebViewClient",
        "WebChromeClient",
        "DshWebActivity",
        "CSharpWebUiController",
        "activity_web",
        "WebUiPolicy",
        "DshOriginPolicy",
        "WaitingForWebUi",
        "HomeAction.OpenDsh",
        "Xamarin.AndroidX.WebKit",
    ];

    private static readonly string[] ForbiddenXmlTokens =
    [
        "<WebView",
        "android.webkit.WebView",
        "DshWebActivity",
        "activity_web",
        "home_open_native_chat",
        "nav_home",
    ];

    private static readonly string[] ForbiddenApkPathTokens =
    [
        "activity_web",
        "fragment_home",
        "dshwebactivity",
        "webview",
        "webkit",
    ];

    private static readonly string[] ForbiddenApkBinaryResourceTokens =
    [
        "DshWebActivity",
        "CSharpWebUiController",
        "activity_web",
        "WebUiPolicy",
        "DshOriginPolicy",
        "HomeFragment",
        "fragment_home",
        "WaitingForWebUi",
        "home_open_native_chat",
        "nav_home",
        "Xamarin.AndroidX.WebKit",
        "WebView",
        "WebKit",
    ];

    private static readonly string[] ForbiddenApkDexTokens =
    [
        "DshWebActivity",
        "CSharpWebUiController",
        "activity_web",
        "WebUiPolicy",
        "DshOriginPolicy",
        "HomeFragment",
        "fragment_home",
        "WaitingForWebUi",
        "HomeAction.OpenDsh",
        "Xamarin.AndroidX.WebKit",
        "WebViewClient",
        "WebChromeClient",
    ];

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.Compiled)]
    private static partial Regex XmlCommentRegex();

    [Fact]
    public void SourceProjects_HaveZeroBusinessWebViewReferences()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
            {
                continue;
            }

            var raw = File.ReadAllText(file);
            var codeOnly = StripCSharpComments(raw);
            foreach (var token in ForbiddenCodeTokens)
            {
                Assert.DoesNotContain(token, codeOnly, StringComparison.Ordinal);
            }

            // 仅剥离注释后检查裸 WebView 类名，允许注释中出现「绝不内嵌 WebView」等说明文字。
            Assert.DoesNotContain("WebView", codeOnly, StringComparison.Ordinal);

            // 若文件引入了 Android.Webkit，只允许用于 MimeTypeMap（SAF/Direct 媒体类型解析），绝不允许使用 WebView/CookieManager 等视图 API。
            if (codeOnly.Contains("Android.Webkit", StringComparison.Ordinal))
            {
                Assert.Contains("MimeTypeMap", codeOnly, StringComparison.Ordinal);
                Assert.DoesNotContain("CookieManager", codeOnly, StringComparison.Ordinal);
                Assert.DoesNotContain("WebStorage", codeOnly, StringComparison.Ordinal);
                Assert.DoesNotContain("WebSettings", codeOnly, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AndroidXmlManifestAndProjects_HaveZeroBusinessWebView()
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");

        foreach (var csproj in Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(csproj))
            {
                continue;
            }
            var content = File.ReadAllText(csproj);
            Assert.DoesNotContain("WebKit", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WebView", content, StringComparison.OrdinalIgnoreCase);
        }

        var manifestPath = Path.Combine(srcRoot, "DshMobile.Android", "AndroidManifest.xml");
        var manifestNoComments = XmlCommentRegex().Replace(File.ReadAllText(manifestPath), string.Empty);
        foreach (var token in ForbiddenXmlTokens)
        {
            Assert.DoesNotContain(token, manifestNoComments, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("WebView", manifestNoComments, StringComparison.OrdinalIgnoreCase);

        var resDir = Path.Combine(srcRoot, "DshMobile.Android", "Resources");
        foreach (var xml in Directory.EnumerateFiles(resDir, "*.xml", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(xml))
            {
                continue;
            }
            var xmlNoComments = XmlCommentRegex().Replace(File.ReadAllText(xml), string.Empty);
            foreach (var token in ForbiddenXmlTokens)
            {
                Assert.DoesNotContain(token, xmlNoComments, StringComparison.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain("WebView", xmlNoComments, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(srcRoot, "DshMobile.Android", "DshWebActivity.cs")));
        Assert.False(File.Exists(Path.Combine(srcRoot, "DshMobile.Android", "WebUi", "CSharpWebUiController.cs")));
        Assert.False(File.Exists(Path.Combine(srcRoot, "DshMobile.Android", "Resources", "layout", "activity_web.xml")));
        Assert.False(File.Exists(Path.Combine(srcRoot, "DshMobile.Android", "Home", "HomeFragment.cs")));
        Assert.False(File.Exists(Path.Combine(srcRoot, "DshMobile.Android", "Resources", "layout", "fragment_home.xml")));
    }

    [Fact]
    public void ApkArtifactVerifier_AcceptsCleanPlatformBindings_AndRejectsStaleOrBusinessWebView()
    {
        var tempDir = Directory.CreateTempSubdirectory("dshm_apk_gate_");
        try
        {
            var cleanApk = Path.Combine(tempDir.FullName, "clean.apk");
            WriteSyntheticApk(
                cleanApk,
                dexEntries:
                [
                    ("classes.dex", BuildMinimalDex(
                        "Lio/github/maiji1990/dshmobile/MainActivity;",
                        "Landroid/webkit/MimeTypeMap;",
                        "Landroid/webkit/WebView;",
                        "android.webkit.")),
                    ("classes2.dex", BuildMinimalDex(
                        "Android.Webkit.WebView+IFindListenerImplementor, Mono.Android",
                        "Lmono/android/webkit/WebView_FindListenerImplementor;",
                        "WebView_FindListenerImplementor.java",
                        "n_onFindResultReceived:(IIZ)V:GetOnFindResultReceived_IIZHandler:Android.Webkit.WebView+IFindListenerInvoker, Mono.Android, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\n"))
                ],
                manifestBytes: Encoding.UTF8.GetBytes("MainActivity"),
                resourcesBytes: Encoding.Unicode.GetBytes("app_name"));

            Assert.Empty(FindApkViolations(cleanApk));

            // 模拟 F27：增量构建在 classes.dex 中残留已删除类型的 Java 调用包装字符串。
            var staleDexApk = Path.Combine(tempDir.FullName, "stale-dex.apk");
            WriteSyntheticApk(
                staleDexApk,
                dexEntries:
                [
                    ("classes.dex", BuildMinimalDex(
                        "Lcrc64deadbeef/DshWebActivity;",
                        "Lcrc64deadbeef/CSharpWebUiController;",
                        "Landroid/webkit/WebViewClient;"))
                ],
                manifestBytes: Encoding.UTF8.GetBytes("MainActivity"),
                resourcesBytes: []);

            var staleViolations = FindApkViolations(staleDexApk);
            Assert.Contains(staleViolations, v => v.Contains("DshWebActivity", StringComparison.Ordinal));
            Assert.Contains(staleViolations, v => v.Contains("CSharpWebUiController", StringComparison.Ordinal));
            Assert.Contains(staleViolations, v => v.Contains("WebViewClient", StringComparison.Ordinal));

            // 非 Mono.Android 的任意自定义 WebView 类或 Manifest UTF-16LE / 布局条目也必须被拦截。
            var customWebViewApk = Path.Combine(tempDir.FullName, "custom-webview.apk");
            WriteSyntheticApk(
                customWebViewApk,
                dexEntries:
                [
                    ("classes.dex", BuildMinimalDex("Lio/github/maiji1990/dshmobile/EmbeddedWebViewHelper;"))
                ],
                manifestBytes: Encoding.Unicode.GetBytes("android.webkit.WebView"),
                resourcesBytes: [],
                extraEntries: [("res/layout/activity_web.xml", "<LinearLayout/>"u8.ToArray())]);

            var customViolations = FindApkViolations(customWebViewApk);
            Assert.Contains(customViolations, v => v.Contains("EmbeddedWebViewHelper", StringComparison.Ordinal));
            Assert.Contains(customViolations, v => v.Contains("AndroidManifest.xml", StringComparison.Ordinal));
            Assert.Contains(customViolations, v => v.Contains("activity_web", StringComparison.Ordinal));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void BuiltReleaseApk_WhenVerifyPathSpecified_HasZeroBusinessWebView()
    {
        var apkPath = Environment.GetEnvironmentVariable(VerifyApkPathEnvVar);
        if (string.IsNullOrWhiteSpace(apkPath))
        {
            // 未显式指定待验 APK 时跳过产物扫描（由净构建脚本与 CI 在构建后注入 DSHM_VERIFY_APK_PATH 强制执行）。
            return;
        }

        Assert.True(File.Exists(apkPath), $"Specified APK does not exist: {apkPath}");
        var violations = FindApkViolations(apkPath);
        Assert.True(
            violations.Count == 0,
            $"APK artifact contains business WebView or removed legacy references ({apkPath}):{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    internal static IReadOnlyList<string> FindApkViolations(string apkPath)
    {
        var violations = new List<string>();
        using var zip = ZipFile.OpenRead(apkPath);

        var hasManifest = false;
        var dexCount = 0;

        foreach (var entry in zip.Entries)
        {
            var fullName = entry.FullName;

            foreach (var token in ForbiddenApkPathTokens)
            {
                if (fullName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"ZIP entry path contains forbidden token '{token}': {fullName}");
                }
            }

            if (string.Equals(fullName, "AndroidManifest.xml", StringComparison.Ordinal))
            {
                hasManifest = true;
                ScanBinaryResourceEntry(entry, violations);
            }
            else if (string.Equals(fullName, "resources.arsc", StringComparison.Ordinal))
            {
                ScanBinaryResourceEntry(entry, violations);
            }
            else if (fullName.StartsWith("classes", StringComparison.Ordinal) &&
                     fullName.EndsWith(".dex", StringComparison.Ordinal) &&
                     !fullName.Contains('/', StringComparison.Ordinal))
            {
                dexCount++;
                ScanDexEntry(entry, violations);
            }
        }

        if (!hasManifest)
        {
            violations.Add("APK missing AndroidManifest.xml");
        }
        if (dexCount == 0)
        {
            violations.Add("APK contains no classes*.dex entries");
        }

        return violations;
    }

    private static void ScanBinaryResourceEntry(ZipArchiveEntry entry, List<string> violations)
    {
        var bytes = ReadEntryBytes(entry);
        var ascii = Encoding.ASCII.GetString(bytes);
        var utf16 = Encoding.Unicode.GetString(bytes);

        foreach (var token in ForbiddenApkBinaryResourceTokens)
        {
            if (ascii.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                utf16.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{entry.FullName} contains forbidden token '{token}'");
            }
        }
    }

    private static void ScanDexEntry(ZipArchiveEntry entry, List<string> violations)
    {
        var dex = ReadEntryBytes(entry);
        if (dex.Length < 0x70 ||
            dex[0] != (byte)'d' ||
            dex[1] != (byte)'e' ||
            dex[2] != (byte)'x' ||
            dex[3] != (byte)'\n')
        {
            violations.Add($"{entry.FullName} does not start with a valid DEX header");
            return;
        }

        var stringCount = BinaryPrimitives.ReadUInt32LittleEndian(dex.AsSpan(56, 4));
        var stringTableOff = BinaryPrimitives.ReadUInt32LittleEndian(dex.AsSpan(60, 4));
        var tableEnd = (long)stringTableOff + stringCount * 4L;
        if (stringTableOff < 0x70 || tableEnd > dex.Length)
        {
            violations.Add($"{entry.FullName} has out-of-range string_ids table ({stringTableOff}..{tableEnd})");
            return;
        }

        for (uint i = 0; i < stringCount; i++)
        {
            var itemOff = BinaryPrimitives.ReadUInt32LittleEndian(dex.AsSpan((int)(stringTableOff + i * 4), 4));
            if (itemOff >= dex.Length)
            {
                violations.Add($"{entry.FullName} string_id[{i}] offset {itemOff} out of bounds");
                return;
            }

            var pos = (int)itemOff;
            while (pos < dex.Length && (dex[pos++] & 0x80) != 0)
            {
            }

            if (pos >= dex.Length)
            {
                continue;
            }

            var end = Array.IndexOf(dex, (byte)0, pos);
            if (end <= pos)
            {
                continue;
            }

            var str = Encoding.UTF8.GetString(dex, pos, end - pos);

            var hitForbidden = false;
            foreach (var token in ForbiddenApkDexTokens)
            {
                if (str.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{entry.FullName} contains forbidden business/legacy token '{token}': {str.TrimEnd()}");
                    hitForbidden = true;
                }
            }

            if (!hitForbidden &&
                (str.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
                 str.Contains("WebKit", StringComparison.OrdinalIgnoreCase)) &&
                !IsAllowedPlatformWebKitDexString(str))
            {
                violations.Add($"{entry.FullName} contains unapproved WebView/WebKit string: {str.TrimEnd()}");
            }
        }
    }

    private static bool IsAllowedPlatformWebKitDexString(string value)
    {
        var text = value.TrimEnd('\r', '\n');
        if (text is "Landroid/webkit/MimeTypeMap;"
            or "android.webkit."
            or "Landroid/webkit/DownloadListener;"
            or "Landroid/webkit/WebIconDatabase$IconListener;"
            or "Landroid/webkit/WebView;"
            or "Landroid/webkit/WebView$FindListener;"
            or "Landroid/webkit/WebView$PictureListener;"
            or "WebView_FindListenerImplementor.java"
            or "WebView_PictureListenerImplementor.java")
        {
            return true;
        }

        // 只放行 Mono.Android 自动生成的 webkit 接口实现代理，绝不放行 crc64 业务类或 WebViewClient。
        if (text.StartsWith("Lmono/android/webkit/", StringComparison.Ordinal) &&
            text.EndsWith("Implementor;", StringComparison.Ordinal))
        {
            return true;
        }

        if (text.Contains(", Mono.Android", StringComparison.Ordinal) &&
            (text.StartsWith("Android.Webkit.", StringComparison.Ordinal) ||
             text.StartsWith("n_on", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteSyntheticApk(
        string path,
        (string Name, byte[] Data)[] dexEntries,
        byte[] manifestBytes,
        byte[] resourcesBytes,
        (string Name, byte[] Data)[]? extraEntries = null)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteZipEntry(zip, "AndroidManifest.xml", manifestBytes);
        WriteZipEntry(zip, "resources.arsc", resourcesBytes);
        foreach (var (name, data) in dexEntries)
        {
            WriteZipEntry(zip, name, data);
        }
        if (extraEntries is not null)
        {
            foreach (var (name, data) in extraEntries)
            {
                WriteZipEntry(zip, name, data);
            }
        }
    }

    private static void WriteZipEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private static byte[] BuildMinimalDex(params string[] strings)
    {
        const int headerSize = 0x70;
        using var ms = new MemoryStream();
        ms.Write(new byte[headerSize]);

        var tablePos = (int)ms.Position;
        ms.Write(new byte[strings.Length * 4]);

        var offsets = new int[strings.Length];
        for (var i = 0; i < strings.Length; i++)
        {
            offsets[i] = (int)ms.Position;
            var utf8 = Encoding.UTF8.GetBytes(strings[i]);
            WriteUleb128(ms, (uint)strings[i].Length);
            ms.Write(utf8, 0, utf8.Length);
            ms.WriteByte(0);
        }

        var buffer = ms.ToArray();
        "dex\n035\0"u8.CopyTo(buffer.AsSpan(0, 8));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(32, 4), buffer.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(36, 4), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), 0x12345678);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(56, 4), strings.Length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(60, 4), tablePos);

        for (var i = 0; i < strings.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(tablePos + i * 4, 4), offsets[i]);
        }

        return buffer;
    }

    private static void WriteUleb128(Stream stream, uint value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }
            stream.WriteByte(b);
        } while (value != 0);
    }

    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string StripCSharpComments(string source) =>
        string.Join('\n', source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));
}
