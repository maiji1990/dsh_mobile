// 移植自 DSHBox v1.5.1 (7c9d33c) dsh-renderer/api DshTokenRevisionTracker.kt (GPL-3.0-or-later)。
// 向量回归见 tests/DshMobile.Runtime.Tests/TokenRevisionTrackerTests.cs（tests/TestVectors/webview-policy.json#tokenTracker）。

using System.Globalization;

namespace DshMobile.Runtime.Hosting;

/// <summary>
/// 进程内存态 launch token 可用性与非敏感单调版本号追踪器：
/// 当同一 token 短暂离线后恢复时保留原版本号，仅在出现真正不同的新 token 时递增；绝不打印 token 原文。
/// </summary>
public sealed class DshTokenRevisionTracker
{
    private string? _lastAvailable;
    private long _generation;

    public DshTokenRevisionTracker(string? initialToken = null)
    {
        _lastAvailable = string.IsNullOrEmpty(initialToken) ? null : initialToken;
        _generation = _lastAvailable is null ? 0 : 1;
        Token = _lastAvailable;
    }

    public string? Token { get; private set; }

    public long? Revision => Token is null ? null : _generation;

    /// <returns>可用性或 token 值是否发生变化。</returns>
    public bool Update(string? value)
    {
        var available = string.IsNullOrEmpty(value) ? null : value;
        if (string.Equals(available, Token, StringComparison.Ordinal))
        {
            return false;
        }
        Token = available;
        if (available is not null && !string.Equals(available, _lastAvailable, StringComparison.Ordinal))
        {
            _generation++;
            _lastAvailable = available;
        }
        return true;
    }

    /// <summary>仅在宿主停止/释放时清空全部状态。</summary>
    public void ClearForRelease()
    {
        Token = null;
        _lastAvailable = null;
        _generation = 0;
    }

    public override string ToString() => $"DshTokenRevisionTracker(revision={Revision?.ToString(CultureInfo.InvariantCulture) ?? "null"})";
}
