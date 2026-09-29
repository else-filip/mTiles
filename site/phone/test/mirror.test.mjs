// The draft mirror's order of events, without a page or a clock: `node --test site/phone/test`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { DraftMirror, DRAFT_IN_THE_WAY } from "../mirror.js";

/** A connection whose requests wait until the test answers them. */
function fakeConnection() {
  const calls = [];
  return {
    calls,
    request(message) { return new Promise((resolve, reject) => calls.push({ message, resolve, reject })); },
  };
}

function manualTimers() {
  const timers = { due: null, set: (fn) => (timers.due = fn), clear: () => (timers.due = null) };
  return timers;
}

const settle = () => new Promise((r) => setImmediate(r));

function build({ open = () => true, capture }) {
  const conn = fakeConnection();
  const timers = manualTimers();
  const left = [];
  const mirror = new DraftMirror({ capture: () => capture(conn), isOpen: open, onLeft: (t) => left.push(t), timers });
  return { mirror, conn, timers, left };
}

const target = (conn, text, tileId = "a") => ({ tileId, machine: "m", conn, text, seen: null, unchanged: "" });

test("a mirror waits for the debounce, then sends the text", async () => {
  const { mirror, conn, timers } = build({ capture: (c) => target(c, "hi") });
  mirror.soon();
  assert.equal(conn.calls.length, 0);
  timers.due();
  assert.equal(conn.calls[0].message.text, "hi");
  conn.calls[0].resolve({ ok: true });
  await settle();
  assert.equal(mirror.mirrored, "hi");
});

test("a send cancels a mirror still waiting", () => {
  const { mirror, conn, timers } = build({ capture: (c) => target(c, "hi") });
  mirror.soon();
  mirror.cancel();
  assert.equal(timers.due, null);
  mirror.flush();
  assert.equal(conn.calls.length, 0);
});

test("leaving a tile flushes its waiting mirror, and where it lands is told to onLeft", async () => {
  let open = true;
  const { mirror, conn, left } = build({ open: () => open, capture: (c) => target(c, "bye") });
  mirror.soon();
  mirror.flush();
  open = false;
  conn.calls[0].resolve({ ok: true });
  await settle();
  assert.equal(left[0].text, "bye");
  assert.equal(mirror.mirrored, null);
});

test("a refusal stops the mirror until it is forgotten", async () => {
  const { mirror, conn, timers } = build({ capture: (c) => target(c, "x") });
  mirror.soon(); timers.due();
  conn.calls[0].reject(Object.assign(new Error("in the way"), { code: DRAFT_IN_THE_WAY }));
  await settle();
  assert.equal(mirror.refused, true);
  mirror.soon();
  assert.equal(timers.due, null);
  mirror.forget();
  mirror.soon();
  assert.notEqual(timers.due, null);
});

test("one at a time: a later text queued behind the one in flight is sent as seen by it", async () => {
  let text = "a";
  const { mirror, conn, timers } = build({ capture: (c) => target(c, text) });
  mirror.soon(); timers.due();
  assert.equal(mirror.holding("adopted"), "a");
  text = "ab";
  mirror.soon(); timers.due();
  assert.equal(conn.calls.length, 1);
  conn.calls[0].resolve({ ok: true });
  await settle();
  assert.equal(conn.calls[1].message.text, "ab");
  assert.equal(conn.calls[1].message.seen, "a");
});

test("a queued text for a refused tile is not sent", async () => {
  let text = "a";
  const { mirror, conn, timers } = build({ capture: (c) => target(c, text) });
  mirror.soon(); timers.due();
  text = "ab";
  mirror.soon(); timers.due();
  conn.calls[0].reject(Object.assign(new Error("no"), { code: DRAFT_IN_THE_WAY }));
  await settle();
  assert.equal(conn.calls.length, 1);
});

test("a failed request still sends the text queued behind it", async () => {
  const conn = fakeConnection();
  const mirror = new DraftMirror({ capture: () => null, isOpen: () => true, onError: () => {}, timers: manualTimers() });
  mirror.now(target(conn, "a"));
  mirror.now(target(conn, "ab"));
  conn.calls[0].reject(new Error("offline"));
  await settle();
  assert.equal(conn.calls[1]?.message.text, "ab");
});

test("a mirror still landing for a tile left behind is not what the open tile's box holds", async () => {
  let open = "a";
  let release;
  const conn = { request: () => new Promise((r) => { release = r; }) };
  const mirror = new DraftMirror({
    capture: () => ({ tileId: "a", machine: "m", conn, text: "from a", seen: null, unchanged: "" }),
    isOpen: (t) => t.tileId === open,
    timers: { set: () => 0, clear: () => {} },
  });
  const landing = mirror.now();
  assert.equal(mirror.holding("b's"), "from a");
  open = "b";
  assert.equal(mirror.inFlight, null);
  assert.equal(mirror.holding("b's"), "b's");
  release();
  await landing;
});
