// The service worker only routes commands: recording itself runs in an offscreen document, which keeps
// MediaRecorder alive while the popup is closed and the worker sleeps.
let creating;

async function ensureOffscreen() {
  const contexts = await chrome.runtime.getContexts({ contextTypes: ["OFFSCREEN_DOCUMENT"] });
  if (contexts.length) return;
  creating ??= chrome.offscreen.createDocument({
    url: "offscreen.html",
    reasons: ["USER_MEDIA"],
    justification: "Запись выбранных пользователем вкладок в CallDock"
  }).finally(() => { creating = undefined; });
  await creating;
}

async function hasOffscreen() {
  return (await chrome.runtime.getContexts({ contextTypes: ["OFFSCREEN_DOCUMENT"] })).length > 0;
}

/** Chrome's own capture errors, said in plain words. */
function explain(error) {
  const text = String(error?.message || error);
  if (/active stream/i.test(text)) return "Эту вкладку уже записывает CallDock или другое расширение.";
  if (/activeTab|cannot be captured|chrome pages/i.test(text))
    return "Chrome не разрешает записывать эту страницу. Откройте вкладку с трансляцией и нажмите значок CallDock на ней.";
  return text;
}

async function start({ tabId, monitor, quality, withSources }) {
  const { token } = await chrome.storage.local.get("token");
  if (!token) throw new Error("Сначала вставьте код подключения из CallDock.");
  const tab = await chrome.tabs.get(tabId);
  let streamId;
  try { streamId = await chrome.tabCapture.getMediaStreamId({ targetTabId: tab.id }); }
  catch (error) { throw new Error(explain(error)); }
  await ensureOffscreen();
  const title = (tab.title || new URL(tab.url || "about:blank").hostname || "Вкладка Chrome").slice(0, 240);
  const result = await chrome.runtime.sendMessage({ target: "offscreen", type: "start", streamId, token, tabId: tab.id, title, monitor: !!monitor, quality, withSources: !!withSources });
  if (result?.ok) {
    await chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: "#E5484D" });
    await chrome.action.setBadgeText({ tabId: tab.id, text: "REC" });
    await chrome.action.setTitle({ tabId: tab.id, title: "CallDock — вкладка записывается" });
  }
  return result;
}

async function stopped({ tabId, error }) {
  try {
    await chrome.action.setBadgeText({ tabId, text: error ? "!" : "" });
    await chrome.action.setTitle({ tabId, title: error ? "CallDock: " + error : "CallDock — запись вкладок" });
  } catch {
    // The tab is already closed.
  }
  return {};
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || message.target !== "background") return;
  (async () => {
    if (message.type === "start") return await start(message);
    if (message.type === "stopped") return await stopped(message);
    if (message.type === "status" || message.type === "stop")
      return await hasOffscreen() ? await chrome.runtime.sendMessage({ ...message, target: "offscreen" }) : { tracks: [], error: "" };
    throw new Error("Неизвестная команда.");
  })().then(respond, error => respond({ error: explain(error) }));
  return true;
});
