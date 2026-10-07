// Records the captured tabs: one MediaRecorder per tab, one-second WebM chunks streamed to CallDock in order.
// A chunk is retried a few times; if one is lost for good the tab stops, because a gap would corrupt the file.
// A pause of the CallDock recording pauses every tab: MediaRecorder leaves the paused time out of the file, as the other
// tracks do, so they stay in step.
const BASE = "http://127.0.0.1:47831";
const MAX_TABS = 4;
// Video costs most of the processor. On a weak computer four 1080p encodes starve the sound encoder and the last
// seconds of sound are lost, so 720p at 15 frames is the default and «Без видео» is there for four streams at once.
const QUALITY = {
  high: { width: 1920, height: 1080, fps: 30, bitrate: 6_000_000 },
  normal: { width: 1280, height: 720, fps: 15, bitrate: 2_500_000 },
  audio: null
};
const tracks = new Map();
let lastError = "";
/** The stream of pause and resume from CallDock, open while tabs record. */
let events;

async function api(token, path, data, binary = false) {
  let response;
  try {
    response = await fetch(BASE + path, {
      method: "POST",
      headers: { Authorization: "Bearer " + token, "Content-Type": binary ? "application/octet-stream" : "application/json" },
      body: binary ? data : JSON.stringify(data),
      signal: AbortSignal.timeout(8000)
    });
  } catch {
    throw new Error("CallDock не отвечает. Проверьте, что программа запущена на этом компьютере.");
  }
  const text = await response.text();
  if (!response.ok) {
    if (response.status === 401) throw new Error("Код подключения не подходит. Скопируйте его заново в настройках CallDock.");
    let message = "";
    try { message = JSON.parse(text).error || ""; } catch { message = text; }
    throw new Error(message || `CallDock: ошибка ${response.status}`);
  }
  return text ? JSON.parse(text) : {};
}

async function start(message) {
  if (tracks.has(message.tabId)) throw new Error("Эта вкладка уже записывается.");
  if (tracks.size >= MAX_TABS) throw new Error(`Уже записываются ${MAX_TABS} вкладки — это предел.`);
  const quality = QUALITY[message.quality] === undefined ? QUALITY.normal : QUALITY[message.quality];
  let media, audio, id;
  try {
    media = await navigator.mediaDevices.getUserMedia({
      audio: { mandatory: { chromeMediaSource: "tab", chromeMediaSourceId: message.streamId } },
      video: quality
        ? { mandatory: { chromeMediaSource: "tab", chromeMediaSourceId: message.streamId, maxWidth: quality.width, maxHeight: quality.height, maxFrameRate: quality.fps } }
        : false
    });
    audio = new AudioContext();
    await audio.resume();
    const input = audio.createMediaStreamSource(media);
    const analyser = audio.createAnalyser();
    analyser.fftSize = 1024;
    input.connect(analyser);
    // Chrome mutes a captured tab; playing it back keeps it audible for the person.
    if (message.monitor) input.connect(audio.destination);

    const mimeType = quality ? "video/webm;codecs=vp8,opus" : "audio/webm;codecs=opus";
    if (!MediaRecorder.isTypeSupported(mimeType)) throw new Error("Этот браузер не умеет записывать WebM с VP8 и Opus.");
    let paused;
    ({ id, paused } = await api(message.token, "/tabs/start",
      { title: message.title, video: !!quality, withSources: !!message.withSources, canPause: true }));
    const recorder = new MediaRecorder(media, quality
      ? { mimeType, videoBitsPerSecond: quality.bitrate, audioBitsPerSecond: 192_000 }
      : { mimeType, audioBitsPerSecond: 192_000 });
    const state = {
      recorder, media, audio, id, title: message.title, token: message.token, tabId: message.tabId,
      sequence: 0, queuedBytes: 0, pending: Promise.resolve(), error: "", stopping: false, peak: 0, startedAt: Date.now(),
      pausedMs: 0, pausedAt: 0
    };
    tracks.set(message.tabId, state);

    recorder.ondataavailable = event => {
      if (!event.data.size || state.error) return;
      state.queuedBytes += event.data.size;
      if (state.queuedBytes > 64 * 1024 * 1024) { state.error = "CallDock не успевает сохранять видео (очередь 64 МБ)."; stop(state); return; }
      const sequence = state.sequence++;
      state.pending = state.pending.then(async () => {
        let failure;
        for (let attempt = 0; attempt < 3; attempt++) {
          try {
            const result = await api(state.token, `/tabs/${id}/chunk/${sequence}`, event.data, true);
            state.queuedBytes -= event.data.size;
            follow(state, result);
            return;
          } catch (error) {
            failure = error;
            await new Promise(resolve => setTimeout(resolve, 300 * (attempt + 1)));
          }
        }
        throw failure;
      });
      // The chain stays rejected: uploading after a missing chunk would corrupt the file.
      state.pending.catch(error => { state.error ||= error.message; stop(state); });
    };
    recorder.onerror = event => { state.error ||= event.error?.message || "Ошибка записи Chrome."; stop(state); };
    // The moment the recorder really started places the tab on CallDock's timeline, next to its other tracks.
    recorder.onstart = () => { api(state.token, `/tabs/${id}/started`, { at: Date.now() }).catch(() => {}); };
    recorder.onstop = async () => {
      clearInterval(state.timer);
      try { await state.pending; } catch (error) { state.error ||= error.message; }
      if (tracks.size === 1) { events?.abort(); events = undefined; }
      try { await api(state.token, `/tabs/${id}/finish`, { error: state.error || null }); }
      catch (error) { state.error ||= error.message; }
      media.getTracks().forEach(track => track.stop());
      await audio.close().catch(() => {});
      tracks.delete(state.tabId);
      lastError = state.error;
      // Offscreen documents only have chrome.runtime: the toolbar badge is the service worker's job.
      chrome.runtime.sendMessage({ target: "background", type: "stopped", tabId: state.tabId, error: state.error }).catch(() => {});
    };
    media.getTracks()[0].onended = () => { state.error ||= "Вкладка закрыта или Chrome прекратил захват."; stop(state); };

    const samples = new Float32Array(analyser.fftSize);
    let pinging = false;
    state.timer = setInterval(async () => {
      if (pinging || state.stopping) return;
      pinging = true;
      try {
        analyser.getFloatTimeDomainData(samples);
        let peak = 0;
        for (const value of samples) peak = Math.max(peak, Math.abs(value));
        state.peak = peak;
        const result = await api(state.token, `/tabs/${id}/ping`, { peak });
        follow(state, result);
      } catch (error) { state.error ||= error.message; stop(state); }
      finally { pinging = false; }
    }, 350);

    recorder.start(1000);
    // A tab added while CallDock is paused waits for the recording to go on.
    if (paused) pause(state, true);
    listen(message.token);
    lastError = "";
    return { ok: true };
  } catch (error) {
    media?.getTracks().forEach(track => track.stop());
    if (audio && audio.state !== "closed") await audio.close().catch(() => {});
    if (id) await api(message.token, `/tabs/${id}/finish`, { error: error.message }).catch(() => {});
    throw error;
  }
}

function stop(state) {
  if (state.stopping) return;
  state.stopping = true;
  if (state.recorder.state !== "inactive") state.recorder.stop();
}

/** CallDock's answer to a chunk or a ping: stop this tab, or pause and resume it with the recording. */
function follow(state, result) {
  if (result.stop) stop(state);
  else if (typeof result.paused === "boolean") pause(state, result.paused);
}

function pause(state, paused) {
  if (state.stopping) return;
  const recorder = state.recorder;
  if (paused && recorder.state === "recording") {
    recorder.pause();
    state.pausedAt = Date.now();
  } else if (!paused && recorder.state === "paused") {
    recorder.resume();
    state.pausedMs += Date.now() - state.pausedAt;
    state.pausedAt = 0;
  }
}

/**
 * CallDock reports pause and resume the moment they happen, one JSON line each, so the tabs leave out the same time as
 * its other tracks. The pings repeat the state: if this stream breaks, a pause reaches the tabs a ping later.
 */
async function listen(token) {
  if (events) return;
  const controller = new AbortController();
  events = controller;
  while (tracks.size && !controller.signal.aborted) {
    try {
      const response = await fetch(BASE + "/events", { headers: { Authorization: "Bearer " + token }, signal: controller.signal });
      if (!response.ok) throw new Error(`CallDock: ошибка ${response.status}`);
      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      let buffer = "";
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += value;
        let end;
        while ((end = buffer.indexOf("\n")) >= 0) {
          const line = buffer.slice(0, end).trim();
          buffer = buffer.slice(end + 1);
          if (!line) continue;
          const message = JSON.parse(line);
          if (typeof message.paused === "boolean") tracks.forEach(state => pause(state, message.paused));
        }
      }
    } catch {
      // CallDock was closed or restarted: the pings notice that. While tabs record, try again in a second.
    }
    if (!controller.signal.aborted) await new Promise(resolve => setTimeout(resolve, 1000));
  }
  if (events === controller) events = undefined;
}

/** What has been recorded of a tab, its pauses left out (milliseconds). */
function recorded(state) {
  return Date.now() - state.startedAt - state.pausedMs - (state.pausedAt ? Date.now() - state.pausedAt : 0);
}

function status() {
  return {
    tracks: [...tracks.values()].map(s => ({
      id: s.id, tabId: s.tabId, title: s.title, peak: s.peak, stopping: s.stopping,
      paused: s.recorder.state === "paused", recorded: recorded(s)
    })),
    error: lastError
  };
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || message.target !== "offscreen") return;
  (async () => {
    if (message.type === "start") return await start(message);
    if (message.type === "stop") {
      if (message.tabId != null) { const state = tracks.get(message.tabId); if (state) stop(state); }
      else tracks.forEach(stop);
    }
    return status();
  })().then(respond, error => respond({ error: error.message }));
  return true;
});
