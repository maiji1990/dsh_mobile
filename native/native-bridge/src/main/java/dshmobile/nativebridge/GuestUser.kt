package dshmobile.nativebridge

import android.system.Os
import java.io.File

/**
 * Adds a guest /etc/passwd entry for the app's real Linux uid, so shells show a user name instead of
 * "I have no name!" (PRoot runs the guest as the app uid, which the Debian base layer doesn't know).
 *
 * Ported from DSHBox `app/.../runtime/GuestUserProvisioner.kt` (GPL-3.0, v1.5.1). Differences: user name
 * `dshmobile` instead of DSHBox's brand name (AGENTS.md rule 4); same idempotent, uid-at-runtime logic.
 */
internal object GuestUser {
    const val USER_NAME = "dshmobile"
    private const val HOME_DIR = "/root"
    private const val LOGIN_SHELL = "/bin/bash"

    enum class Outcome { UPDATED, ALREADY_PRESENT, SKIPPED }

    fun provision(baseRoot: File): Outcome {
        if (!baseRoot.isDirectory) return Outcome.SKIPPED
        val file = File(baseRoot, "etc/passwd")
        val current = runCatching { file.readText() }.getOrNull() ?: return Outcome.SKIPPED
        if (current.lineSequence().none { it.startsWith("root:") }) return Outcome.SKIPPED
        val uid = runCatching { Os.getuid() }.getOrDefault(-1)
        val gid = runCatching { Os.getgid() }.getOrDefault(-1)
        if (uid < 0 || gid < 0) return Outcome.SKIPPED
        val updated = withEntry(current, uid, gid)
        if (updated == current) return Outcome.ALREADY_PRESENT
        return runCatching {
            val tmp = File(file.parentFile, "passwd.dshmobile.tmp")
            tmp.writeText(updated)
            if (!tmp.renameTo(file)) {
                file.writeText(updated)
                tmp.delete()
            }
            Outcome.UPDATED
        }.getOrDefault(Outcome.SKIPPED)
    }

    /** Pure: drops any previous entry of ours, appends the current one. Exposed for unit tests. */
    fun withEntry(content: String, uid: Int, gid: Int): String {
        val kept = content.lineSequence().filter { it.isNotBlank() && it.substringBefore(':').trim() != USER_NAME }.toList()
        val entry = "$USER_NAME:x:$uid:$gid::$HOME_DIR:$LOGIN_SHELL"
        return (kept + entry).joinToString(separator = "\n", postfix = "\n")
    }
}
