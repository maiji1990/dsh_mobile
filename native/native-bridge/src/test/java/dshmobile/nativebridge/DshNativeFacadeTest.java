package dshmobile.nativebridge;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotEquals;
import static org.junit.Assert.assertTrue;

import org.json.JSONObject;
import org.junit.After;
import org.junit.Before;
import org.junit.Test;

import java.util.List;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

public class DshNativeFacadeTest {
    private DshNativeFacade facade;

    @Before
    public void setUp() {
        facade = new DshNativeFacade(new PlatformInfo(34, new String[] {"arm64-v8a"}, "TestDevice"));
        facade.initialize(null);
    }

    @After
    public void tearDown() {
        facade.close();
    }

    private static String req(String id, String method, String payloadJson) {
        return "{\"protocol\":1,\"requestId\":\"" + id + "\",\"method\":\"" + method
                + "\",\"deadlineMs\":5000,\"payload\":" + payloadJson + "}";
    }

    private static JSONObject call(DshNativeFacade f, String json) throws Exception {
        BlockingQueue<String> q = new LinkedBlockingQueue<>();
        f.execute(json, q::add);
        String r = q.poll(5, TimeUnit.SECONDS);
        if (r == null) {
            throw new AssertionError("no response");
        }
        return new JSONObject(r);
    }

    @Test
    public void bridgeInfo_reportsProtocolAndPlatform() throws Exception {
        JSONObject r = call(facade, req("1", "bridge.info", "{}"));
        assertTrue(r.getBoolean("ok"));
        assertEquals("OK", r.getString("code"));
        assertEquals("1", r.getString("requestId"));
        JSONObject p = r.getJSONObject("payload");
        assertEquals(1, p.getInt("protocol"));
        assertEquals(34, p.getInt("sdkInt"));
        assertEquals("arm64-v8a", p.getJSONArray("abis").getString(0));
        assertFalse(p.getString("kotlinVersion").isEmpty());
        assertTrue(p.getJSONObject("upstream").has("dshbox"));
    }

    @Test
    public void versionsCompare_usesDshboxKotlinSemantics() throws Exception {
        // Release is newer than its own prerelease; rc.10 is newer than rc.9 (numeric segment compare).
        JSONObject r1 = call(facade, req("a", "dshbox.versions.compare", "{\"a\":\"0.1.1\",\"b\":\"0.1.1-rc.2\"}"));
        assertEquals(1, r1.getJSONObject("payload").getInt("result"));
        JSONObject r2 = call(facade, req("b", "dshbox.versions.compare", "{\"a\":\"0.1.0-rc.9\",\"b\":\"0.1.0-rc.10\"}"));
        assertEquals(-1, r2.getJSONObject("payload").getInt("result"));
    }

    @Test
    public void versionsCompare_rejectsNonStringArgs() throws Exception {
        JSONObject r = call(facade, req("c", "dshbox.versions.compare", "{\"a\":1}"));
        assertFalse(r.getBoolean("ok"));
        assertEquals("BAD_REQUEST", r.getString("code"));
    }

    @Test
    public void unknownMethod_isMethodNotFound_notFakeSuccess() throws Exception {
        JSONObject r = call(facade, req("x", "runtime.start", "{}"));
        assertFalse(r.getBoolean("ok"));
        assertEquals("METHOD_NOT_FOUND", r.getString("code"));
    }

    @Test
    public void malformedRequests_areRejected() throws Exception {
        assertEquals("BAD_REQUEST", call(facade, "not json").getString("code"));
        assertEquals("BAD_REQUEST", call(facade, "{\"requestId\":\"1\",\"method\":\"bridge.ping\"}").getString("code"));
        assertEquals("PROTOCOL_MISMATCH", call(facade, "{\"protocol\":2,\"requestId\":\"1\",\"method\":\"bridge.ping\"}").getString("code"));
        assertEquals("BAD_REQUEST", call(facade, "{\"protocol\":1,\"requestId\":\"1\",\"method\":\"bridge.ping\",\"deadlineMs\":0}").getString("code"));
        assertEquals("BAD_REQUEST", call(facade, "{\"protocol\":1,\"requestId\":\"1\",\"method\":\"bridge.ping\",\"payload\":[]}").getString("code"));
    }

    @Test
    public void notInitialized_isReported() throws Exception {
        DshNativeFacade f = new DshNativeFacade(new PlatformInfo(34, null, null));
        try {
            assertEquals("NOT_INITIALIZED", call(f, req("1", "bridge.ping", "{}")).getString("code"));
        } finally {
            f.close();
        }
    }

    @Test
    public void duplicateInFlightRequestId_isRejected_andExecutedOnce() throws Exception {
        CountDownLatch gate = new CountDownLatch(1);
        // Block the single worker so the first request stays in flight.
        facade.execute(req("blocker", "bridge.ping", "{}"), json -> {
            try {
                gate.await(5, TimeUnit.SECONDS);
            } catch (InterruptedException ignored) {
            }
        });
        BlockingQueue<String> first = new LinkedBlockingQueue<>();
        BlockingQueue<String> second = new LinkedBlockingQueue<>();
        facade.execute(req("dup", "bridge.ping", "{}"), first::add);
        // Rejected synchronously at enqueue time (the first "dup" is in flight); the reply is still
        // delivered on the worker, so it arrives once the worker is released.
        facade.execute(req("dup", "bridge.ping", "{}"), second::add);
        gate.countDown();
        JSONObject firstResp = new JSONObject(first.poll(5, TimeUnit.SECONDS));
        JSONObject secondResp = new JSONObject(second.poll(5, TimeUnit.SECONDS));
        assertTrue(firstResp.getBoolean("ok"));
        assertEquals("DUPLICATE_REQUEST", secondResp.getString("code"));
        // blocker = seq 1, first "dup" = seq 2; the rejected duplicate must not have consumed a seq.
        assertEquals(2, firstResp.getJSONObject("payload").getLong("seq"));
        assertEquals(3, call(facade, req("next", "bridge.ping", "{}")).getJSONObject("payload").getLong("seq"));
    }

    @Test
    public void callbacks_neverOnCallerThread_andInFlightDrainsToZero() throws Exception {
        int n = 1000;
        CountDownLatch done = new CountDownLatch(n);
        List<String> wrongThread = new CopyOnWriteArrayList<>();
        Thread caller = Thread.currentThread();
        for (int i = 0; i < n; i++) {
            facade.execute(req("r" + i, "bridge.ping", "{\"i\":" + i + "}"), json -> {
                if (Thread.currentThread() == caller) {
                    wrongThread.add(json);
                }
                done.countDown();
            });
        }
        assertTrue(done.await(10, TimeUnit.SECONDS));
        assertTrue(wrongThread.isEmpty());
        // inFlight is cleared right after the callback returns.
        long until = System.currentTimeMillis() + 2000;
        while (facade.getInFlightCount() != 0 && System.currentTimeMillis() < until) {
            Thread.sleep(5);
        }
        assertEquals(0, facade.getInFlightCount());
    }

    @Test
    public void throwingCallback_doesNotKillWorker() throws Exception {
        facade.execute(req("boom", "bridge.ping", "{}"), json -> {
            throw new RuntimeException("managed side exploded");
        });
        assertTrue(call(facade, req("after", "bridge.ping", "{}")).getBoolean("ok"));
    }

    @Test
    public void afterClose_requestsGetClosed() throws Exception {
        facade.close();
        JSONObject r = call(facade, req("late", "bridge.ping", "{}"));
        assertEquals("CLOSED", r.getString("code"));
        assertEquals(0, facade.getInFlightCount());
    }

    @Test
    public void subscribe_receivesLifecycleEvents() throws Exception {
        DshNativeFacade f = new DshNativeFacade(new PlatformInfo(34, null, null));
        BlockingQueue<String> events = new LinkedBlockingQueue<>();
        f.subscribe(events::add);
        f.initialize(null);
        JSONObject e = new JSONObject(events.poll(5, TimeUnit.SECONDS));
        assertEquals("event", e.getString("type"));
        assertEquals("bridge.lifecycle", e.getString("event"));
        assertEquals("INITIALIZED", e.getJSONObject("payload").getString("state"));
        f.close();
        JSONObject closed = new JSONObject(events.poll(5, TimeUnit.SECONDS));
        assertEquals("CLOSED", closed.getJSONObject("payload").getString("state"));
    }

    @Test
    public void ping_sequenceIncreases() throws Exception {
        long s1 = call(facade, req("p1", "bridge.ping", "{}")).getJSONObject("payload").getLong("seq");
        long s2 = call(facade, req("p2", "bridge.ping", "{}")).getJSONObject("payload").getLong("seq");
        assertNotEquals(s1, s2);
        assertTrue(s2 > s1);
    }
}
