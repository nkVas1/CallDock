const BASE = "http://127.0.0.1:47831";
const MAX_TABS = 4;
const TOKEN = /^[a-f0-9]{64}$/i;
const $ = id => document.getElementById(id);

const labels = {
  checking: "Проверяю…",
  ok: "Подключено",
  recording: "Идёт запись",
  offline: "CallDock не запущен",
  "bad-token": "Код не подходит",
  unpaired: "Не подключено"
};

let token = "";
let tab;
let connection = { state: "checking" };
let tracks = [];
let editingToken = false;
let shownError = "";
const rows = new Map();

const qualityOptions = () => document.querySelectorAll('input[name="quality"]');
const chosenQuality = () => [...qualityOptions()].find(option => option.checked)?.value || "normal";

function showMessage(text, kind = "warn") {
  const box = $("message");
  box.textContent = text || "";
  box.hidden = !text;
  box.className = "message" + (kind === "info" ? " info" : "");
}

async function command(type, data = {}) {
  const result = await chrome.runtime.sendMessage({ target: "background", type, ...data });
  if (result?.error) throw new Error(result.error);
  return result;
}

async function checkConnection() {
  if (!token) { connection = { state: "unpaired" }; return; }
  try {
    const response = await fetch(BASE + "/health", { headers: { Authorization: "Bearer " + token }, signal: AbortSignal.timeout(1500) });
    if (response.status === 401) connection = { state: "bad-token" };
    else if (!response.ok) connection = { state: "offline" };
    else {
      const data = await response.json();
      connection = { state: data.recording ? "recording" : "ok", version: data.version, extension: data.extension };
    }
  } catch {
    connection = { state: "offline" };
  }
}

/** Chrome never lets extensions capture its own pages or the Web Store. */
function capturable(target) {
  const url = target?.url;
  if (!url) return true;
  if (!/^(https?|file):/i.test(url)) return false;
  return !/^https:\/\/(chromewebstore\.google\.com|chrome\.google\.com\/webstore)/i.test(url);
}

function elapsed(startedAt) {
  const seconds = Math.max(0, Math.floor((Date.now() - startedAt) / 1000));
  const hours = Math.floor(seconds / 3600), minutes = Math.floor(seconds / 60) % 60, rest = seconds % 60;
  const two = n => String(n).padStart(2, "0");
  return hours ? `${hours}:${two(minutes)}:${two(rest)}` : `${two(minutes)}:${two(rest)}`;
}

function level(peak) {
  if (!(peak > 0)) return 0;
  const db = 20 * Math.log10(peak);
  return Math.min(100, Math.max(0, (db + 60) / 60 * 100));
}

function render() {
  const pill = $("connection");
  pill.dataset.state = connection.state;
  pill.querySelector("span").textContent = labels[connection.state];
  pill.title = connection.version ? `CallDock ${connection.version}` : "";

  // Chrome keeps running an unpacked extension's old code until it is reloaded: when CallDock brings changed extension
  // files, the version it ships differs from the one Chrome has loaded.
  const loaded = chrome.runtime.getManifest().version;
  $("outdated").hidden = !connection.extension || connection.extension === loaded;

  const needsToken = editingToken || connection.state === "unpaired" || connection.state === "bad-token";
  $("pairing").hidden = !needsToken;
  $("cancel-token").hidden = !(editingToken && token);
  $("change-token").hidden = needsToken;

  const connected = connection.state === "ok" || connection.state === "recording";
  const mine = tracks.find(t => t.tabId === tab?.id);
  const start = $("start");
  start.classList.toggle("stop", !!mine);
  start.querySelector("span").textContent = mine
    ? (mine.stopping ? "Сохраняю запись…" : "Остановить запись вкладки")
    : "Записывать эту вкладку";
  start.disabled = !tab || (mine ? mine.stopping : !connected || tracks.length >= MAX_TABS || !capturable(tab));
  start.title = !mine && tracks.length >= MAX_TABS ? `Одновременно записывается не больше ${MAX_TABS} вкладок` : "";
  $("monitor").disabled = !!mine;
  // Only a new recording starts CallDock's own sources; one that is already going has them (or adds them in CallDock).
  $("with-sources-row").hidden = connection.state !== "ok" || tracks.length > 0;
  for (const option of qualityOptions()) option.disabled = !!mine;
  $("tab-title").textContent = !tab ? "—" : capturable(tab) ? (tab.title || tab.url || "Без названия") : "Эту страницу Chrome записать не даёт";

  $("tab-hint").hidden = tracks.length > 0;
  $("recording").hidden = tracks.length === 0;
  $("recording-title").textContent = `Записываются · ${tracks.length} из ${MAX_TABS}`;
  renderTracks();
}

function renderTracks() {
  const list = $("tracks");
  const alive = new Set(tracks.map(t => t.tabId));
  for (const [tabId, row] of rows) if (!alive.has(tabId)) { row.remove(); rows.delete(tabId); }
  for (const track of tracks) {
    let row = rows.get(track.tabId);
    if (!row) {
      row = document.createElement("li");
      const dot = document.createElement("span");
      dot.className = "dot";
      const title = document.createElement("span");
      title.className = "track-title";
      title.title = "Перейти к вкладке";
      title.style.cursor = "pointer";
      title.onclick = () => chrome.tabs.update(track.tabId, { active: true }).catch(() => {});
      const stop = document.createElement("button");
      stop.className = "track-stop secondary";
      stop.textContent = "Стоп";
      stop.onclick = async () => {
        stop.disabled = true;
        try { await command("stop", { tabId: track.tabId }); } catch (error) { showMessage(error.message); }
        await refresh();
      };
      const meta = document.createElement("span");
      meta.className = "track-meta";
      const meter = document.createElement("span");
      meter.className = "meter";
      meter.append(document.createElement("b"));
      const time = document.createElement("span");
      time.className = "time";
      meta.append(meter, time);
      row.append(dot, title, stop, meta);
      rows.set(track.tabId, row);
      list.append(row);
    }
    row.classList.toggle("stopping", track.stopping);
    row.classList.toggle("current", track.tabId === tab?.id);
    row.querySelector(".track-title").textContent = track.title;
    row.querySelector(".track-stop").disabled = track.stopping;
    const bar = row.querySelector(".meter b");
    const width = level(track.peak);
    bar.style.width = width + "%";
    bar.classList.toggle("hot", width > 95);
    row.querySelector(".time").textContent = track.stopping ? "сохранение…" : elapsed(track.startedAt);
  }
}

async function refresh() {
  try {
    const status = await command("status");
    tracks = status.tracks || [];
    if (status.error && status.error !== shownError) { shownError = status.error; showMessage(status.error); }
  } catch (error) {
    showMessage(error.message);
  }
  render();
}

async function saveToken() {
  const value = $("token").value.trim();
  if (!TOKEN.test(value)) {
    showMessage("Это не код подключения. Скопируйте его кнопкой «Скопировать код» в настройках CallDock.");
    return;
  }
  token = value.toLowerCase();
  await chrome.storage.local.set({ token });
  $("token").value = "";
  editingToken = false;
  await checkConnection();
  if (connection.state === "bad-token") showMessage("CallDock не принял этот код. Скопируйте его заново.");
  else if (connection.state === "offline") showMessage("Код сохранён. Запустите CallDock — расширение подключится само.", "info");
  else showMessage("");
  render();
}

$("save-token").onclick = saveToken;
$("token").addEventListener("input", () => { if (TOKEN.test($("token").value.trim())) saveToken(); });
$("token").addEventListener("keydown", event => { if (event.key === "Enter") saveToken(); });
$("change-token").onclick = () => { editingToken = true; render(); $("token").focus(); };
$("cancel-token").onclick = () => { editingToken = false; $("token").value = ""; render(); };
$("monitor").onchange = () => chrome.storage.local.set({ monitor: $("monitor").checked });
$("with-sources").onchange = () => chrome.storage.local.set({ withSources: $("with-sources").checked });
for (const option of qualityOptions()) option.onchange = () => chrome.storage.local.set({ quality: chosenQuality() });

$("start").onclick = async () => {
  const mine = tracks.find(t => t.tabId === tab.id);
  $("start").disabled = true;
  try {
    if (mine) {
      await command("stop", { tabId: tab.id });
      showMessage("Запись вкладки сохраняется в CallDock.", "info");
    } else {
      await command("start", {
        tabId: tab.id, monitor: $("monitor").checked, quality: chosenQuality(),
        withSources: !$("with-sources-row").hidden && $("with-sources").checked
      });
      showMessage("Запись идёт в CallDock. Можно переключаться на другие вкладки и программы.", "info");
    }
  } catch (error) {
    showMessage(error.message);
  }
  await refresh();
};

$("stop-all").onclick = async () => {
  try {
    await command("stop");
    showMessage("Записи вкладок сохраняются в CallDock.", "info");
  } catch (error) {
    showMessage(error.message);
  }
  await refresh();
};

$("discover").onclick = async () => {
  const box = $("players");
  box.replaceChildren();
  try {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: () => {
        const found = [];
        document.querySelectorAll("iframe[src]").forEach((frame, i) => {
          // Visible players only: counters, ads and chat widgets are small or hidden frames.
          if (frame.offsetWidth >= 240 && frame.offsetHeight >= 135)
            found.push({ url: frame.src, name: frame.title || frame.getAttribute("aria-label") || `Плеер ${i + 1}` });
        });
        document.querySelectorAll("video").forEach((video, i) => {
          const url = video.currentSrc || video.src;
          if (url) found.push({ url, name: `Видео ${i + 1}` });
        });
        return found.filter(item => /^https?:\/\//i.test(item.url));
      }
    });
    const seen = new Set();
    const players = (result?.result || []).filter(p => !seen.has(p.url) && seen.add(p.url));
    for (const player of players) {
      const button = document.createElement("button");
      button.className = "secondary";
      button.textContent = player.name;
      const host = document.createElement("small");
      host.textContent = new URL(player.url).hostname;
      button.append(host);
      button.onclick = () => chrome.tabs.create({ url: player.url, active: false });
      box.append(button);
    }
    showMessage(players.length
      ? `Найдено плееров: ${players.length}. Откройте нужные — каждый в своей вкладке — и запустите запись в каждой.`
      : "Плееры не найдены. Запустите трансляцию на странице и попробуйте снова — или откройте каждый зал в отдельной вкладке вручную.",
      players.length ? "info" : "warn");
  } catch {
    showMessage("Не удалось осмотреть эту страницу: Chrome закрывает к ней доступ расширениям.");
  }
};

const saved = await chrome.storage.local.get(["token", "monitor", "quality", "withSources"]);
token = saved.token || "";
$("monitor").checked = saved.monitor ?? true;
$("with-sources").checked = saved.withSources ?? false;
for (const option of qualityOptions()) option.checked = option.value === (saved.quality || "normal");
[tab] = await chrome.tabs.query({ active: true, currentWindow: true });
$("version").textContent = `Расширение ${chrome.runtime.getManifest().version}`;
await checkConnection();
await refresh();
setInterval(refresh, 400);
setInterval(async () => { await checkConnection(); render(); }, 3000);
