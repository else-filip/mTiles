# 0006 — The phone reaches mTiles through a relay, not through a port on this machine

Date: 2026-09-25

## Context

Dictating from a phone began as a server on this machine: Kestrel serving HTTPS on a configurable port,
a page embedded in the executable, a cookie issued for a single-use token in a QR code. Every piece
around it was a consequence of that port being opened and then found:

- **TLS** — a browser gives the microphone only to a secure origin, and no public authority certifies
  `192.168.1.20`, so mTiles signed certificates itself (and asked the user to click through a warning,
  again for every new network), or fetched a real one from Tailscale where it was installed.
- **The firewall** — Windows silently blocks an inbound port the first time its own prompt is dismissed,
  so the panel read the firewall's rules, diagnosed four ways of being blocked, and offered an elevated
  repair; on Linux it named the running firewall and the command to open it.
- **The address** — a machine has half a dozen, and the QR code holds one, so a pure ranker chose between
  LAN, Tailscale and mDNS names by what had worked before, per kind of session.
- **Reach** — a phone not on the same network, and not on Tailscale, could not reach it at all.

About 5 000 lines, and the user still met a certificate warning, a port that Hyper-V had reserved, or a
firewall that said nothing. Meanwhile the page could only dictate and press keys: it could not show what
the agents it was dictating to were doing.

[tailcat-link](https://github.com/b-y-t-e/tailcat-link) (`Tailcat.Link` on NuGet, BSD-3-Clause, by this
application's author) connects two machines that cannot see each other: both dial out to Tailscale's
public DERP relays on port 443, pair once with a short code, and exchange end-to-end encrypted requests,
notifications and ordered channels, reconnecting by themselves. It ships a browser client.

## Decision

1. **The bridge is a tailcat-link host** (`TailcatLink.HostManyAsync("mtiles-phone")`, at most four
   devices). Nothing on this machine listens to the network. Kestrel, the certificates, the firewall
   repair, the address ranking and the cookie pairing are removed, together with the
   `Microsoft.AspNetCore.App` framework reference they needed.
2. **The page is a static site on GitHub Pages** (`site/phone/`), because with no port mTiles cannot
   serve it and a browser still needs an https origin. The invitation code travels in the URL
   **fragment**, which is never sent to the server. The browser client and tweetnacl are vendored; the
   DERP map is copied in by the Pages workflow, since it must be same-origin.
3. **The page is a remote for the window, not only a microphone**: the workspaces, each one's layout to
   scale, and any tile zoomed into — an agent's or a goal's conversation live, answerable, and a
   terminal's screen as text. What a tile shows is the tile's own answer (`IRemoteViewTile`), sampled by
   version and pushed to the phone watching it.
4. **Audio is one ordered channel per utterance**, header first and the channel's orderly close as the end
   of the sentence.

## Consequences

- **No port, no certificate warning, no firewall, any network.** A paired phone reaches the machine from
  anywhere the machine can reach the internet.
- **Everything the phone sees now crosses Tailscale's relays** — audio, and while a tile is watched, the
  text of its conversation. It is end-to-end encrypted with keys only the two devices hold; the relay sees
  that two keys talk and how much. The link's key schedule has the shape of Noise IK without being it and
  has **not been reviewed** outside tailcat-link. The README says so.
- **The relays are shared and rate-limited.** A browser can never leave the relay (it cannot hole-punch),
  so every byte crosses it twice. Adequate for text and 16 kHz–48 kHz mono audio; not for anything large.
- **The page's origin is part of the trust boundary.** Whoever can run script on it can use the pairing:
  a strict CSP, nothing loaded from elsewhere, and no `innerHTML` anywhere. Whoever can publish to the
  repository's Pages can change the page — the same people who can change the application.
- **Page and application ship separately**, so the protocol carries a version and a mismatch is a screen
  that says which to update.
- **A machine that never paired a phone never dials a relay**; with one paired, mTiles connects to the
  relay whenever it runs, which is what lets the phone reach it.
- GitHub Pages has to be enabled for the repository (source: GitHub Actions).

## History

- 2026-08 — the Kestrel bridge, its self-signed and Tailscale certificates, `PhoneFirewall` and
  `PhoneEndpointRanker` (recorded in `docs/DICTATION.md` before this change). Superseded by this record.
