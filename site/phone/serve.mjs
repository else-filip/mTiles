// A local copy of the phone page, for developing against a build of mTiles:
//
//   node site/phone/serve.mjs            # http://localhost:8787/
//
// Point mTiles at it by setting "Phone": { "PageUrl": "http://localhost:8787/" } in settings.json. A browser
// gives the microphone and WebCrypto to localhost without https, so the page works here as it does on
// GitHub Pages. The DERP map is proxied (tailcat.dev serves it without CORS) and the fonts are read from
// the application's own copy, which is what the Pages workflow copies too.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = fileURLToPath(new URL(".", import.meta.url));
const FONTS = join(HERE, "../../src/mTiles/Assets/Fonts/JetBrainsMono");
const PORT = Number(process.env.PORT ?? 8787);
const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8", ".json": "application/json", ".webmanifest": "application/manifest+json",
  ".png": "image/png", ".ttf": "font/ttf",
};

createServer(async (request, response) => {
  const { pathname } = new URL(request.url, "http://localhost");
  try {
    if (pathname === "/derpmap.json") {
      const upstream = await fetch("https://tailcat.dev/derpmap.json");
      response.writeHead(upstream.status, { "content-type": TYPES[".json"] });
      response.end(await upstream.text());
      return;
    }
    const relative = normalize(decodeURIComponent(pathname === "/" ? "/index.html" : pathname)).replace(/^[\/]+/, "");
    if (relative.startsWith("..")) throw new Error("outside");
    const file = relative.startsWith("fonts") ? join(FONTS, relative.slice(6)) : join(HERE, relative);
    const body = await readFile(file);
    response.writeHead(200, { "content-type": TYPES[extname(file)] ?? "application/octet-stream", "cache-control": "no-store" });
    response.end(body);
  } catch {
    response.writeHead(404);
    response.end();
  }
}).listen(PORT, () => console.log(`http://localhost:${PORT}/`));
