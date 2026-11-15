// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../BundleManager.kt rollback (GPL-3.0-or-later), applied to the
// layered body that swapRuntimeBody / LayerInstaller keep under runtime-current/previous. Registered in docs/UPSTREAMS.md.

using DshMobile.Runtime.Bundle;

namespace DshMobile.Runtime.Layers;

public sealed record LayerRollbackResult(bool Ok, string Code, string? Message, IReadOnlyList<string> Restored);

/// <summary>
/// Puts the previous runtime body back (MIGRATION row 18). An import moves the current body layers
/// (<c>base</c>, <c>node</c>, <c>android-side</c>) and <c>runtime-profile.json</c> into <c>runtime-current/previous/</c>
/// when that directory exists (legacy <c>swapRuntimeBody</c>; the DSH layer provisioning creates it for <c>previous/dsh</c>);
/// this swaps them back with the legacy <c>BundleManager.rollback</c> rules:
/// <list type="bullet">
/// <item>no previous body (profile and <c>base</c>) → <see cref="NoRollbackSlot"/>, nothing touched;</item>
/// <item>the stale <c>failed/</c> is dropped; the current body moves to <c>runtime-current/failed/</c>; the previous body
/// moves into place;</item>
/// <item>any move that fails is undone in reverse order so a usable body is never lost → <see cref="RollbackFailed"/>;</item>
/// <item><c>failed/</c> is kept (like legacy) until the next rollback; the DSH layer (<c>dsh</c>, <c>previous/dsh</c>) and
/// <c>user-data</c> are never touched.</item>
/// </list>
/// The caller guarantees that no sandbox / DSH process is running.
/// </summary>
public static class LayerRollback
{
    public const string NoRollbackSlot = "BUNDLE_NO_ROLLBACK_SLOT";
    public const string RollbackFailed = "BUNDLE_ROLLBACK_FAILED";

    /// <summary>The body items in the order they are moved (layers, then the profile, as swapRuntimeBody writes them).</summary>
    public static IReadOnlyList<string> BodyItems { get; } = ["base", "node", "android-side", RuntimeBundleLayout.ProfileName];

    public static string PreviousDir(RuntimeLayout layout) => Path.Combine(layout.RuntimeCurrentDir, "previous");

    public static string FailedDir(RuntimeLayout layout) => Path.Combine(layout.RuntimeCurrentDir, "failed");

    /// <summary>Whether a previous body exists to roll back to (its profile and its <c>base</c> layer).</summary>
    public static bool Available(RuntimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var previous = PreviousDir(layout);
        return File.Exists(Path.Combine(previous, RuntimeBundleLayout.ProfileName))
            && Directory.Exists(Path.Combine(previous, "base"))
            && Directory.EnumerateFileSystemEntries(Path.Combine(previous, "base")).Any();
    }

    public static LayerRollbackResult Rollback(RuntimeLayout layout) => Rollback(layout, LayerInstaller.TryRename);

    /// <param name="rename">The move primitive (tests inject a failing one); production: <see cref="LayerInstaller.TryRename"/>.</param>
    internal static LayerRollbackResult Rollback(RuntimeLayout layout, Func<string, string, bool> rename)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rename);
        if (!Available(layout))
        {
            return new LayerRollbackResult(false, NoRollbackSlot, "no previous runtime body (runtime-current/previous) to roll back to", []);
        }
        var current = layout.RuntimeCurrentDir;
        var previous = PreviousDir(layout);
        var failed = FailedDir(layout);
        var undo = new Stack<(string From, string To)>();
        try
        {
            if (LayerInstaller.Exists(failed))
            {
                LayerInstaller.DeleteTree(failed);
            }
            Directory.CreateDirectory(failed);
            foreach (var item in BodyItems)
            {
                var cur = Path.Combine(current, item);
                if (LayerInstaller.Exists(cur))
                {
                    Move(rename, cur, Path.Combine(failed, item), undo, "failed to move current aside");
                }
            }
            var restored = new List<string>();
            foreach (var item in BodyItems)
            {
                var prev = Path.Combine(previous, item);
                if (LayerInstaller.Exists(prev))
                {
                    Move(rename, prev, Path.Combine(current, item), undo, "failed to restore previous body");
                    restored.Add(item);
                }
            }
            return new LayerRollbackResult(true, RuntimeErrorCodes.Ok, null, restored);
        }
        catch (RollbackStepException e)
        {
            var leftover = Undo(rename, undo);
            return new LayerRollbackResult(false, RollbackFailed, e.Message + (leftover.Count == 0 ? "" : $"; could not undo: {string.Join(", ", leftover)}"), []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var leftover = Undo(rename, undo);
            return new LayerRollbackResult(false, RollbackFailed, e.Message + (leftover.Count == 0 ? "" : $"; could not undo: {string.Join(", ", leftover)}"), []);
        }
    }

    private static void Move(Func<string, string, bool> rename, string from, string to, Stack<(string From, string To)> undo, string why)
    {
        if (!rename(from, to))
        {
            throw new RollbackStepException($"{why}: {Path.GetFileName(from)}");
        }
        undo.Push((from, to));
    }

    /// <summary>Reverses the moves made so far, newest first; returns the items that could not be moved back.</summary>
    private static List<string> Undo(Func<string, string, bool> rename, Stack<(string From, string To)> undo)
    {
        var leftover = new List<string>();
        while (undo.TryPop(out var m))
        {
            if (!rename(m.To, m.From))
            {
                leftover.Add(Path.GetFileName(m.From));
            }
        }
        return leftover;
    }

    private sealed class RollbackStepException(string message) : Exception(message);
}
