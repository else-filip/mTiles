// The link to one mTiles, and what it says.
//
// The invitation code arrives in the URL's fragment, which a browser never sends to any server — so the
// host of this page never sees it. It is read once, used to pair, and taken out of the address bar.
// Each machine this browser has paired with gets a stored identity of its own, named after the machine's
// public key, so one phone can hold a desktop and a laptop.

import { TailcatLink, parseInvitationCode, PairingRefusedError } from "./vendor/tailcat-link/index.js";

export const PROTOCOL = 1;
const MACHINES = "mtiles.machines";
const LAST = "mtiles.lastMachine";

/** The machines this browser is paired with, newest last. */
export function knownMachines() {
  try { return JSON.parse(localStorage.getItem(MACHINES) ?? "[]"); } catch { return []; }
}

function remember(machine) {
  const list = knownMachines().filter((m) => m.id !== machine.id);
  list.push(machine);
  localStorage.setItem(MACHINES, JSON.stringify(list));
  localStorage.setItem(LAST, machine.id);
}

export function forgetMachine(id) {
  localStorage.setItem(MACHINES, JSON.stringify(knownMachines().filter((m) => m.id !== id)));
  if (localStorage.getItem(LAST) === id) localStorage.removeItem(LAST);
  return TailcatLink.forget(appNameFor(id)).catch(() => {});
}

export function lastMachineId() {
  return localStorage.getItem(LAST);
}

const appNameFor = (id) => `mtiles-phone-${id}`;

/** Reads a code out of the page's fragment, and takes it out of the address bar. */
export function takeInvitationFromUrl() {
  const hash = location.hash.slice(1);
  if (!hash) return null;
  history.replaceState(null, "", location.pathname + location.search);
  try {
    const code = decodeURIComponent(hash);
    const { address } = parseInvitationCode(code);
    return { code, id: hex(address.serverPublic).slice(0, 12) };
  } catch {
    return null;
  }
}

function hex(bytes) {
  return Array.from(bytes ?? [], (b) => b.toString(16).padStart(2, "0")).join("");
}

function deviceName() {
  const ua = navigator.userAgent;
  const device = /iPhone/.test(ua) ? "iPhone" : /iPad/.test(ua) ? "iPad" : /Android/.test(ua) ? "Android phone"
    : /Mac/.test(ua) ? "Mac" : /Windows/.test(ua) ? "Windows PC" : "Browser";
  const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /CriOS|Chrome\//.test(ua) ? "Chrome"
    : /Safari\//.test(ua) ? "Safari" : "";
  return browser ? `${device} · ${browser}` : device;
}

/**
 * One mTiles, connected. `onPush(message)` is called for everything it pushes; `onState(state)` with
 * "connecting", "connected", "reconnecting" or "refused".
 */
export class Connection {
  #link = null;
  #onPush;
  #onState;

  constructor({ onPush, onState }) {
    this.#onPush = onPush;
    this.#onState = onState;
  }

  get link() { return this.#link; }

  async open({ id, code = null }) {
    this.#onState("connecting");
    const link = await TailcatLink.join({
      appName: appNameFor(id),
      invitationCode: code,
      displayName: deviceName(),
      derpMap: new URL("derpmap.json", location.href).href,
    });
    this.#link = link;

    link.onNotify((text) => {
      try { this.#onPush(JSON.parse(text)); } catch { /* not ours to understand */ }
    });
    link.onRequest(() => "");

    link.events?.addEventListener?.("failed", () => this.#onState("refused"));
    link.events?.addEventListener?.("disconnected", () => this.#onState("reconnecting"));
    link.events?.addEventListener?.("connected", () => this.#onState("connected"));

    const hello = await this.request({ type: "hello", protocol: PROTOCOL });
    remember({ id, name: hello.machine ?? "mTiles" });
    this.#onState("connected");
    return hello;
  }

  /** Asks mTiles something. Resolves with its answer, or throws an Error carrying its sentence. */
  async request(message) {
    let text;
    try {
      text = await this.#link.request(JSON.stringify(message));
    } catch (error) {
      if (error instanceof PairingRefusedError) {
        this.#onState("refused");
        throw new Error("This phone is no longer paired with that computer. Scan a new code.");
      }
      throw new Error("mTiles did not answer. It may be off, asleep, or offline.");
    }
    const answer = JSON.parse(text);
    if (answer.ok === false) throw new Error(answer.error ?? "mTiles could not do that.");
    return answer;
  }

  async close() {
    const link = this.#link;
    this.#link = null;
    await link?.close().catch(() => {});
  }
}
