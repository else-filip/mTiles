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
import { Connection, knownMachines, lastMachineId, takeInvitationFromUrl, forgetMachine, PROTOCOL } from "./connection.js";
import { Dictation } from "./dictation.js";

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
  machine: null,          // { id, name }
  conn: null,
  connState: "idle",      // idle | connecting | connected | reconnecting | refused | failed
  hello: null,
  session: null,          // { dictation: { available, state, tileId }, activeTileId }
  workspaces: [],
  layout: null,           // RemoteLayout of state.workspaceId
  tile: null,             // RemoteTileView of state.tileId
  level: "workspaces",
  workspaceId: null,
  tileId: null,
  keysOpen: false,
  actionsOpen: false,
  answers: {},            // pendingId -> { questionId -> [labels] }
  answerChips: 0,         // moves when a chip is toggled — typing does not redraw, or the field loses focus
  sending: false,
};

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
  const machines = knownMachines();
  const id = invitation?.id ?? lastMachineId() ?? machines.at(-1)?.id;

  if (!id) {
    state.connState = "unpaired";
    draw();
    return;
  }

  await connect(id, invitation?.code ?? null);
}

async function connect(id, code) {
  await state.conn?.close();
  state.machine = knownMachines().find((m) => m.id === id) ?? { id, name: "mTiles" };
  state.workspaces = [];
  state.layout = null;
  state.tile = null;
  state.connState = "connecting";
  draw();

  const conn = new Connection({ onPush, onState: onConnectionState });
  state.conn = conn;

  try {
    state.hello = await conn.open({ id, code });
    state.machine = { id, name: state.hello.machine ?? "mTiles" };
    if (!state.hello.compatible) {
      state.connState = state.hello.protocol > PROTOCOL ? "page-old" : "app-old";
      draw();
      return;
    }
    state.session = state.hello.session ?? null;
    const { workspaces } = await conn.request({ type: "workspaces" });
    state.workspaces = workspaces;
    state.connState = "connected";
    await watch();
  } catch (error) {
    state.connState = /no longer paired|not been paired/.test(error.message) ? "refused" : "failed";
    state.error = error.message;
  }
  draw();
}

function onPush(message) {
  switch (message.type) {
    case "session": state.session = message; break;
    case "workspaces": state.workspaces = message.workspaces ?? []; break;
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
      say(`Heard: ${message.message}`);
      break;
    case "error":
      say(message.message, "error");
      break;
    default:
      return;
  }
  draw();
}

/** A link back after a drop meets an mTiles that may have restarted and forgotten what this page watches. */
function onConnectionState(next) {
  const reconnected = state.connState === "reconnecting" && next === "connected";
  state.connState = next;
  draw();
  if (reconnected) watch();
}

/** Tells mTiles what this page is looking at, so that is what it pushes. */
async function watch() {
  if (!state.conn || state.connState !== "connected") return;
  const workspaceId = state.level === "workspaces" ? null : state.workspaceId;
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
function go(level, { workspaceId, tileId, from } = {}, push = true) {
  const before = state.level;
  state.level = level;
  if (workspaceId !== undefined) state.workspaceId = workspaceId;
  if (tileId !== undefined) state.tileId = tileId;
  if (level !== "tile") { state.tile = null; state.keysOpen = false; state.actionsOpen = false; lastTileKey = lastDockKey = null; }
  if (level === "workspaces") state.layout = null;

  if (push) history.pushState({ level, workspaceId: state.workspaceId, tileId: state.tileId }, "");

  const origin = from?.getBoundingClientRect();
  draw();
  zoom(origin, depth(level) > depth(before));
  watch();

  if (level === "layout" && !state.layout) ask({ type: "layout", workspaceId: state.workspaceId }).then((a) => {
    if (a?.layout) { state.layout = a.layout; draw(); }
  });
  if (level === "tile" && !state.tile) ask({ type: "tile", tileId: state.tileId }).then((a) => {
    if (a?.tile) { state.tile = a.tile; draw(); scrollToEnd(true); }
  });
}

const depth = (level) => ({ workspaces: 0, layout: 1, tile: 2 })[level] ?? 0;

addEventListener("popstate", (event) => {
  const s = event.state ?? { level: "workspaces" };
  go(s.level, { workspaceId: s.workspaceId ?? null, tileId: s.tileId ?? null }, false);
});

function up() {
  if (state.level === "workspaces") return;
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

  if (state.connState !== "connected" && state.connState !== "reconnecting") {
    fill(bar, h("div.title", h("span.name", "mTiles")));
    fill(view, statusScreen());
    fill(dock);
    lastTileKey = lastDockKey = null;
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
    unpaired: ["Pair this phone with mTiles",
      "On the computer, open mTiles and press the QR button beside Settings. Scan the code with this phone's camera."],
    connecting: ["Connecting…", "Reaching mTiles through the relay. This takes a few seconds."],
    refused: ["This phone is not paired any more",
      "It was unpaired on the computer, or the code had already been used. Scan a new code from mTiles."],
    failed: ["mTiles did not answer", state.error ?? "The computer may be off, asleep, or offline."],
    "page-old": ["This page is older than mTiles", "Reload the page to get the current version."],
    "app-old": ["mTiles on the computer needs updating", "This page speaks a newer protocol than that copy of mTiles. Update mTiles, then reload."],
  };
  const [title, text] = screens[state.connState] ?? screens.connecting;
  const others = knownMachines().filter((m) => m.id !== state.machine?.id);

  return h("section.status-screen",
    state.connState === "connecting" ? activityMark("working") : null,
    h("h1", title),
    h("p", text),
    state.connState === "failed"
      ? h("button.primary", { onclick: () => connect(state.machine.id, null) }, "Try again") : null,
    state.connState === "refused" && state.machine
      ? h("button", { onclick: async () => { await forgetMachine(state.machine.id); location.reload(); } }, "Forget this computer") : null,
    others.length ? h("div.others", h("p", "Other computers"), others.map((m) =>
      h("button", { onclick: () => connect(m.id, null) }, m.name))) : null);
}

function header({ back, title, sub, marks }) {
  fill(bar,
    back ? h("button.back", { onclick: up, "aria-label": back }, icon("back")) : null,
    h("div.title", h("span.name", title), sub ? h("span.sub", sub) : null),
    marks ?? null,
    state.connState === "reconnecting" ? h("span.conn", { title: "Reconnecting" }, activityMark("working")) : null);
}

// workspaces

function drawWorkspaces() {
  const machines = knownMachines();
  header({
    title: state.machine?.name ?? "mTiles",
    sub: machines.length > 1 ? "tap to switch computer" : null,
  });
  if (machines.length > 1) bar.querySelector(".title").addEventListener("click", chooseMachine);

  const rows = state.workspaces.map((ws) =>
    h("button.ws-row", {
      class: [ws.current && "current", !ws.loaded && "unloaded"].filter(Boolean).join(" "),
      onclick: (e) => openWorkspace(ws, e.currentTarget),
    },
      h("span.ws-text",
        h("span.ws-name", ws.name),
        h("span.ws-meta", ws.branch ?? (ws.loaded ? "" : "not open"))),
      activityMark(ws.activity)));

  fill(view, rows.length ? h("nav.ws-list", rows) : h("p.empty", "mTiles has no workspaces yet."));
  fill(dock);
  lastDockKey = null;
}

async function openWorkspace(ws, from) {
  if (!ws.loaded) {
    if (!confirm(`Open ${ws.name} on the computer? mTiles switches to it and starts its tiles.`)) return;
    if (!(await ask({ type: "open", workspaceId: ws.id }))) return;
  }
  go("layout", { workspaceId: ws.id, from });
}

function chooseMachine() {
  const machines = knownMachines();
  const sheet = h("div.sheet",
    h("p.sheet-title", "Computers paired with this phone"),
    machines.map((m) => h("button", {
      class: m.id === state.machine?.id ? "current" : "",
      onclick: () => { sheet.remove(); connect(m.id, null); },
    }, m.name)),
    h("button.quiet", { onclick: () => sheet.remove() }, "Close"));
  root.append(sheet);
}

// layout

function drawLayout() {
  const ws = state.workspaces.find((w) => w.id === state.workspaceId);
  header({ back: "All workspaces", title: state.layout?.name ?? ws?.name ?? "Workspace", sub: ws?.branch });

  const layout = state.layout;
  if (!layout) { fill(view, h("p.empty", "Loading the layout…")); fill(dock); return; }
  if (!layout.root) { fill(view, h("p.empty", "This workspace has no tiles.")); fill(dock); return; }

  const box = view.getBoundingClientRect();
  const pad = 8;
  const rects = layoutRects(layout.root, { x: 0, y: 0, w: box.width - pad * 2, h: box.height - pad * 2 }, 8);

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
  const classes = ["tile-card", row && "row", leaf.active && "active", !leaf.reachable && "unreachable",
    leaf.activity === "blocked" && "blocked"].filter(Boolean).join(" ");
  return h("button", {
    class: classes,
    style: { "--kind": `var(--k-${KIND_ACCENT[leaf.kind] ?? "terminal"})` },
    disabled: !leaf.reachable,
    title: leaf.reachable ? leaf.name : `${leaf.name} — not available from a phone`,
    onclick: (e) => go("tile", { tileId: leaf.tileId, from: e.currentTarget }),
  },
    h("span.tile-head", activityMark(leaf.activity), h("span.tile-kind", KINDS[leaf.kind] ?? leaf.kind)),
    h("span.tile-name", leaf.name));
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

history.replaceState({ level: "workspaces" }, "");
start();
