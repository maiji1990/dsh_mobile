package dshmobile.nativebridge

import android.content.Context
import android.content.ContextWrapper
import interlock.relay.core.exec.BackendCall
import interlock.relay.core.exec.BackendResult
import interlock.relay.core.exec.a11y.A11yBackend
import interlock.relay.core.exec.a11y.NodeSelector
import interlock.relay.core.exec.a11y.NodeView
import interlock.relay.core.exec.a11y.Selector
import interlock.relay.core.exec.a11y.SelectorSpec
import interlock.relay.core.log.RunLog
import interlock.relay.core.protocol.CapabilityRegistry
import interlock.relay.core.protocol.SurfaceKind
import interlock.relay.core.spi.RelayPathPolicy
import interlock.relay.core.storage.QuotaLedger
import interlock.relay.core.storage.RelayPaths
import interlock.relay.core.storage.StorageReaper
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.lang.reflect.Method
import java.nio.file.Files

/**
 * PR-07 step 5: the LEGACY accessibility pure logic (DSHBox interlock-relay-core exec/a11y NodeSelector,
 * RelativeGeometry, TrustedNodeTree, staleNodeIdReason and the A11yBackend argument checks) over
 * tests/TestVectors/relay-a11y.json; the C# port runs the same vectors (RelayA11yParityTests). Internal members are
 * reached by reflection. csharpOnly cases (libcore org.json semantics) and the Rect-based bounds are skipped here.
 */
class LegacyRelayA11yParityTest {
    private fun repo(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "global.json").isFile) dir = dir.parentFile
        return checkNotNull(dir) { "repo root (global.json) not found" }
    }

    private val v by lazy { JSONObject(File(repo(), "tests/TestVectors/relay-a11y.json").readText(Charsets.UTF_8)) }

    private fun objects(a: JSONArray) = (0 until a.length()).map { a.getJSONObject(it) }

    private fun str(o: JSONObject, name: String): String? = if (!o.has(name) || o.isNull(name)) null else o.getString(name)

    private fun method(cls: Class<*>, name: String, arity: Int): Method =
        cls.declaredMethods.first { (it.name == name || it.name.startsWith("$name$")) && it.parameterCount == arity }
            .also { it.isAccessible = true }

    private class FakeNode(
        val key: String,
        override val text: String?,
        override val contentDescription: String?,
        override val viewId: String?,
        override val className: String?,
        override val packageName: String?,
        override val siblingIndex: Int,
    ) : NodeView {
        val kids = mutableListOf<NodeView>()
        override val children: List<NodeView> get() = kids
    }

    private fun build(n: JSONObject, sibling: Int): FakeNode {
        val node = FakeNode(n.getString("k"), str(n, "t"), str(n, "d"), str(n, "id"), str(n, "cls"), str(n, "pkg"), sibling)
        val c = n.getJSONArray("children")
        for (i in 0 until c.length()) node.kids += build(c.getJSONObject(i), i)
        return node
    }

    private val trees by lazy {
        val t = v.getJSONObject("trees")
        val map = t.keys().asSequence().associateWith { build(t.getJSONObject(it), 0) }.toMutableMap()
        val gen = v.getJSONObject("generatedTrees")
        var deep: FakeNode? = null
        for (i in gen.getInt("deep") - 1 downTo 0) {
            val n = FakeNode("d$i", "d$i", null, null, null, "com.app", 0)
            deep?.let { n.kids += it }
            deep = n
        }
        map["deep"] = deep!!
        val wide = FakeNode("wroot", null, null, null, null, "com.app", 0)
        for (i in 0 until gen.getInt("wide")) wide.kids += FakeNode("w$i", "w$i", null, null, null, "com.app", i)
        map["wide"] = wide
        map
    }

    private fun sel(selector: JSONObject): Selector =
        (NodeSelector.parse(JSONObject().put("selector", selector)) as SelectorSpec.Parsed).selector

    @Test
    fun legacyParseMatchesSharedVectors() {
        val cases = objects(v.getJSONArray("parse"))
        assertTrue(cases.size > 40)
        for (c in cases) {
            if (c.optBoolean("csharpOnly")) continue
            val got = NodeSelector.parse(c.getJSONObject("args"))
            val want = c.getJSONObject("expect")
            val where = c.getJSONObject("args").toString()
            when (want.getString("kind")) {
                "invalid" -> assertEquals(where, want.getString("reason"), (got as? SelectorSpec.Invalid)?.reason)
                "node" -> assertEquals(where, want.getInt("nodeId"), (got as? SelectorSpec.ByNodeId)?.nodeId)
                else -> {
                    val s = want.getJSONObject("selector")
                    val expected = Selector(
                        text = str(s, "text"), contentDescription = str(s, "desc"), viewId = str(s, "id"),
                        className = str(s, "class"), index = if (s.has("index")) s.getInt("index") else null, packageHint = str(s, "package"),
                    )
                    assertEquals(where, expected, (got as? SelectorSpec.Parsed)?.selector)
                }
            }
        }
    }

    @Test
    fun legacyFindMatchesSharedVectors() {
        for (c in objects(v.getJSONArray("find"))) {
            val root = trees.getValue(c.getString("tree"))
            val limit = if (c.has("limit")) c.getInt("limit") else NodeSelector.DEFAULT_MATCH_LIMIT
            val got = NodeSelector.findAll(root, sel(c.getJSONObject("selector")), limit, c.getBoolean("allowContains"))
                .map { "${it.nodeId}/${it.depth}/${it.mode.name}" }
            val e = c.getJSONArray("expect")
            val want = (0 until e.length()).map { e.getJSONArray(it).let { r -> "${r.getInt(0)}/${r.getInt(1)}/${r.getString(2)}" } }
            assertEquals(c.toString(), want, got)
        }
        for (c in objects(v.getJSONArray("findById"))) {
            val node = NodeSelector.findById(trees.getValue(c.getString("tree")), c.getInt("nodeId")) as FakeNode?
            assertEquals(c.toString(), str(c, "expect"), node?.key)
        }
    }

    private val geo by lazy { Class.forName("interlock.relay.core.exec.a11y.RelativeGeometry") }
    private val boxClass by lazy { Class.forName("interlock.relay.core.exec.a11y.RelativeGeometry\$Box") }
    private val sideClass by lazy { Class.forName("interlock.relay.core.exec.a11y.RelativeGeometry\$Side") }

    private fun box(b: JSONArray): Any = boxClass.declaredConstructors.single { it.parameterCount == 6 }
        .newInstance(b.getInt(0), b.getInt(1), b.getInt(2), b.getInt(3), b.getInt(4), if (b.isNull(5)) null else b.getString(5))

    private fun get(o: Any, getter: String): Any? = o.javaClass.getMethod(getter).invoke(o)

    @Test
    fun legacyGeometryMatchesSharedVectors() {
        val instance = geo.getField("INSTANCE").get(null)
        val pick = method(geo, "pick", 5)
        val sideOf = method(geo, "sideOf", 1)
        for (c in objects(v.getJSONArray("geometry"))) {
            val side = sideClass.enumConstants.single { (it as Enum<*>).name == c.getString("side") }
            val cands = c.getJSONArray("candidates").let { a -> (0 until a.length()).map { box(a.getJSONArray(it)) } }
            val outcome = pick.invoke(instance, box(c.getJSONArray("anchor")), cands, side, c.getInt("nth"), c.getBoolean("sameClass"))!!
            val want = c.getJSONObject("expect")
            when (want.getString("kind")) {
                "pick" -> {
                    assertEquals(c.toString(), "Pick", outcome.javaClass.simpleName)
                    assertEquals(c.toString(), want.getInt("ref"), get(get(outcome, "getBox")!!, "getRef"))
                    assertEquals(c.toString(), want.getInt("gap"), get(outcome, "getGapPx"))
                }
                "none" -> assertEquals(c.toString(), "None", outcome.javaClass.simpleName)
                else -> {
                    assertEquals(c.toString(), "Ambiguous", outcome.javaClass.simpleName)
                    val refs = (get(outcome, "getBoxes") as List<*>).map { get(it!!, "getRef") }
                    val r = want.getJSONArray("refs")
                    assertEquals(c.toString(), (0 until r.length()).map { r.getInt(it) }, refs)
                    assertEquals(c.toString(), want.getInt("gap"), get(outcome, "getGapPx"))
                }
            }
        }
        val sides = v.getJSONArray("sideOf")
        for (i in 0 until sides.length()) {
            val c = sides.getJSONArray(i)
            assertEquals(c.toString(), if (c.isNull(1)) null else c.getString(1), (sideOf.invoke(instance, c.getString(0)) as Enum<*>?)?.name)
        }
    }

    @Test
    fun legacyTrustedTreeAndStaleReasonMatchSharedVectors() {
        val cls = Class.forName("interlock.relay.core.exec.a11y.TrustedNodeTree")
        val tree = cls.getDeclaredConstructor().also { it.isAccessible = true }.newInstance()
        val observe = method(cls, "observe", 2)
        val state = method(cls, "state", 1)
        for (s in objects(v.getJSONArray("trusted"))) {
            if (s.getString("op") == "observe") observe.invoke(tree, s.getInt("displayId"), s.getBoolean("found"))
            else assertEquals(s.toString(), s.getString("expect"), state.invoke(tree, s.getInt("alive")))
        }
        val stale = method(Class.forName("interlock.relay.core.exec.a11y.NodeActionsKt"), "staleNodeIdReason", 4)
        for (c in objects(v.getJSONArray("stale"))) {
            assertEquals(
                c.toString(), str(c, "expect"),
                stale.invoke(null, c.getBoolean("askedByNodeId"), c.getBoolean("visible"), c.getBoolean("area"), c.getInt("displayId")),
            )
        }
    }

    private class FakeContext : ContextWrapper(null) {
        override fun getApplicationContext(): Context = this
        override fun getPackageName(): String = "org.example.dshmobile"
    }

    private val policy = object : RelayPathPolicy {
        override val guestEntry = "/opt/pilot"
        override val hostDirName = "pilot"
        override val cliName = "relay"
        override val artifactPrefix = "relay-"
        override val assetClient = "relay/relay-client.cjs"
        override val mediaAlbumDir = "DSH Mobile"
    }

    @Test
    fun legacyArgumentChecksMatchSharedVectors() {
        val root = Files.createTempDirectory("relay-a11y").toFile()
        val paths = RelayPaths(root, policy)
        paths.ensureWritableDirs()
        val runLog = RunLog(paths.logsDir)
        val reaper = StorageReaper(paths, QuotaLedger(paths.quotaFile), runLog)
        val backend = A11yBackend(FakeContext(), reaper)
        val argsError = method(A11yBackend::class.java, "argsError", 1)
        for (c in objects(v.getJSONArray("argsError"))) {
            if (c.optBoolean("csharpOnly")) continue
            val call = BackendCall(
                "r1", checkNotNull(CapabilityRegistry.find(c.getString("capability"))), c.getJSONObject("args"),
                SurfaceKind.FOREGROUND, paths, 10_000,
            )
            val got = argsError.invoke(backend, call) as BackendResult.Failed?
            assertEquals(c.toString(), str(c, "expect"), got?.reason)
        }
        fun strings(a: JSONArray): LinkedHashSet<String> = (0 until a.length()).mapTo(LinkedHashSet()) { a.getString(it) }
        val badArgShape = method(A11yBackend::class.java, "badArgShape", 3)
        for (c in objects(v.getJSONArray("badArgShape"))) {
            assertEquals(c.toString(), str(c, "expect"), badArgShape.invoke(backend, c.getJSONObject("args"), strings(c.getJSONArray("required")), strings(c.getJSONArray("allowed"))))
        }
        val badCoordinate = method(A11yBackend::class.java, "badCoordinate", 2)
        for (c in objects(v.getJSONArray("badCoordinate"))) {
            val l = c.getJSONArray("limits")
            val limits = LinkedHashMap<String, Int>().apply { for (i in 0 until l.length()) put(l.getJSONArray(i).getString(0), l.getJSONArray(i).getInt(1)) }
            assertEquals(c.toString(), str(c, "expect"), badCoordinate.invoke(backend, c.getJSONObject("args"), limits))
        }
        root.deleteRecursively()
    }
}
