// End-to-end check of the Chrome extension against a running CallDock: four tabs, each playing its own tone,
// are recorded at once through the real action popup; every tab must deliver sound, all must flush and close,
// and every saved file must carry sound to its end.
//
//   CALLDOCK_DATA=<CallDock data folder> node tools/qa/browser.mjs
//
// Options: QA_TABS (1–4, default 4), QA_SECONDS (default 6), QA_QUALITY (normal | high | audio).
// Needs Node 22+ (global WebSocket), Playwright's Chromium (npx playwright install chromium) or CALLDOCK_CHROMIUM,
// and FFmpeg from tools/Get-MediaTools.ps1 (or CALLDOCK_FFPROBE).
import { chromium } from "playwright";
import { readFile, readdir, mkdir, writeFile, rm } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import path from "node:path";
import http from "node:http";
import { createHash } from "node:crypto";
import assert from "node:assert/strict";

if (typeof WebSocket === "undefined") throw new Error("Node 22 or newer is required (global WebSocket).");

const root = path.resolve(import.meta.dirname, "../..");
const qa = path.join(root, ".qa");
const data = process.env.CALLDOCK_DATA || path.join(qa, "data");
const token = (await readFile(path.join(data, "browser-token.txt"), "utf8")).trim();
// Branded Chrome no longer loads unpacked extensions from the command line; Playwright's Chromium does.
const executablePath = process.env.CALLDOCK_CHROMIUM || undefined;
const extensionPath = path.join(root, "extension");
// The manifest's public key fixes the extension ID: the first 128 bits of its SHA-256, written with the letters a–p.
const manifest = JSON.parse(await readFile(path.join(extensionPath, "manifest.json"), "utf8"));
const extensionId = [...createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest("hex").slice(0, 32)]
  .map(c => String.fromCharCode(97 + parseInt(c, 16))).join("");
const debugPort = 47833;
const tabCount = Math.min(4, Math.max(1, Number(process.env.QA_TABS || 4)));
const seconds = Number(process.env.QA_SECONDS || 6);
const quality = process.env.QA_QUALITY || "normal";
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const results = [];

async function find(folder, test) {
  for (const entry of await readdir(folder, { withFileTypes: true }).catch(() => [])) {
    const full = path.join(folder, entry.name);
    if (entry.isDirectory()) { const hit = await find(full, test); if (hit) return hit; }
    else if (test(full)) return full;
  }
  return null;
}

const ffprobe = process.env.CALLDOCK_FFPROBE || await find(path.join(root, "tools/vendor"), f => path.basename(f) === "ffprobe.exe");
if (!ffprobe) throw new Error("FFmpeg not found: run tools/Get-MediaTools.ps1 or set CALLDOCK_FFPROBE.");

/** The time of the last packet of a stream, in seconds; 0 when the file has no such stream. */
function lastPacket(file, stream) {
  const out = execFileSync(ffprobe, ["-v", "error", "-select_streams", stream, "-show_entries", "packet=pts_time", "-of", "csv=p=0", file], { encoding: "utf8" });
  const lines = out.trim().split(/\r?\n/).filter(Boolean);
  return lines.length ? Number(lines.at(-1)) : 0;
}

/** Playwright does not attach to extension popups, so the popup is driven over the raw DevTools protocol. */
class Cdp {
  static async connect(url) {
    const socket = new WebSocket(url);
    await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
    return new Cdp(socket);
  }

  constructor(socket) {
    this.socket = socket;
    this.next = 0;
    this.pending = new Map();
    socket.onmessage = event => {
      const message = JSON.parse(event.data);
      const waiter = this.pending.get(message.id);
      if (!waiter) return;
      this.pending.delete(message.id);
      if (message.error) waiter.reject(new Error(message.error.message)); else waiter.resolve(message.result);
    };
  }

  send(method, params = {}) {
    const id = ++this.next;
    this.socket.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
  }

  async evaluate(expression) {
    const result = await this.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result.value;
  }

  async waitFor(expression, timeout = 15000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      if (await this.evaluate(expression)) return;
      await sleep(150);
    }
    throw new Error(`Timed out waiting for ${expression}; message: ${await this.evaluate("document.querySelector('#message')?.textContent")}`);
  }

  async screenshot(file) {
    const { data: png } = await this.send("Page.captureScreenshot", { format: "png" });
    await writeFile(file, Buffer.from(png, "base64"));
  }

  close() { this.socket.close(); }
}

async function openPopup(worker) {
  await worker.evaluate(() => chrome.action.openPopup());
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline) {
    const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
    const popup = targets.find(t => t.type === "page" && t.url.startsWith(`chrome-extension://${extensionId}/popup.html`));
    if (popup) return await Cdp.connect(popup.webSocketDebuggerUrl);
    await sleep(150);
  }
  throw new Error("The extension popup did not open.");
}

const server = http.createServer((req, res) => {
  const id = Number(new URL(req.url, "http://localhost").searchParams.get("id") || 1);
  res.setHeader("Content-Type", "text/html; charset=utf-8");
  res.end(`<!doctype html><title>Зал ${id} — тестовая трансляция</title><body style="background:hsl(${id * 65} 50% 30%);color:white;font:40px system-ui"><h1>ZONE ${id}</h1><p id="clock"></p><button id="play">Start synthetic tone</button><script>
  setInterval(()=>document.querySelector('#clock').textContent=new Date().toISOString(),100);
  document.querySelector('#play').onclick=async()=>{const a=new AudioContext();await a.resume();const o=a.createOscillator();const g=a.createGain();o.frequency.value=${300 + 200 * id};g.gain.value=0.015;o.connect(g).connect(a.destination);o.start();window.audio=a;};
  </script>`);
});
await new Promise(resolve => server.listen(47832, "127.0.0.1", resolve));
await mkdir(qa, { recursive: true });
// A fresh profile every run: Chromium keeps the service worker of an unpacked extension between launches.
const profile = path.join(qa, "chromium");
await rm(profile, { recursive: true, force: true });
const context = await chromium.launchPersistentContext(profile, {
  executablePath, headless: false, viewport: { width: 800, height: 500 },
  args: [`--disable-extensions-except=${extensionPath}`, `--load-extension=${extensionPath}`, `--remote-debugging-port=${debugPort}`,
    // A popup opened by automation does not grant activeTab the way a click on the toolbar icon does; the allowlist stands in for that click.
    `--allowlisted-extension-id=${extensionId}`,
    "--autoplay-policy=no-user-gesture-required", "--disable-background-timer-throttling"]
});
try {
  const worker = context.serviceWorkers()[0] || await context.waitForEvent("serviceworker");
  assert.equal(new URL(worker.url()).host, extensionId, "the extension ID must follow from the manifest key");
  await worker.evaluate(async ([token, quality]) => { await chrome.storage.local.set({ token, monitor: false, quality }); }, [token, quality]);
  for (let id = 1; id <= tabCount; id++) {
    const page = await context.newPage();
    await page.goto(`http://127.0.0.1:47832/?id=${id}`);
    await page.click("#play");
    await page.bringToFront();
    const popup = await openPopup(worker);
    try {
      await popup.waitFor("document.querySelector('#start') && !document.querySelector('#start').disabled");
      await popup.evaluate("document.querySelector('#start').click()");
      await popup.waitFor("document.querySelector('#message')?.textContent.includes('Запись идёт')");
      if (id === tabCount) {
        await sleep(1500);
        await popup.screenshot(path.join(qa, "popup-recording.png"));
      }
    } finally { popup.close(); }
    await page.bringToFront();
  }
  await sleep(seconds * 1000);
  const status = await worker.evaluate(() => chrome.runtime.sendMessage({ target: "offscreen", type: "status" }));
  assert.equal(status.tracks.length, tabCount);
  assert.ok(status.tracks.every(t => t.peak > 0.001), JSON.stringify(status));
  results.push({ test: "concurrent-isolated-tabs", tabs: tabCount, quality, tracks: status.tracks.map(t => ({ title: t.title, peak: t.peak })), passed: true });
  await worker.evaluate(() => chrome.runtime.sendMessage({ target: "offscreen", type: "stop" }));
  await sleep(5000);
  const stopped = await worker.evaluate(() => chrome.runtime.sendMessage({ target: "offscreen", type: "status" }));
  assert.equal(stopped.tracks.length, 0);
  assert.equal(stopped.error, "");
  results.push({ test: "flush-and-close-all-tabs", passed: true });

  // The files themselves: sound must last as long as the picture (a starved encoder loses the last seconds of sound).
  const settings = JSON.parse(await readFile(path.join(data, "settings.json"), "utf8").catch(() => "{}"));
  const archive = settings.ArchiveRoot || path.join(data, "archive");
  const files = [];
  for (const track of status.tracks) {
    const file = await find(archive, f => f.includes(track.id) && f.endsWith(".webm"));
    assert.ok(file, `no file for track ${track.id}`);
    const audio = lastPacket(file, "a:0"), video = lastPacket(file, "v:0");
    files.push({ title: track.title, audio, video });
    assert.ok(audio > seconds - 1, `sound too short in ${file}: ${audio} s`);
    if (quality === "audio") assert.equal(video, 0, `picture recorded in audio-only mode: ${file}`);
    else assert.ok(audio >= video - 1, `sound ends ${(video - audio).toFixed(1)} s before the picture in ${file}`);
  }
  results.push({ test: "sound-covers-the-whole-recording", files, passed: true });
  console.log(JSON.stringify(results, null, 2));
  await writeFile(path.join(qa, "browser-results.json"), JSON.stringify(results, null, 2));
} finally {
  await context.close();
  server.close();
}
