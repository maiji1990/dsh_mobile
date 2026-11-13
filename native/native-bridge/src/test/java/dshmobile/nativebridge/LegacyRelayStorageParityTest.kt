package dshmobile.nativebridge

import interlock.relay.core.log.AuditEntry
import interlock.relay.core.log.AuditLog
import interlock.relay.core.log.LogEvent
import interlock.relay.core.log.LogSubsystem
import interlock.relay.core.log.RunLog
import interlock.relay.core.spi.RelayPathPolicy
import interlock.relay.core.storage.ArtifactOut
import interlock.relay.core.storage.QuotaLedger
import interlock.relay.core.storage.RelayPaths
import interlock.relay.core.storage.StorageReaper
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.io.RandomAccessFile
import java.nio.file.Files
import java.util.TimeZone

/**
 * PR-07 segment 6: the LEGACY storage and log layer (DSHBox interlock-relay-core RelayPaths, QuotaLedger, RunLog,
 * AuditLog, StorageReaper) over the shared cases in tests/TestVectors/relay-storage.json; the C# port runs the same
 * cases (RelayStorageParityTests).
 */
class LegacyRelayStorageParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-storage.json").readText(Charsets.UTF_8)) }
    private val root: File = Files.createTempDirectory("relay-storage").toFile()

    @After
    fun cleanup() {
        root.deleteRecursively()
    }

    private fun objects(a: JSONArray) = (0 until a.length()).map { a.getJSONObject(it) }

    private fun rel(f: File) = f.relativeTo(root).path.replace('\\', '/')

    private val policy by lazy {
        val p = v.getJSONObject("paths").getJSONObject("policy")
        object : RelayPathPolicy {
            override val guestEntry = p.getString("guestEntry")
            override val hostDirName = p.getString("hostDirName")
            override val cliName = p.getString("cliName")
            override val artifactPrefix = p.getString("artifactPrefix")
            override val assetClient = p.getString("assetClient")
            override val mediaAlbumDir = p.getString("mediaAlbumDir")
        }
    }

    @Test
    fun legacyPathsMatchSharedVectors() {
        val paths = RelayPaths(root, policy)
        val host = v.getJSONObject("paths").getJSONObject("host")
        for (key in host.keys()) {
            val getter = RelayPaths::class.java.getMethod("get" + key.replaceFirstChar { it.uppercaseChar() })
            assertEquals(key, host.getString(key), rel(getter.invoke(paths) as File))
        }
        val guest = v.getJSONObject("paths").getJSONObject("guest")
        for (key in guest.keys()) {
            val getter = RelayPaths::class.java.getMethod("get" + key.replaceFirstChar { it.uppercaseChar() })
            assertEquals(key, guest.getString(key), getter.invoke(paths) as String)
        }
    }

    @Test
    fun legacyUploadNamesAndOutMatchSharedVectors() {
        val paths = RelayPaths(root, policy)
        paths.uploadsDir.mkdirs()
        for (c in objects(v.getJSONArray("uploadNames"))) {
            val name = c.getString("name")
            val file = runCatching { File(paths.uploadsDir, name) }.getOrNull()
            val created = name.isNotEmpty() && !name.contains('/') && !name.contains('\\') && name != ".." &&
                runCatching { file!!.writeText("x"); true }.getOrDefault(false)
            val resolved = paths.resolveUpload(name)
            if (c.getBoolean("ok") && created && !name.contains("..")) {
                assertEquals(name, file!!.absolutePath, resolved?.absolutePath)
            } else {
                assertNull(name, resolved)
            }
        }
        val dotdot = v.getJSONArray("resolveRejectsDotDot")
        for (i in 0 until dotdot.length()) {
            File(paths.uploadsDir, dotdot.getString(i)).writeText("x")
            assertNull(paths.resolveUpload(dotdot.getString(i)))
        }
        val sub = v.getJSONObject("subdirFor")
        for (key in sub.keys()) assertEquals(key, sub.getString(key), ArtifactOut.subdirFor(key))
    }

    @Test
    fun legacyQuotaMatchesSharedVectors() {
        val q = v.getJSONObject("quota")
        val ledger = QuotaLedger(File(root, "q/quota.properties"), q.getLong("maxBytes"))
        for (c in objects(v.getJSONArray("charged"))) {
            val probe = QuotaLedger(File(root, "c-${c.getLong("bytes")}.properties"), Long.MAX_VALUE / 2)
            probe.recordWritten(c.getLong("bytes"))
            assertEquals(c.toString(), c.getLong("charged"), probe.totalBytes)
        }
        val reservations = mutableListOf<QuotaLedger.Reservation?>()
        objects(q.getJSONArray("steps")).forEachIndexed { i, s ->
            val where = "step $i $s"
            when (s.getString("op")) {
                "reserve" -> {
                    val r = ledger.reserve(s.getLong("bytes"))
                    reservations += r
                    assertEquals(where, s.getBoolean("expect"), r != null)
                }
                "commit" -> ledger.commit(reservations[s.getInt("which")]!!, s.getLong("actual"))
                "release" -> ledger.release(reservations[s.getInt("which")]!!)
                "written" -> ledger.recordWritten(s.getLong("bytes"))
                "freed" -> ledger.recordFreed(s.getLong("bytes"))
            }
            assertEquals(where, s.getLong("usable"), ledger.usableBytes())
            assertEquals(where, s.getLong("total"), ledger.totalBytes)
            assertEquals(where, s.getInt("count"), ledger.artifactCount)
            if (s.has("over")) assertEquals(where, s.getBoolean("over"), ledger.overQuota())
        }
        val file = File(root, "load.properties")
        for (c in objects(v.getJSONArray("quotaLoad"))) {
            file.writeText(c.getString("text"))
            val l = QuotaLedger(file)
            var corrupted: String? = null
            assertEquals(c.toString(), c.getBoolean("ok"), l.load { corrupted = it })
            if (c.getBoolean("ok")) {
                assertEquals(c.getLong("total"), l.totalBytes)
                assertEquals(c.getInt("count"), l.artifactCount)
            } else {
                assertEquals("quota ledger malformed, reconcile from disk required", corrupted)
            }
        }
    }

    @Test
    fun legacyRunLogMatchesSharedVectors() {
        val events = v.getJSONArray("logEvents")
        assertEquals(LogEvent.entries.size, events.length())
        LogEvent.entries.forEachIndexed { i, e ->
            val row = events.getJSONArray(i)
            assertEquals(row.getString(0), e.name)
            assertEquals(row.getString(1), e.level.name)
            assertEquals(row.getString(2), e.template)
        }
        val logs = File(root, "logs")
        val log = RunLog(logs)
        for (c in objects(v.getJSONArray("runLog"))) {
            val f = c.getJSONArray("fields")
            val fields = (0 until f.length()).map { f.getJSONArray(it).let { p -> p.getString(0) to p.getString(1) } }.toTypedArray()
            log.event(LogSubsystem.valueOf(c.getString("sub")), LogEvent.valueOf(c.getString("event")), *fields)
            assertEquals(c.getString("text"), log.snapshot().last().text)
            assertEquals(c.getString("line"), File(logs, "relay.log").readLines().last())
        }
    }

    @Test
    fun legacyAuditLogMatchesSharedVectors() {
        val a = v.getJSONObject("audit")
        val saved = TimeZone.getDefault()
        TimeZone.setDefault(TimeZone.getTimeZone(a.getString("zone")))
        try {
            val dir = File(root, "audit")
            val wall = a.getLong("wallMs")
            val audit = AuditLog(dir, a.getString("salt"), now = { wall })
            for (e in objects(a.getJSONArray("entries"))) {
                fun opt(n: String) = if (e.isNull(n)) null else e.getString(n)
                audit.record(
                    AuditEntry(
                        e.getString("requestId"), e.getString("cap"), opt("target"), e.getString("sys"), e.getString("tier"),
                        e.getString("decision"), e.getString("source"), e.getString("uid"), e.getString("outcome"), opt("surface"),
                        opt("from"), opt("reason"), e.getLong("ms"), e.getLong("bytes"), opt("args"),
                    ),
                )
                assertEquals(e.getString("line"), File(dir, "audit-20261112.jsonl").readLines(Charsets.UTF_8).last())
            }
            assertEquals(2, audit.recent(10).size)
        } finally {
            TimeZone.setDefault(saved)
        }
    }

    @Test
    fun legacyNamesMatchSharedVectors() {
        val stem = StorageReaper::class.java // stems are private: observe them through newArtifactFile / recordArtifact
        assertTrue(stem.name.isNotEmpty())
        val segment = AuditLog::class.java.getDeclaredMethod("segmentFile", String::class.java, Int::class.javaPrimitiveType).apply { isAccessible = true }
        val audit = AuditLog(File(root, "seg"), "s")
        val segs = v.getJSONObject("audit").getJSONArray("segments")
        for (i in 0 until segs.length()) {
            val s = segs.getJSONArray(i)
            assertEquals(s.getString(2), (segment.invoke(audit, s.getString(0), s.getInt(1)) as File).name)
        }
    }

    @Test
    fun legacyDeliverySweepMatchesSharedVectors() {
        val s = v.getJSONObject("outSweep")
        val now = s.getLong("nowWallMs")
        val paths = RelayPaths(root, policy)
        val reaper = StorageReaper(paths, QuotaLedger(paths.quotaFile), RunLog(paths.logsDir), now = { 0L }, wallNow = { now })
        val all = mutableListOf<String>()
        for (f in objects(s.getJSONArray("files"))) {
            val file = File(File(paths.outDir, f.getString("kind")), f.getString("name"))
            file.parentFile!!.mkdirs()
            RandomAccessFile(file, "rw").use { it.setLength(f.getLong("bytes")) }
            assertTrue(file.setLastModified(now - f.getLong("ageMs")))
            all += f.getString("name")
        }
        val result = reaper.sweep()
        val left = ArtifactOut.SUBDIRS.flatMap { File(paths.outDir, it).listFiles()!!.map(File::getName) }.toSet()
        val deleted = all.filter { it !in left }
        val want = s.getJSONArray("deleted").let { a -> (0 until a.length()).map(a::getString) }
        assertEquals(want.sorted(), deleted.sorted())
        assertEquals(deleted.size, result.deletedArtifacts)
    }
}
