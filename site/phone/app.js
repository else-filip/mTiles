// The phone page: one mTiles, seen at three distances.
//
//   workspaces  — every workspace, with the mark that says whether anything in it wants you
//   layout      — one workspace as the desktop lays it out, drawn to scale; tap a tile to zoom in
//   tile        — one tile: the conversation or the screen, what it is waiting for, and a dock to answer
//
// Going up a level is the back button, the header's back arrow, or a pinch. Everything shown is what
// mTiles pushed or answered; nothing here decides what a tile is allowed to do.

import { h, fill, activityMark, icon } from "./dom.js";
import { parse, render } from "./markdown.js";
import { layoutRects, isLegible, readingOrder } from "./geometry.js";
import { Connection, knownMachines, lastMachineId, setLastMachine, takeInvitationFromUrl, parseInvitation, forgetMachine, PROTOCOL } from "./connection.js";
import { Dictation } from "./dictation.js";
import { ago } from "./format.js";
import { isUp, attentionOf, computerStatus } from "./computers.js";
import { orderWorkspaces, matchesQuery, FILTER_THRESHOLD } from "./workspaces.js";

const KINDS = {
  terminal: "Terminal", agent: "Terminal agent", "agent-conversation": "Agent", goal: "Goal",
  note: "Note", todo: "Todo", git: "Git", database: "Database", usage: "Usage",
};

const KIND_ACCENT = {
  terminal: "terminal", agent: "agent", "agent-conversation": "agent", goal: "goal", note: "note",
  todo: "todo", git: "git", database: "database", usage: "usage",
};

const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");

const state = {
  layout: null,           // RemoteLayout of state.workspaceId, on the computer on screen
  tile: null,             // RemoteTileView of state.tileId
  level: "workspaces",    // computers | workspaces | layout | tile
  workspaceId: null,
  tileId: null,
  keysOpen: false,
  actionsOpen: false,
  answers: {},            // pendingId -> { questionId -> [labels] }
  answerChips: 0,         // moves when a chip is toggled — typing does not redraw, or the field loses focus
  sending: false,
};

// ── computers ─────────────────────────────────────────────────────────────────────────────────
//
// Every computer this phone is paired with is connected at once, not only the one on screen: that is
// what lets the list of computers say which one needs you, and what makes switching to one instant. Each
// holds its own link and its own picture of its workspaces; `state.conn`, `state.workspaces` and the rest
// of the fields below always mean the computer on screen.

/** id -> { id, name, conn, connState, hello, session, workspaces, clockOffset, error } */
const machines = new Map();
let currentId = null;

const MACHINE_DEFAULTS = {
  conn: null,
  connState: "unpaired",  // unpaired | connecting | connected | reconnecting | refused | failed | page-old | app-old
  hello: null,
  session: null,          // { dictation: { available, state, tileId }, activeTileId }
  workspaces: [],
  clockOffset: 0,         // mTiles' clock less this phone's, from the hello — cards' times are mTiles'
  error: null,
};

for (const key of Object.keys(MACHINE_DEFAULTS)) {
  Object.defineProperty(state, key, {
    get: () => machines.get(currentId)?.[key] ?? MACHINE_DEFAULTS[key],
    set: (value) => { const m = machines.get(currentId); if (m) m[key] = value; },
  });
}
Object.defineProperty(state, "machine", { get: () => machines.get(currentId) ?? null });

function machineFor(id) {
  let m = machines.get(id);
  if (!m) {
    const known = knownMachines().find((k) => k.id === id);
    m = { id, name: known?.name ?? "mTiles", ...MACHINE_DEFAULTS, workspaces: [] };
    machines.set(id, m);
  }
  return m;
}

const isCurrent = (m) => m.id === currentId;

/** Whether a computer other than the one on screen has something waiting for you. */
const elsewhereNeedsYou = () => [...machines.values()].some((m) => !isCurrent(m) && attentionOf(m).blocked > 0);

const root = document.getElementById("app");
const bar = h("header.bar");
const view = h("main.view");
const dock = h("footer.dock");
const toast = h("div.toast", { role: "status", "aria-live": "polite" });
root.append(bar, view, dock, toast);

const dictation = new Dictation({ getLink: () => state.conn?.link, onError: (m) => say(m, "error") });

// ── connection ────────────────────────────────────────────────────────────────────────────────

async function start() {
  const invitation = takeInvitationFromUrl();
  const known = knownMachines();
  const first = invitation?.id ?? lastMachineId() ?? known.at(-1)?.id;

  if (!first) {
    draw();
    return;
  }

  // Back from a computer's workspaces is the list of computers, so that is where the history starts.
  currentId = first;
  machineFor(first);
  history.replaceState({ level: "computers" }, "");
  go("workspaces", { machineId: first });

  connect(first, invitation?.code ?? null);
  for (const m of known) {
    if (m.id !== first) connect(m.id, null);
  }
}

async function connect(id, code) {
  const m = machineFor(id);
  // The new link is this computer's before anything is awaited, so a second call cannot leave two alive,
  // and a link that has been replaced is not listened to any more.
  const previous = m.conn;
  const conn = new Connection({
    onPush: (message) => { if (m.conn === conn) onPush(m, message); },
    onState: (next) => { if (m.conn === conn) onConnectionState(m, next); },
  });
  m.conn = conn;
  await previous?.close();
  if (m.conn !== conn) return;
  m.workspaces = [];
  m.connState = "connecting";
  m.error = null;
  if (isCurrent(m)) { state.layout = null; state.tile = null; }
  draw();

  try {
    const hello = await conn.open({ id, code });
    if (m.conn !== conn) return; // replaced meanwhile — a retry, or a log-out
    m.hello = hello;
    m.name = hello.machine ?? m.name;
    if (isCurrent(m)) setLastMachine(id);
    if (!hello.compatible) {
      m.connState = hello.protocol > PROTOCOL ? "page-old" : "app-old";
      draw();
      return;
    }
    m.session = hello.session ?? null;
    m.clockOffset = typeof hello.now === "number" ? hello.now - Date.now() : 0;
    const { workspaces } = await conn.request({ type: "workspaces" });
    m.workspaces = workspaces;
    m.connState = "connected";
    // mTiles remembers what a phone watched across a reconnect, so a computer in the background is told
    // outright to push nothing but its list.
    if (isCurrent(m)) await watch();
    else watchNothing(m);
  } catch (error) {
    if (m.conn !== conn) return;
    // Only a join that never succeeded is abandoned: once the relay has joined, mTiles holds this phone as
    // paired and the stored identity is what "Try again" reconnects with.
    if (code && !conn.link && !isPaired(id)) { abandonPairing(m, error.message); return; }
    m.connState = /no longer paired|not been paired/.test(error.message) ? "refused" : "failed";
    m.error = error.message;
  }
  draw();
}

/** Out of reach and not trying: connecting again is offered only then, so a pairing still under way is
 *  not replaced by a join without its code. */
const isDown = (m) => !isUp(m) && m.connState !== "connecting";

const isPaired = (id) => knownMachines().some((k) => k.id === id);

/** A first pairing that failed leaves nothing behind: without a pairing there is nothing to retry, and
 *  the code it came with is spent or wrong. */
function abandonPairing(m, reason) {
  const conn = m.conn;
  m.conn = null;
  conn?.close();
  machines.delete(m.id);
  say(`Pairing failed: ${reason}`, "error");
  // Somebody who has moved on to another computer meanwhile is left where they are.
  if (!isCurrent(m)) { draw(); return; }
  putNextOnScreen();
  go(currentId ? "computers" : "workspaces");
}

function onPush(m, message) {
  if (!isCurrent(m)) {
    // A computer in the background is listened to only for what the list of computers shows.
    const before = elsewhereNeedsYou();
    if (message.type === "session") m.session = message;
    else if (message.type === "workspaces") m.workspaces = message.workspaces ?? [];
    else { sayFrom(m, message); return; }
    if (state.level === "computers" || before !== elsewhereNeedsYou()) draw();
    return;
  }

  switch (message.type) {
    case "session": m.session = message; break;
    case "workspaces": m.workspaces = message.workspaces ?? []; break;
    case "layout":
      if (message.layout?.workspaceId === state.workspaceId) state.layout = message.layout;
      break;
    case "tile":
      if (message.tile?.tileId === state.tileId) state.tile = message.tile;
      break;
    case "tileGone":
      if (message.tileId === state.tileId) { say("That tile was closed on the computer."); go("layout"); return; }
      break;
    case "text":
    case "error":
      sayFrom(m, message);
      break;
    default:
      return;
  }
  draw();
}

/** What a computer says back — a dictated sentence heard, or an error — is said whichever computer is on
 *  screen, named when it is not the one on screen, so a sentence sent before switching is not lost. */
function sayFrom(m, message) {
  if (message.type !== "text" && message.type !== "error") return;
  const from = isCurrent(m) ? "" : `${m.name}: `;
  if (message.type === "text") say(`${from}Heard: ${message.message}`);
  else say(`${from}${message.message}`, "error");
}

/** A link back after a drop meets an mTiles that may have restarted and forgotten what this page watches. */
function onConnectionState(m, next) {
  // While the hello and the first list are on their way, "connected" is `connect`'s to say.
  if (next === "connected" && m.connState === "connecting") return;
  const reconnected = m.connState === "reconnecting" && next === "connected";
  m.connState = next;
  draw();
  if (!reconnected) return;
  if (isCurrent(m)) watch();
  else watchNothing(m);
}

/** Tells a computer in the background to push no layout and no tile: only its list is listened to. */
function watchNothing(m) {
  m.conn?.request({ type: "watch", workspaceId: null, tileId: null }).catch(() => {});
}

/** Tells mTiles what this page is looking at, so that is what it pushes. */
async function watch() {
  if (!state.conn || state.connState !== "connected") return;
  const workspaceId = depth(state.level) <= depth("workspaces") ? null : state.workspaceId;
  const tileId = state.level === "tile" ? state.tileId : null;
  try { await state.conn.request({ type: "watch", workspaceId, tileId }); } catch { /* the next push corrects it */ }
}

async function ask(message) {
  try {
    return await state.conn.request(message);
  } catch (error) {
    say(error.message, "error");
    return null;
  }
}

// ── navigation ────────────────────────────────────────────────────────────────────────────────

/** Moves to a level. `from` is the element the zoom grows out of, when there is one. */
function go(level, { machineId, workspaceId, tileId, from } = {}, push = true) {
  const before = state.level;
  // The list of computers belongs to none of them, so reaching it — Back included — keeps the current one.
  if (level !== "computers" && machineId && machineId !== currentId && machines.has(machineId)) switchTo(machineId);
  state.level = level;
  if (workspaceId !== undefined) state.workspaceId = workspaceId;
  if (tileId !== undefined) state.tileId = tileId;
  if (level !== "tile") { state.tile = null; state.keysOpen = false; state.actionsOpen = false; lastTileKey = lastDockKey = null; }
  if (depth(level) <= depth("workspaces")) state.layout = null;

  if (push) history.pushState({ level, machineId: currentId, workspaceId: state.workspaceId, tileId: state.tileId }, "");

  const origin = from?.getBoundingClientRect();
  draw();
  zoom(origin, depth(level) > depth(before));
  watch();

  // An answer is kept only if the page is still looking at what it asked about, on the same computer —
  // the same test a push passes.
  const askedOf = currentId;
  const stillWanted = (wantedLevel) => currentId === askedOf && state.level === wantedLevel;
  if (level === "layout" && !state.layout) ask({ type: "layout", workspaceId: state.workspaceId }).then((a) => {
    if (a?.layout && stillWanted("layout") && a.layout.workspaceId === state.workspaceId) { state.layout = a.layout; draw(); }
  });
  if (level === "tile" && !state.tile) ask({ type: "tile", tileId: state.tileId }).then((a) => {
    if (a?.tile && stillWanted("tile") && a.tile.tileId === state.tileId) { state.tile = a.tile; draw(); scrollToEnd(true); }
  });
}

const depth = (level) => ({ computers: 0, workspaces: 1, layout: 2, tile: 3 })[level] ?? 1;

/** Puts another computer on screen. The one left stops being asked for its layout and tiles — it goes on
 *  telling the list of computers what it is doing. */
function switchTo(id) {
  const leaving = machines.get(currentId);
  if (leaving?.connState === "connected") watchNothing(leaving);
  currentId = id;
  // Remembered only once paired: a pairing cut short by a reload must not be what the page opens on.
  if (id && isPaired(id)) setLastMachine(id);
  state.layout = null;
  state.tile = null;
  state.workspaceId = null;
  state.tileId = null;
  lastTileKey = lastDockKey = null;
  wsQuery = "";
  wsPage = null;
}

/** After the computer on screen is gone — logged out of, or a pairing that failed — the last paired one
 *  takes its place, with nothing of the one that left carried over. */
function putNextOnScreen() {
  switchTo(knownMachines().at(-1)?.id ?? null);
}

addEventListener("popstate", (event) => {
  const s = event.state ?? { level: "computers" };
  // An entry of a computer since logged out of names workspaces and tiles no other computer has.
  if (s.machineId && !machines.has(s.machineId)) { go("computers", {}, false); return; }
  go(s.level, { machineId: s.machineId, workspaceId: s.workspaceId ?? null, tileId: s.tileId ?? null }, false);
});

function up() {
  if (state.level === "computers") return;
  history.back();
}

/** The one motion on this page: the thing tapped grows into the screen, and shrinks back into its
 *  place going up. Answers a gesture, so it is kept even short; skipped when motion is reduced. */
function zoom(origin, inward) {
  if (reducedMotion.matches) return;
  const target = view.getBoundingClientRect();
  if (inward && origin && origin.width > 0) {
    const sx = origin.width / target.width;
    const sy = origin.height / target.height;
    view.animate(
      [
        { transformOrigin: "0 0", transform: `translate(${origin.left - target.left}px, ${origin.top - target.top}px) scale(${sx}, ${sy})`, opacity: 0.4 },
        { transformOrigin: "0 0", transform: "none", opacity: 1 },
      ],
      { duration: 260, easing: "cubic-bezier(.2,.7,.2,1)" },
    );
  } else {
    view.animate([{ transform: "scale(1.06)", opacity: 0.3 }, { transform: "none", opacity: 1 }],
      { duration: 200, easing: "ease-out" });
  }
}

// Pinching in — the gesture for seeing more — goes up a level.
let pinch = null;
view.addEventListener("touchstart", (e) => {
  if (e.touches.length === 2) pinch = spread(e.touches);
}, { passive: true });
view.addEventListener("touchmove", (e) => {
  if (pinch && e.touches.length === 2 && spread(e.touches) < pinch * 0.7) { pinch = null; up(); }
}, { passive: true });
view.addEventListener("touchend", () => { pinch = null; }, { passive: true });
const spread = (t) => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);

// ── drawing ───────────────────────────────────────────────────────────────────────────────────

function draw() {
  document.body.dataset.level = state.level;
  document.body.dataset.conn = state.connState;

  if (machines.size === 0) {
    fill(bar, h("div.title", h("span.name", "mTiles")));
    fill(view, pairingScreen());
    fill(dock);
    lastTileKey = lastDockKey = null;
    return;
  }

  if (state.level === "computers") {
    drawComputers();
    return;
  }

  if (state.connState !== "connected" && state.connState !== "reconnecting") {
    header({ back: "All computers", title: state.machine?.name ?? "mTiles" });
    fill(view, statusScreen());
    fill(dock);
    lastTileKey = lastDockKey = null;
    wsPage = null;
    return;
  }

  switch (state.level) {
    case "layout": drawLayout(); break;
    case "tile": drawTile(); break;
    default: drawWorkspaces(); break;
  }
}

function statusScreen() {
  const screens = {
    connecting: ["Connecting…", "Reaching mTiles through the relay. This takes a few seconds."],
    refused: ["This phone is not paired any more",
      "It was unpaired on the computer, or the code had already been used. Scan a new code from mTiles."],
    failed: ["mTiles did not answer", state.error ?? "The computer may be off, asleep, or offline."],
    "page-old": ["This page is older than mTiles", "Reload the page to get the current version."],
    "app-old": ["mTiles on the computer needs updating", "This page speaks a newer protocol than that copy of mTiles. Update mTiles, then reload."],
  };
  const [title, text] = screens[state.connState] ?? screens.connecting;
  const m = state.machine;

  return h("section.status-screen",
    state.connState === "connecting" ? activityMark("working") : null,
    h("h1", title),
    h("p", text),
    state.connState === "failed"
      ? h("button.primary", { onclick: () => connect(m.id, null) }, "Try again") : null,
    state.connState === "failed" || state.connState === "refused"
      ? h("button", { onclick: () => logOut(m) }, "Log out of this computer") : null);
}

/** The first screen, and the one an unpaired phone stays on. */
function pairingScreen() {
  return h("section.status-screen",
    h("h1", "Pair this phone with mTiles"),
    h("p", "On the computer, open mTiles and press the QR button beside Settings. Scan the code with this phone's camera."),
    pasteLinkForm());
}

/** The route for a phone that cannot open the camera's link here — a page added to the home screen
 *  opens it in the browser instead. The link is the one under the QR code in mTiles. */
function pasteLinkForm(onDone) {
  const input = h("input.q-custom", {
    type: "url", placeholder: "Or paste the pairing link", autocomplete: "off", spellcheck: false,
    enterKeyHint: "go",
  });
  const pair = () => {
    const invitation = parseInvitation(input.value.trim());
    if (!invitation) { say("That is not a pairing link from mTiles.", "error"); return; }
    onDone?.();
    pairWith(invitation);
  };
  input.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); pair(); } });
  return h("div.paste-link", input, h("button", { onclick: pair }, "Pair"));
}

/** Pairs with a computer from a code, and puts it on screen. */
function pairWith(invitation) {
  machineFor(invitation.id);
  go("workspaces", { machineId: invitation.id });
  connect(invitation.id, invitation.code);
}

function header({ back, title, sub, marks }) {
  // Another computer waiting for you is said on every screen but the list that already says it, and
  // leads there.
  const elsewhere = state.level !== "computers" && elsewhereNeedsYou()
    ? h("button.elsewhere", {
      onclick: () => go("computers"), "aria-label": "Another computer needs you", title: "Another computer needs you",
    }, activityMark("blocked"))
    : null;
  fill(bar,
    back ? h("button.back", { onclick: up, "aria-label": back }, icon("back")) : null,
    h("div.title", h("span.name", title), sub ? h("span.sub", sub) : null),
    elsewhere,
    marks ?? null,
    state.level !== "computers" && state.connState === "reconnecting"
      ? h("span.conn", { title: "Reconnecting" }, activityMark("working")) : null);
}

// computers

function drawComputers() {
  header({ title: "Computers" });

  // In the order they were paired, never by urgency: a row that jumps while you reach for it is worse
  // than one you have to look for, and the mark on it already says which one wants you.
  // A pairing still under way — or one whose hello never came back — is not stored yet, and is listed
  // after the stored ones so it can still be retried or logged out of.
  const stored = knownMachines().map(({ id }) => machineFor(id));
  const pending = [...machines.values()].filter((m) => !stored.includes(m));
  const rows = [...stored, ...pending].map((m) => {
    const attention = attentionOf(m);
    const down = isDown(m);
    return h("div.pc-row", { class: [isCurrent(m) && "current", down && "down", attention.blocked && "blocked"].filter(Boolean).join(" ") },
      h("button.pc-open", {
        onclick: (e) => {
          if (down) connect(m.id, null);
          go("workspaces", { machineId: m.id, from: e.currentTarget });
        },
      },
        h("span.ws-text",
          h("span.ws-name", m.name),
          h("span.ws-meta", computerStatus(m))),
        m.connState === "connecting" ? activityMark("working") : activityMark(attention.activity)),
      h("button.pc-more", { onclick: () => computerSheet(m), "aria-label": `More for ${m.name}` }, icon("more")));
  });

  fill(view, h("div.pc-page",
    h("nav.ws-list", rows),
    h("button.pc-add", { onclick: addComputerSheet }, icon("plus"), "Add a computer")));
  fill(dock);
  lastDockKey = null;
  wsPage = null;
}

function sheet(...children) {
  root.querySelector(".sheet")?.remove();
  const el = h("div.sheet", children, h("button.quiet", { onclick: () => el.remove() }, "Close"));
  root.append(el);
  return el;
}

function computerSheet(m) {
  const el = sheet(
    h("p.sheet-title", m.name),
    isDown(m) ? h("button", { onclick: () => { el.remove(); connect(m.id, null); } }, "Connect again") : null,
    h("button.danger", { onclick: () => { el.remove(); logOut(m); } }, "Log out of this computer"));
}

function addComputerSheet() {
  const el = sheet(
    h("p.sheet-title", "Add a computer"),
    h("p.sheet-text", "On the other computer, open mTiles and press the QR button beside Settings. Scan the code with this phone's camera — it opens here, beside the computers already paired."),
    pasteLinkForm(() => el.remove()));
}

// workspaces

let wsQuery = "";
let wsPage = null;        // the filter and the list under it, kept so typing never rebuilds the field

function drawWorkspaces() {
  header({ back: "All computers", title: state.machine?.name ?? "mTiles" });

  // Built once and refilled: a push arriving while somebody types must not take the keyboard away.
  if (!wsPage || !view.contains(wsPage.root)) {
    const input = h("input.ws-filter", {
      type: "search", placeholder: "Filter workspaces", autocomplete: "off", spellcheck: false,
      enterKeyHint: "search", value: wsQuery,
      oninput: (e) => { wsQuery = e.target.value; drawWorkspaceRows(); },
    });
    const list = h("nav.ws-list");
    wsPage = { root: h("div.ws-page", input, list), input, list };
    fill(view, wsPage.root);
  }

  const showFilter = state.workspaces.length > FILTER_THRESHOLD;
  wsPage.input.hidden = !showFilter;
  if (!showFilter && wsQuery) { wsQuery = ""; wsPage.input.value = ""; }
  drawWorkspaceRows();
  fill(dock);
  lastDockKey = null;
}

function drawWorkspaceRows() {
  const all = orderWorkspaces(state.workspaces);
  const shown = all.filter((ws) => matchesQuery(ws, wsQuery));

  const rows = shown.map((ws) =>
    h("button.ws-row", {
      class: [ws.current && "current", !ws.loaded && "unloaded"].filter(Boolean).join(" "),
      onclick: (e) => openWorkspace(ws, e.currentTarget),
    },
      h("span.ws-text",
        h("span.ws-name", ws.name),
        h("span.ws-meta", ws.branch ?? (ws.loaded ? "" : "not open"))),
      activityMark(ws.activity),
      ws.favorite ? h("span.ws-pin", { title: "Pinned" }, icon("star")) : null));

  // A filter that leaves nothing says so: a list that empties itself reads as workspaces that have gone.
  fill(wsPage.list,
    all.length === 0 ? h("p.empty", "mTiles has no workspaces yet.")
      : rows.length === 0 ? h("p.empty", `No workspace matches “${wsQuery.trim()}”.`)
        : rows);
}

/** Opening a workspace here opens it on the computer too: the phone is a remote for that screen, and
 *  the two showing different workspaces would leave dictation and the active tile aimed elsewhere. Only
 *  a workspace that is not loaded asks first, because opening it starts its shells and agents. */
async function openWorkspace(ws, from) {
  if (!ws.loaded && !confirm(`Open ${ws.name} on the computer? mTiles switches to it and starts its tiles.`)) return;
  const askedOf = currentId;
  if (!ws.current && !(await ask({ type: "open", workspaceId: ws.id }))) return;
  // Switched to another computer while it was opening: this workspace is not one of that computer's.
  if (currentId !== askedOf || state.level !== "workspaces") return;
  go("layout", { workspaceId: ws.id, from });
}

/**
 * Logs this phone out of one computer: mTiles is asked to forget it — the same as Unpair in its panel —
 * and this browser forgets the pairing. Pairing again needs a new code. With the computer out of reach
 * only the phone's half can be done; the row it leaves in mTiles' panel opens nothing without this
 * phone's key, and Unpair there removes it.
 */
async function logOut(m) {
  if (!m) return;
  if (!confirm(`Log out of ${m.name}? Pairing this phone again needs a new code from mTiles.`)) return;

  const conn = m.conn;
  m.conn = null;
  if (conn && isUp(m)) {
    try { await conn.request({ type: "unpair" }); } catch { /* forgotten here all the same */ }
  }
  await conn?.close();
  await forgetMachine(m.id);
  machines.delete(m.id);

  if (isCurrent(m)) putNextOnScreen();
  say(`Logged out of ${m.name}.`);
  go("computers");
}

// layout

function drawLayout() {
  const ws = state.workspaces.find((w) => w.id === state.workspaceId);
  header({ back: "All workspaces", title: state.layout?.name ?? ws?.name ?? "Workspace", sub: ws?.branch });

  const layout = state.layout;
  if (!layout) { fill(view, h("p.empty", "Loading the layout…")); fill(dock); return; }
  if (!layout.root) { fill(view, h("p.empty", "This workspace has no tiles.")); fill(dock); return; }

  // clientWidth, not getBoundingClientRect: the zoom animates the view with a transform, and a layout
  // that arrives mid-zoom was measured at the animation's scale — a miniature drawn at half size.
  const pad = 8;
  const rects = layoutRects(layout.root, { x: 0, y: 0, w: view.clientWidth - pad * 2, h: view.clientHeight - pad * 2 }, 8);

  if (isLegible(rects)) {
    const canvas = h("div.miniature");
    for (const r of rects) {
      const card = tileCard(r.leaf);
      Object.assign(card.style, { left: `${r.x}px`, top: `${r.y}px`, width: `${r.w}px`, height: `${r.h}px` });
      canvas.append(card);
    }
    fill(view, canvas);
  } else {
    // Too many tiles for a phone to draw to scale: listed in reading order, with the same marks.
    fill(view, h("nav.tile-list", readingOrder(rects).map((r) => tileCard(r.leaf, true))));
  }
  fill(dock);
}

function tileCard(leaf, row = false) {
  const preview = leaf.preview;
  const classes = ["tile-card", row && "row", leaf.active && "active", !leaf.reachable && "unreachable",
    leaf.activity === "blocked" && "blocked", leaf.activity === "working" && "working"].filter(Boolean).join(" ");

  // How long it has been quiet: said only of a tile that is not working, since the arc says the rest.
  const since = preview?.changedAt != null && leaf.activity !== "working"
    ? ago(Date.now() + state.clockOffset - preview.changedAt) : "";
  const context = preview?.contextPercent;

  return h("button", {
    class: classes,
    style: { "--kind": `var(--k-${KIND_ACCENT[leaf.kind] ?? "terminal"})` },
    disabled: !leaf.reachable,
    title: leaf.reachable ? leaf.name : `${leaf.name} — not available from a phone`,
    onclick: (e) => go("tile", { tileId: leaf.tileId, from: e.currentTarget }),
  },
    h("span.tile-head", activityMark(leaf.activity), h("span.tile-kind", KINDS[leaf.kind] ?? leaf.kind),
      since ? h("span.tile-age", { title: "since it last did something" }, since) : null),
    preview?.text ? h("span.tile-preview", preview.text) : null,
    h("span.tile-name", leaf.name),
    context != null ? h("span.tile-context", {
      class: context >= 80 ? "high" : "",
      title: `${Math.round(context)}% of the context used`,
      style: { "--used": `${Math.max(0, Math.min(100, context))}%` },
    }) : null);
}

// tile

let transcriptEl = null;
const drawnItems = new Map();

function drawTile() {
  const tile = state.tile;
  const status = tile?.status;
  header({
    back: "Back to the layout",
    title: tile?.name ?? "Tile",
    sub: [status?.detail, status?.contextPercent != null ? `${Math.round(status.contextPercent)}% context` : null]
      .filter(Boolean).join("   "),
    marks: tile ? activityMark(status.activity) : null,
  });

  if (!tile) { fill(view, h("p.empty", "Loading…")); fill(dock); lastTileKey = lastDockKey = null; return; }

  // Unchanged since it was drawn — a push about something else — so nothing under the finger moves: a
  // half-typed answer keeps its focus and its keyboard.
  const key = JSON.stringify([tile.status, tile.chat, tile.screen, tile.view, state.answerChips]);
  if (key !== lastTileKey || !view.querySelector(".tile-body")) {
    lastTileKey = key;
    drawTileBody(tile);
  }
  drawDock(tile);
}

let lastTileKey = null;

function drawTileBody(tile) {
  const status = tile.status;
  const atEnd = isAtEnd();
  const body = h("div.tile-body",
    status?.text ? h("p.tile-status", { class: status.activity }, status.text) : null,
    tile.view === "chat" ? chatView(tile) : tile.view === "terminal" ? screenView(tile) : actionsOnlyView(tile));
  if (view.firstChild?.classList?.contains("tile-body") && view.firstChild.dataset.tile === tile.tileId) {
    view.firstChild.replaceWith(body);
  } else {
    fill(view, body);
  }
  body.dataset.tile = tile.tileId;
  scrollToEnd(atEnd);
}

function isAtEnd() {
  return view.scrollHeight - view.scrollTop - view.clientHeight < 80;
}

function scrollToEnd(force) {
  if (force) requestAnimationFrame(() => { view.scrollTop = view.scrollHeight; });
}

function chatView(tile) {
  const chat = tile.chat;
  if (!transcriptEl || transcriptEl.dataset.tile !== tile.tileId) {
    transcriptEl = h("ol.transcript");
    transcriptEl.dataset.tile = tile.tileId;
    drawnItems.clear();
  }

  // Kept by id and rebuilt only where an item changed, so a streaming answer does not re-render the
  // whole conversation four times a second.
  const seen = new Set();
  let previous = null;
  for (const item of chat.items) {
    seen.add(item.id);
    const key = JSON.stringify(item);
    let entry = drawnItems.get(item.id);
    if (!entry || entry.key !== key) {
      const el = chatItem(item);
      if (entry) entry.el.replaceWith(el);
      entry = { key, el };
      drawnItems.set(item.id, entry);
    }
    const expected = previous ? previous.nextSibling : transcriptEl.firstChild;
    if (expected !== entry.el) transcriptEl.insertBefore(entry.el, expected);
    previous = entry.el;
  }
  for (const [id, entry] of drawnItems) {
    if (!seen.has(id)) { entry.el.remove(); drawnItems.delete(id); }
  }

  return [
    chat.omitted ? h("p.omitted", `${chat.omitted} earlier ${chat.omitted === 1 ? "entry" : "entries"} are on the computer.`) : null,
    transcriptEl,
    chat.plan ? planSteps(chat.plan) : null,
    chat.pending ? pendingBlock(tile, chat.pending) : null,
  ];
}

function chatItem(item) {
  const li = h("li.msg", { class: `${item.role} ${item.tone ?? ""}` });
  if (item.role === "work") {
    const details = h("details.work",
      h("summary", item.text),
      h("ul", (item.work ?? []).map((w) => h("li", { class: w.state }, h("span.state"), w.title))));
    if ((item.work ?? []).some((w) => w.state === "running")) details.classList.add("running");
    li.append(details);
  } else if (item.markdown) {
    const body = h("div.md");
    body.append(render(parse(item.text)));
    li.append(body);
  } else {
    li.append(h("pre.plain", item.text));
  }
  if (item.streaming) li.classList.add("streaming");
  return li;
}

function planSteps(plan) {
  return h("ol.plan", plan.map((s) => h("li", { class: `step-${s.status}` }, s.text)));
}

function pendingBlock(tile, pending) {
  const block = h("section.pending", { class: pending.kind },
    h("p.pending-title", pending.title,
      pending.secondsLeft != null ? h("span.countdown", ` ${pending.secondsLeft}s`) : null));

  if (pending.detail) {
    if (pending.kind === "plan") {
      const md = h("div.md.plan-detail");
      md.append(render(parse(pending.detail)));
      block.append(md);
    } else {
      block.append(h("pre.plain.detail", pending.detail));
    }
  }

  if (pending.kind === "questions") {
    const answers = (state.answers[pending.id] ??= {});
    for (const q of pending.questions ?? []) {
      const chosen = answers[q.id] ?? [];
      block.append(h("div.question",
        q.header ? h("p.q-header", q.header) : null,
        h("p.q-text", q.text),
        h("div.chips", q.options.map((label) => h("button.chip", {
          class: chosen.includes(label) ? "on" : "",
          onclick: () => {
            answers[q.id] = q.multi
              ? (chosen.includes(label) ? chosen.filter((c) => c !== label) : [...chosen, label])
              : [label];
            state.answerChips++;
            draw();
          },
        }, label))),
        q.custom ? h("input.q-custom", {
          type: "text", placeholder: "Or type an answer",
          value: chosen.length === 1 && !q.options.includes(chosen[0]) ? chosen[0] : "",
          oninput: (e) => { answers[q.id] = e.target.value.trim() ? [e.target.value.trim()] : []; },
        }) : null));
    }
    block.append(h("div.options", h("button.primary", {
      onclick: async () => {
        if (await ask({ type: "answer", tileId: tile.tileId, pendingId: pending.id, answers })) delete state.answers[pending.id];
      },
    }, "Send answers")));
  } else if (pending.options.length) {
    block.append(h("div.options", pending.options.map((o) => h("button", {
      class: o.tone,
      onclick: () => ask({ type: "choose", tileId: tile.tileId, pendingId: pending.id, optionId: o.id }),
    }, o.label))));
  }
  return block;
}

function screenView(tile) {
  const pre = h("pre.screen");
  pre.textContent = (tile.screen?.lines ?? []).join("\n");
  return pre;
}

function actionsOnlyView(tile) {
  return tile.actions.length
    ? h("div.actions-only", tile.actions.map((a) => actionButton(tile, a)))
    : h("p.empty", "There is nothing to do in this tile from a phone.");
}

function actionButton(tile, a) {
  return h("button", {
    disabled: !a.enabled,
    onclick: async () => {
      state.actionsOpen = false;
      if (await ask({ type: "action", tileId: tile.tileId, id: a.id })) say(`${a.label}: started.`);
      draw();
    },
  }, a.label);
}

// dock

let draft = "";
let lastDockKey = null;

function drawDock(tile) {
  const composer = tile.composer;
  const dictating = state.session?.dictation?.available;
  const recordingHere = dictation.recording;
  const hasActions = tile.actions.length > 0 && tile.view !== "none";

  // Rebuilt only when what it offers changed: rebuilding the text box under somebody's thumb ends their
  // keyboard's composition and throws the caret to the end.
  const dockKey = JSON.stringify([tile.tileId, composer, tile.actions, dictating, state.keysOpen, state.actionsOpen, draft.length === 0]);
  if (dockKey === lastDockKey && dock.firstChild) return;
  lastDockKey = dockKey;

  const input = h("textarea.draft", {
    rows: 1,
    placeholder: composer.placeholder || "Type a message",
    disabled: !composer.enabled,
    value: draft,
    oninput: (e) => {
      const wasEmpty = draft.length === 0;
      draft = e.target.value;
      grow(e.target);
      // Send and Stop share a slot, and which one shows depends on whether anything is typed.
      if (wasEmpty !== (draft.length === 0) && composer.canInterrupt) drawDock(tile);
    },
    onkeydown: (e) => { if (e.key === "Enter" && !e.shiftKey && !composer.keys) { e.preventDefault(); send(tile, true); } },
  });

  const mic = dictating ? h("button.mic", {
    class: recordingHere ? "recording" : "",
    "aria-label": "Hold to talk",
    onpointerdown: (e) => { e.preventDefault(); e.currentTarget.setPointerCapture(e.pointerId); dictation.begin(tile.tileId); e.currentTarget.classList.add("recording"); },
    onpointerup: (e) => { dictation.finish(false); e.currentTarget.classList.remove("recording"); },
    onpointercancel: (e) => { dictation.finish(true); e.currentTarget.classList.remove("recording"); },
    oncontextmenu: (e) => e.preventDefault(),
  }, icon("mic")) : null;

  // Stop stands where Send was a moment ago, and what the tool was answering is lost — so a second tap
  // meant for Send must not be enough, the rule the desktop's own Pause keeps by asking first.
  const confirmStop = () => window.confirm("Stop? What the tool is answering now is lost.");
  const primary = composer.canInterrupt && !draft.trim()
    ? h("button.stop", { "aria-label": "Stop", onclick: () => confirmStop() && ask({ type: "interrupt", tileId: tile.tileId }) }, icon("stop"))
    : h("button.send", { "aria-label": "Send", disabled: !composer.enabled, onclick: () => send(tile, true) }, icon("send"));

  const tools = [
    composer.keys ? h("button.tool", { class: state.keysOpen ? "on" : "", "aria-label": "Keys", onclick: () => { state.keysOpen = !state.keysOpen; draw(); } }, icon("keys")) : null,
    hasActions ? h("button.tool", { class: state.actionsOpen ? "on" : "", "aria-label": "Actions", onclick: () => { state.actionsOpen = !state.actionsOpen; draw(); } }, icon("more")) : null,
  ];

  const focused = document.activeElement?.classList?.contains("draft");
  fill(dock,
    state.actionsOpen && hasActions ? h("div.dock-actions", tile.actions.map((a) => actionButton(tile, a))) : null,
    state.keysOpen && composer.keys ? h("div.keys", [
      ["up", "up"], ["down", "down"], ["left", "left"], ["right", "right"], ["escape", null], ["enter", "enter"],
    ].map(([key, glyph]) => h("button.key", {
      "aria-label": key, onclick: () => ask({ type: "key", tileId: tile.tileId, key }),
    }, glyph ? icon(glyph) : "Esc"))) : null,
    h("div.dock-row", tools, input, mic, primary));
  grow(input);
  if (focused) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
}

function grow(el) {
  el.style.height = "auto";
  el.style.height = `${Math.min(el.scrollHeight, 140)}px`;
}

async function send(tile, submit) {
  const text = draft.trim();
  if (!text || state.sending) return;
  state.sending = true;
  const answer = await ask({ type: "send", tileId: tile.tileId, text, submit });
  state.sending = false;
  if (answer) { draft = ""; scrollToEnd(true); }
  draw();
}

// ── toast ─────────────────────────────────────────────────────────────────────────────────────

let toastTimer = null;
function say(message, tone = "info") {
  toast.textContent = message;
  toast.dataset.tone = tone;
  toast.classList.add("shown");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("shown"), tone === "error" ? 6000 : 3500);
}

addEventListener("resize", () => { if (state.level === "layout") draw(); });

// The cards' "3m" moves with the clock, not only with pushes.
setInterval(() => { if (state.level === "layout" && !document.hidden) draw(); }, 30_000);

// The keyboard. Not every browser honours `interactive-widget=resizes-content` — some lay the keyboard
// over the page and shrink only the visual viewport, which left the dock under it. So the page is held
// to the visual viewport itself: as tall as what is visible, and moved down by however far the browser
// scrolled the layout to keep the caret in view.
const vv = window.visualViewport;
if (vv) {
  const fit = () => {
    const atEnd = state.level === "tile" && isAtEnd();
    root.style.height = `${vv.height}px`;
    root.style.transform = vv.offsetTop > 0 ? `translateY(${vv.offsetTop}px)` : "";
    if (atEnd) scrollToEnd(true);
  };
  vv.addEventListener("resize", fit);
  vv.addEventListener("scroll", fit);
  fit();
}

// A pairing link opened in the page already running — pasted into its address bar — changes only the
// fragment, which reloads nothing.
addEventListener("hashchange", () => {
  const invitation = takeInvitationFromUrl();
  if (invitation) pairWith(invitation);
});

history.replaceState({ level: "computers" }, "");
start();
