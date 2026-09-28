// The page's pure parts, under node: `node --test site/phone/test`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { parse, inline } from "../markdown.js";
import { layoutRects, isLegible, readingOrder } from "../geometry.js";

test("a javascript: link is text, never a link", () => {
  const [link] = inline("[click](javascript:alert(1))");
  assert.equal(link.type, "link");
  assert.equal(link.href, null);
});

test("an https link keeps its address", () => {
  const [link] = inline("[docs](https://example.com/a)");
  assert.equal(link.href, "https://example.com/a");
});

test("markup is text: nothing in the tree is HTML", () => {
  const blocks = parse("<img src=x onerror=alert(1)> **bold**");
  const spans = blocks[0].inline;
  assert.equal(spans[0].type, "text");
  assert.equal(spans[0].text, "<img src=x onerror=alert(1)> ");
  assert.equal(spans[1].type, "strong");
});

test("blocks are read as what they are", () => {
  const blocks = parse("# Title\n\ntext\nmore\n\n- a\n- b\n\n1. one\n2. two\n\n```diff\n+x\n-y\n```\n\n> said\n\n---");
  assert.deepEqual(blocks.map((b) => b.type), ["heading", "paragraph", "list", "list", "code", "quote", "rule"]);
  assert.equal(blocks[2].items.length, 2);
  assert.equal(blocks[3].ordered, true);
  assert.equal(blocks[4].lang, "diff");
  assert.equal(blocks[4].text, "+x\n-y");
});

test("an unclosed fence takes the rest of the message rather than losing it", () => {
  const blocks = parse("```\nstill streaming");
  assert.equal(blocks.length, 1);
  assert.equal(blocks[0].text, "still streaming");
});

test("a table stays as its rows, without the separator", () => {
  const [table] = parse("| a | b |\n|---|---|\n| 1 | 2 |");
  assert.equal(table.type, "code");
  assert.equal(table.text, "| a | b |\n| 1 | 2 |");
});

const leaf = (id) => ({ type: "leaf", tileId: id });

test("a split divides its room by its ratio, less the gap", () => {
  const tree = { type: "split", direction: "row", ratio: 0.25, first: leaf("a"), second: leaf("b") };
  const [a, b] = layoutRects(tree, { x: 0, y: 0, w: 408, h: 100 }, 8);
  assert.deepEqual([a.x, a.w, b.x, b.w], [0, 100, 108, 300]);
  assert.equal(a.h, 100);
});

test("nested splits tile the whole rectangle", () => {
  const tree = {
    type: "split", direction: "column", ratio: 0.5, first: leaf("top"),
    second: { type: "split", direction: "row", ratio: 0.5, first: leaf("l"), second: leaf("r") },
  };
  const rects = layoutRects(tree, { x: 0, y: 0, w: 208, h: 208 }, 8);
  assert.deepEqual(readingOrder(rects).map((r) => r.tileId), ["top", "l", "r"]);
  assert.deepEqual(rects.map((r) => [r.x, r.y, r.w, r.h]), [[0, 0, 208, 100], [0, 108, 100, 100], [108, 108, 100, 100]]);
});

test("a layout of stamps is not legible", () => {
  const rects = [{ w: 40, h: 200 }, { w: 200, h: 200 }];
  assert.equal(isLegible(rects), false);
  assert.equal(isLegible([{ w: 60, h: 60 }]), true);
});

import { orderWorkspaces, matchesQuery } from "../workspaces.js";

test("pinned workspaces come first, the rest keep the order they were sent in", () => {
  const list = [{ name: "a" }, { name: "b", favorite: true }, { name: "c" }, { name: "d", favorite: true }];
  assert.deepEqual(orderWorkspaces(list).map((w) => w.name), ["b", "d", "a", "c"]);
});

test("the filter wants every word, anywhere, in any order, over the name and the branch", () => {
  const ws = { name: "mterminal", branch: "feat/phone-relay" };
  assert.equal(matchesQuery(ws, ""), true);
  assert.equal(matchesQuery(ws, "relay mterm"), true);
  assert.equal(matchesQuery(ws, "MTERMINAL"), true);
  assert.equal(matchesQuery(ws, "mterminal main"), false);
  assert.equal(matchesQuery({ name: "x", branch: null }, "x"), true);
});

import { ago } from "../format.js";

test("how long ago is said in one unit, and not at all under a minute", () => {
  assert.equal(ago(null), "");
  assert.equal(ago(59_000), "");
  assert.equal(ago(60_000), "1m");
  assert.equal(ago(59 * 60_000), "59m");
  assert.equal(ago(3 * 3_600_000 + 5), "3h");
  assert.equal(ago(50 * 3_600_000), "2d");
});
