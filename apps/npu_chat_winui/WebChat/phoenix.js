"use strict";

const byId = id => document.getElementById(id);
const chat = byId("chat");
const locked = byId("locked");
const connection = byId("connection");
const transcript = byId("transcript");
const welcome = byId("welcome");
const prompt = byId("prompt");
const send = byId("send");
const attach = byId("attach");
const attachmentInput = byId("attachmentInput");
const attachmentList = byId("draftAttachments");
const mic = byId("mic");
const voiceButton = byId("voice");
const newChat = byId("newChat");
const chatHistory = byId("chatHistory");
const historyDialog = byId("historyDialog");
const notice = byId("notice");
const sessionTitle = byId("sessionTitle");
const storageKey = "phoenix-device-credential";
const standalone = window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
let invite = accessFromLink(location.href);

if (location.hash) history.replaceState(null, "", location.pathname + location.search);
let token = localStorage.getItem(storageKey);
let pairing = false;
let snapshot;
let sending = false;
let switching = false;
let chatRevision = 0;
let displayedChatId;
const drafts = new Map();
const attachmentDrafts = new Map();
let timer;
let rows = [];
let recording = null;
let dictating = false;
let voiceSession;
let readingReply;
const AudioCapture = window.AudioContext || window.webkitAudioContext;
const OfflineAudio = window.OfflineAudioContext || window.webkitOfflineAudioContext;
const recognitionType = window.SpeechRecognition || window.webkitSpeechRecognition;
const localRecognition = recognitionType && "processLocally" in recognitionType.prototype &&
  typeof recognitionType.available === "function";
const speech = window.speechSynthesis;
const device = identifyDevice();
const deviceKey = "phoenix-device-name";
let alias = localStorage.getItem(deviceKey) || "";

function accessFromLink(text) {
  try {
    const url = new URL(text);
    const code = new URLSearchParams(url.hash.slice(1)).get("access");
    if (url.origin === location.origin && /^[0-9a-f]{48}$/i.test(code)) return code.toUpperCase();
  } catch {}
  return null;
}

function identifyDevice() {
  const userAgent = navigator.userAgent;
  const platform = navigator.userAgentData?.platform || navigator.platform;
  const isPad = /iPad/i.test(userAgent) || (/Mac/i.test(platform) && navigator.maxTouchPoints > 1);
  let system = "Unknown platform";
  let name;
  if (isPad) [system, name] = ["iPadOS", "iPad"];
  else if (/iPhone|iPod/i.test(userAgent)) [system, name] = ["iOS", /iPod/i.test(userAgent) ? "iPod" : "iPhone"];
  else if (/Android/i.test(platform + " " + userAgent)) system = "Android";
  else if (/Windows|^Win/i.test(platform)) system = "Windows";
  else if (/Chrome OS|CrOS/i.test(platform + " " + userAgent)) system = "ChromeOS";
  else if (/Mac/i.test(platform)) system = "macOS";
  else if (/Linux/i.test(platform)) system = "Linux";
  const mobile = /Android|iOS|iPadOS/.test(system) || navigator.userAgentData?.mobile === true || /\bMobile\b/i.test(userAgent);
  const mode = (mobile ? "Mobile" : "Desktop") + (standalone ? " web app" : " browser");
  return { system, name: name || (system === "Unknown platform" ? (mobile ? "Mobile device" : "Desktop") : system), mode };
}

function deviceName() { return alias || device.name; }

function renderSession() {
  const title = deviceName() + " · " + (snapshot?.title || "New chat");
  sessionTitle.textContent = title;
  sessionTitle.title = title;
  byId("sessionType").textContent = device.system + " · " + device.mode;
  document.title = title + " — Phoenix AI";
}

byId("renameDevice").addEventListener("click", () => {
  const name = window.prompt("Device name for this browser. Leave blank to use the detected name.", deviceName());
  if (name === null) return;
  alias = Array.from(name.replace(/[\p{Cc}\p{Cf}]/gu, "").trim()).slice(0, 40).join("");
  if (alias) localStorage.setItem(deviceKey, alias);
  else localStorage.removeItem(deviceKey);
  renderSession();
});

if (device.system === "Android" && navigator.userAgentData?.getHighEntropyValues) {
  navigator.userAgentData.getHighEntropyValues(["model"]).then(hints => {
    if (hints.model?.trim()) device.name = hints.model.trim();
    if (snapshot) renderSession();
  }).catch(() => {});
}

function noticeText(text) {
  notice.textContent = text;
  notice.hidden = !text;
}

function iconAction(button, label, icon) {
  if (button.getAttribute("aria-label") === label) return;
  button.setAttribute("aria-label", label);
  button.title = label;
  button.querySelector("use").setAttribute("href", "#icon-" + icon);
}

function controls() {
  const busy = switching || sending || dictating;
  const canChangeChat = !busy && !recording && !!snapshot && !snapshot.pending;
  newChat.hidden = !snapshot;
  newChat.disabled = !canChangeChat;
  chatHistory.hidden = !snapshot?.savedChats?.length;
  chatHistory.disabled = !canChangeChat;
  attach.disabled = !!voiceSession || !!readingReply || busy || !!recording || !snapshot?.canSend;
  send.disabled = attach.disabled || (!prompt.value.trim() && !draftFiles().length);
  for (const button of attachmentList.querySelectorAll("button")) button.disabled = attach.disabled;
  mic.disabled = !!voiceSession || !!readingReply || busy ||
    (recording ? !recording.node || recording.finishing : !snapshot?.canSend || !snapshot.voiceReady);
  iconAction(mic, recording?.node ? "Finish recording" : "Dictate", recording?.node ? "stop" : "mic");
  voiceButton.disabled = !voiceSession && (busy || !!recording || !!readingReply || !snapshot?.canSend);
  iconAction(voiceButton, voiceSession ? "End voice" : "Voice mode", voiceSession ? "stop" : "voice");
  voiceButton.setAttribute("aria-pressed", String(!!voiceSession));
  const canRead = !busy && !recording && !voiceSession && snapshot?.canSend &&
    ((AudioCapture && snapshot.canSpeak) || localVoices().length > 0);
  for (const button of transcript.querySelectorAll("[data-reply-action]")) {
    if (button.dataset.replyAction === "read") {
      const index = Number(button.dataset.replyIndex);
      const active = readingReply?.index === index;
      iconAction(button, active ? "Stop reading" : "Read aloud", active ? "stop" : "speaker");
      button.disabled = !active && !canRead;
    } else if (button.dataset.replyAction !== "copy") button.disabled = !canChangeChat;
  }
}

function lock(expired = false) {
  clearTimeout(timer);
  if (token && localStorage.getItem(storageKey) === token) localStorage.removeItem(storageKey);
  token = null;
  snapshot = undefined;
  displayedChatId = undefined;
  drafts.clear();
  attachmentDrafts.clear();
  renderDraftFiles();
  if (historyDialog.open) historyDialog.close();
  stopAudioSessions();
  renderMessages([]);
  prompt.value = "";
  speech?.cancel();
  chat.hidden = true;
  byId("session").hidden = true;
  document.title = "Phoenix AI";
  locked.hidden = false;
  connection.textContent = expired ? "Access expired" : "Pairing required";
  byId("accessHint").textContent = expired ? "Open Share Phoenix on your PC for a fresh QR code or access link."
    : "Open Share Phoenix on your PC to scan the QR code or copy the access link.";
}

byId("pair").addEventListener("submit", async event => {
  event.preventDefault();
  if (pairing) return;
  const input = byId("accessLink");
  const pasted = accessFromLink(input.value.trim());
  if (!pasted) {
    byId("accessHint").textContent = "Paste the full access link for this PC.";
    return;
  }
  input.value = "";
  await pairDevice(pasted);
});

async function pairDevice(code) {
  if (pairing) return;
  clearTimeout(timer);
  pairing = true;
  invite = null;
  connection.textContent = "Pairing…";
  try {
    const response = await fetch("/api/pair", {
      method: "POST",
      headers: { Authorization: "Bearer " + code },
      cache: "no-store"
    });
    if (response.status === 401) { lock(true); return; }
    if (!response.ok) throw new Error("Couldn’t pair with Phoenix.");
    const paired = await response.json();
    if (!/^[0-9A-F]{64}$/.test(paired.token)) throw new Error("Invalid device credential.");
    token = paired.token;
    localStorage.setItem(storageKey, token);
    await refresh();
  } catch {
    lock();
    byId("accessHint").textContent = "Couldn’t pair with Phoenix. Check LAN access on the PC and try again.";
  } finally { pairing = false; }
}

async function request(method = "GET", body, signal, path = "/api/chat") {
  return fetch(path, {
    method,
    signal,
    cache: "no-store",
    headers: {
      Authorization: "Bearer " + token,
      ...(body ? { "Content-Type": "application/json" } : {})
    },
    body: body ? JSON.stringify(body) : undefined
  });
}

function draftFiles(chatId = displayedChatId) { return attachmentDrafts.get(chatId) || []; }

function renderDraftFiles() {
  const files = draftFiles();
  attachmentList.replaceChildren();
  attachmentList.hidden = !files.length;
  files.forEach((file, index) => {
    const chip = document.createElement("div");
    chip.className = "attachment-chip";
    const name = document.createElement("span");
    name.textContent = name.title = file.name;
    const remove = document.createElement("button");
    remove.type = "button";
    remove.textContent = "×";
    remove.setAttribute("aria-label", "Remove " + file.name);
    remove.addEventListener("click", () => {
      files.splice(index, 1);
      if (!files.length) attachmentDrafts.delete(displayedChatId);
      renderDraftFiles();
      controls();
    });
    chip.append(name, remove);
    attachmentList.append(chip);
  });
}

function stageFiles(files) {
  if (attach.disabled || !displayedChatId) return;
  const ready = draftFiles().slice();
  let size = ready.reduce((sum, file) => sum + file.size, 0);
  let skipped = false;
  for (const file of files) {
    if (ready.length >= 4 || file.size === 0 || file.size > 4 * 1024 * 1024 || size + file.size > 8 * 1024 * 1024) {
      skipped = true;
      continue;
    }
    ready.push(file);
    size += file.size;
  }
  if (ready.length) attachmentDrafts.set(displayedChatId, ready);
  renderDraftFiles();
  controls();
  noticeText(skipped ? "Attach up to 4 files, with up to 4 MB per file and 8 MB total." : "");
}

function encodeFile(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error("A file couldn’t be read. Try selecting it again."));
    reader.onload = () => resolve({ name: file.name.slice(0, 120), content: reader.result.split(",", 2)[1] });
    reader.readAsDataURL(file);
  });
}

async function submitChat(chatId, text, signal, files = []) {
  const attachments = files.length ? await Promise.all(files.map(encodeFile)) : undefined;
  signal?.throwIfAborted();
  const response = await request("POST", { chatId, text, attachments }, signal);
  if (response.status === 401) { lock(true); return; }
  if (response.status === 413) throw new Error("The message and attachments are too large.");
  const result = await response.json();
  if (!response.ok) throw new Error(result.message || "Phoenix couldn’t accept this message.");
  return result;
}

function messageRow(message) {
  const article = document.createElement("article");
  if (message.role === "user") article.className = "user";
  const speaker = document.createElement("div");
  speaker.className = "speaker";
  speaker.textContent = message.role === "user" ? "You" : message.speaker || "Phoenix";
  const text = document.createElement("p");
  text.className = "message";
  text.textContent = message.text;
  article.append(speaker);
  if (message.text) article.append(text);
  for (const file of message.attachments || []) {
    const detail = document.createElement("details");
    detail.className = "attachment-file";
    const label = document.createElement("summary");
    if (file.preview && file.preview.length <= 100000 && /^[A-Za-z0-9+/=]+$/.test(file.preview)) {
      const image = document.createElement("img");
      image.alt = "";
      image.src = "data:image/jpeg;base64," + file.preview;
      label.append(image);
    }
    label.append(document.createTextNode(file.name));
    detail.append(label);
    if (file.note || file.image) {
      const note = document.createElement("small");
      note.textContent = file.note || "Text extracted from image";
      detail.append(note);
    }
    if (file.text) {
      const pre = document.createElement("pre");
      pre.textContent = file.text;
      detail.append(pre);
    }
    article.append(detail);
  }
  for (const snippet of message.snippets || []) {
    const details = document.createElement("details");
    details.className = "snippet";
    const summary = document.createElement("summary");
    summary.textContent = snippet.language ? "Code · " + snippet.language : "Code snippet";
    const pre = document.createElement("pre");
    const code = document.createElement("code");
    code.textContent = snippet.code;
    pre.append(code);
    details.append(summary, pre);
    article.append(details);
  }

  const events = [];
  for (const event of message.activity || []) {
    if (event.status !== "Skill loaded") { events.push(event); continue; }
    const skill = document.createElement("small");
    skill.className = "skill-used";
    skill.textContent = `Skill loaded · ${event.name}`;
    article.append(skill);
  }
  if (events.length || message.execution) {
    const activity = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = "Details";
    activity.append(summary);
    if (message.execution) {
      const row = document.createElement("div");
      row.textContent = [message.execution.model, message.execution.device].filter(Boolean).join(" · ");
      activity.append(row);
    }
    for (const event of events) {
      const row = document.createElement("div");
      row.textContent = [event.name, event.status, event.detail].filter(Boolean).join(" · ");
      activity.append(row);
    }
    article.append(activity);
  }
  if (message.sources?.length || message.sourceNotes) {
    const sources = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = message.sources?.length ? `Sources (${message.sources.length})` : "Sources";
    sources.append(summary);
    for (const source of message.sources || []) {
      const url = new URL(source.url);
      if (url.protocol !== "https:" && url.protocol !== "http:") continue;
      const link = document.createElement("a");
      link.href = url.href;
      link.textContent = source.title;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      const item = document.createElement("p");
      item.append(link);
      sources.append(item);
    }
    if (message.sourceNotes) {
      const note = document.createElement("p");
      note.textContent = message.sourceNotes;
      sources.append(note);
    }
    article.append(sources);
  }
  if (Number.isInteger(message.replyIndex)) article.append(replyActions(message));
  return article;
}

function replyActions(message) {
  const row = document.createElement("div");
  row.className = "reply-actions";
  row.setAttribute("role", "group");
  row.setAttribute("aria-label", "Response actions");
  function add(action, label, icon, click, pressed) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "icon-button reply-action";
    button.dataset.replyAction = action;
    button.dataset.replyIndex = message.replyIndex;
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("aria-hidden", "true");
    svg.append(document.createElementNS("http://www.w3.org/2000/svg", "use"));
    button.append(svg);
    iconAction(button, label, icon);
    if (pressed !== undefined) button.setAttribute("aria-pressed", String(pressed));
    button.addEventListener("click", click);
    row.append(button);
  }
  add("copy", "Copy response", "copy", async () => {
    try {
      await navigator.clipboard.writeText(message.copyText ?? message.text);
      noticeText("Response copied.");
    } catch { noticeText("The browser couldn’t copy the response."); }
  });
  add("rate", "Thumbs up", "like", () => rateReply(message, 1), message.rating === 1);
  add("rate", "Thumbs down", "dislike", () => rateReply(message, -1), message.rating === -1);
  if (message.speech) add("read", "Read aloud", "speaker", () => readReply(message));
  add("branch", "Branch in new chat", "branch", () => changeChat(null, message.replyIndex));
  return row;
}

function renderMessages(messages, changedChat = false) {
  const follow = changedChat || transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight <= 2;
  for (let index = messages.length; index < rows.length; index++) rows[index].element.remove();
  rows.length = Math.min(rows.length, messages.length);
  for (let index = 0; index < messages.length; index++) {
    const key = JSON.stringify(messages[index]);
    const previous = rows[index];
    if (previous?.key === key) continue;
    const element = messageRow(messages[index]);
    if (previous) previous.element.replaceWith(element);
    else transcript.append(element);
    rows[index] = { key, element };
  }
  welcome.hidden = messages.length > 0;
  if (follow) transcript.scrollTop = transcript.scrollHeight;
}

async function refresh() {
  if (!token) return;
  const credential = token;
  const revision = chatRevision;
  try {
    if (document.hidden || sending || switching) return;
    const response = await request();
    if (revision !== chatRevision || token !== credential) return;
    if (response.status === 401) {
      const code = invite;
      lock(true);
      if (code) await pairDevice(code);
      return;
    }
    if (!response.ok) throw new Error("Chat unavailable");
    const next = await response.json();
    if (revision === chatRevision && token === credential) applySnapshot(next);
  } catch {
    if (revision === chatRevision && token === credential) {
      snapshot = undefined;
      connection.textContent = "Reconnecting to PC…";
    }
  } finally {
    controls();
    if (token === credential) timer = setTimeout(refresh, voiceSession ? 350 : snapshot?.canSend ? 2000 : 650);
  }
}

function applySnapshot(next) {
  const changedChat = next.chatId !== displayedChatId;
  if (changedChat) {
    if (displayedChatId) {
      if (prompt.value) drafts.set(displayedChatId, prompt.value);
      else drafts.delete(displayedChatId);
      stopAudioSessions();
      speech?.cancel();
    }
    displayedChatId = next.chatId;
    prompt.value = drafts.get(displayedChatId) || "";
    renderDraftFiles();
  }
  snapshot = next;
  invite = null;
  locked.hidden = true;
  chat.hidden = false;
  byId("session").hidden = false;
  renderSession();
  connection.textContent = next.status;
  renderMessages(next.messages, changedChat);
}

async function changeChat(openChatId = null, branchReplyIndex = null) {
  if (newChat.disabled || !snapshot) return;
  stopAudioSessions();
  if (historyDialog.open) historyDialog.close();
  await updateChat("/api/chat/select", { openChatId, branchReplyIndex }, "Phoenix couldn’t switch chats.");
}

async function rateReply(message, value) {
  if (newChat.disabled || !snapshot) return;
  const rating = message.rating === value ? null : value;
  await updateChat("/api/chat/rating", { replyIndex: message.replyIndex, rating }, "Phoenix couldn’t save that rating.");
}

async function updateChat(path, fields, failure) {
  const chatId = snapshot.chatId;
  switching = true;
  chatRevision++;
  controls();
  noticeText("");
  try {
    const response = await request("POST", { chatId, ...fields }, undefined, path);
    if (response.status === 401) { lock(true); return; }
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || failure);
    applySnapshot(result.chat);
  } catch (error) {
    noticeText(error instanceof TypeError ? failure + " Check your connection and try again." : error.message);
  } finally {
    switching = false;
    controls();
  }
}

newChat.addEventListener("click", () => changeChat());
chatHistory.addEventListener("click", () => {
  const list = byId("savedChats");
  list.replaceChildren();
  for (const saved of snapshot?.savedChats || []) {
    const row = byId("savedChatRow").content.cloneNode(true);
    row.querySelector("span").textContent = saved.title;
    const open = row.querySelector("button");
    iconAction(open, "Open " + saved.title, "open");
    open.addEventListener("click", () => changeChat(saved.chatId));
    list.append(row);
  }
  if (list.childElementCount) historyDialog.showModal();
});
byId("closeHistory").addEventListener("click", () => historyDialog.close());

byId("composer").addEventListener("submit", async event => {
  event.preventDefault();
  if (send.disabled || !snapshot) return;
  const text = prompt.value.trim();
  const chatId = snapshot.chatId;
  const files = draftFiles(chatId);
  sending = true;
  controls();
  noticeText("");
  try {
    if (!await submitChat(chatId, text, undefined, files)) return;
    attachmentDrafts.delete(chatId);
    renderDraftFiles();
    if (prompt.value.trim() === text) prompt.value = "";
    if (snapshot?.chatId === chatId) {
      snapshot.canSend = false;
      snapshot.pending = true;
    }
  } catch (error) {
    noticeText(error instanceof TypeError ? "Check the conversation after reconnecting before sending again." : error.message);
  } finally {
    sending = false;
    controls();
  }
});

prompt.addEventListener("input", controls);
attach.addEventListener("click", () => attachmentInput.click());
attachmentInput.addEventListener("change", () => {
  stageFiles(attachmentInput.files);
  attachmentInput.value = "";
});
prompt.addEventListener("paste", event => {
  if (attach.disabled) return;
  const data = event.clipboardData;
  const files = Array.from(data?.files || []);
  if (files.length) {
    event.preventDefault();
    stageFiles(files);
    return;
  }
  const text = data?.getData("text/plain") || "";
  if (text.length >= 1200 || text.split("\n").length > 12) {
    event.preventDefault();
    stageFiles([new File([text], "Pasted text.txt", { type: "text/plain" })]);
  }
});
prompt.addEventListener("keydown", event => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !event.isComposing && !send.disabled) {
    event.preventDefault();
    byId("composer").requestSubmit();
  }
});

mic.hidden = false;
mic.addEventListener("click", async () => {
  if (recording?.node) {
    recording.finishing = true;
    recording.node.port.postMessage("finish");
    controls();
    return;
  }
  if (snapshot?.canDictate && canCaptureAudio()) {
    await startPcDictation();
    return;
  }
  if (!localRecognition) {
    noticeText("Dictation is unavailable here. You can use your keyboard’s microphone if it has one.");
    return;
  }
  dictating = true;
  controls();
  noticeText("");
  try {
    const lang = navigator.language || "en-US";
    const available = await recognitionType.available({ langs: [lang], processLocally: true });
    if (available !== "available") {
      noticeText("Offline dictation isn’t installed for this browser language. You can still type.");
      dictating = false;
      controls();
      return;
    }
    const recognition = new recognitionType();
    recognition.lang = lang;
    recognition.processLocally = true;
    recognition.onresult = event => {
      appendDictation(event.results[0][0].transcript);
    };
    recognition.onerror = () => noticeText("Dictation couldn’t start. Check microphone access or type your message.");
    recognition.onend = () => {
      dictating = false;
      controls();
      if (notice.textContent === "Listening…") noticeText("");
    };
    recognition.start();
    noticeText("Listening…");
  } catch {
    dictating = false;
    controls();
    noticeText("Offline dictation is unavailable in this browser.");
  }
});

function appendDictation(text) {
  const separator = prompt.value.trim() ? " " : "";
  prompt.value = (prompt.value + separator + text).slice(0, prompt.maxLength);
  controls();
}

function canCaptureAudio() {
  return AudioCapture && OfflineAudio && navigator.mediaDevices?.getUserMedia &&
    "audioWorklet" in AudioCapture.prototype;
}

async function openAudio(job) {
  job.context = new AudioCapture();
  await Promise.all([
    job.context.resume(),
    job.context.audioWorklet.addModule("/dictation-worklet.js"),
    navigator.mediaDevices.getUserMedia({ audio: true }).then(stream => {
      for (const track of stream.getTracks()) {
        if (job.signal.aborted) track.stop();
        else track.enabled = false;
      }
      if (!job.signal.aborted) job.stream = stream;
    })
  ]);
  job.signal.throwIfAborted();
}

function captureAudio(job, automatic) {
  job.signal.throwIfAborted();
  const tracks = job.stream.getAudioTracks();
  if (!tracks.some(track => track.readyState === "live")) throw new Error("The microphone was disconnected.");
  const source = job.context.createMediaStreamSource(job.stream);
  const node = new AudioWorkletNode(job.context, "phoenix-recorder",
    { channelCount: 1, channelCountMode: "explicit", processorOptions: { automatic } });
  job.node = node;
  return new Promise((resolve, reject) => {
    function done(samples, error) {
      job.node = null;
      job.signal.removeEventListener("abort", aborted);
      for (const track of tracks) {
        track.removeEventListener("ended", ended);
        track.enabled = false;
      }
      source.disconnect();
      node.disconnect();
      node.port.close();
      if (error) reject(error);
      else resolve(samples);
    }
    const aborted = () => done(null, job.signal.reason);
    const ended = () => done(null, new Error("The microphone was disconnected."));
    job.signal.addEventListener("abort", aborted, { once: true });
    node.onprocessorerror = () => done(null, new Error("The browser couldn’t record audio."));
    node.port.onmessage = event => done(event.data);
    for (const track of tracks) {
      track.addEventListener("ended", ended, { once: true });
      track.enabled = true;
    }
    try { source.connect(node).connect(job.context.destination); }
    catch (error) { done(null, error); }
  });
}

function closeAudio(job) {
  if (!job) return;
  job.controller.abort();
  job.stream?.getTracks().forEach(track => track.stop());
  job.context?.close().catch(() => {});
}

function stopAudioSessions() {
  const manual = recording;
  const activeVoice = voiceSession;
  recording = null;
  voiceSession = undefined;
  closeAudio(manual);
  closeAudio(activeVoice);
  stopReadingReply();
  controls();
}

async function transcribeAudio(job, samples) {
  if (!samples.length) return "";
  const rate = job.context.sampleRate;
  const targetRate = 16000;
  const resampler = new OfflineAudio(1, Math.min(30 * targetRate, Math.ceil(samples.length * targetRate / rate)), targetRate);
  const buffer = resampler.createBuffer(1, samples.length, rate);
  buffer.copyToChannel(samples, 0);
  const source = resampler.createBufferSource();
  source.buffer = buffer;
  source.connect(resampler.destination);
  source.start();
  const audio = (await resampler.startRendering()).getChannelData(0);
  job.signal.throwIfAborted();
  const pcm = new DataView(new ArrayBuffer(audio.length * 2));
  for (let i = 0; i < audio.length; i++) {
    pcm.setInt16(i * 2, Math.round(Math.max(-1, Math.min(1, audio[i])) * 32767), true);
  }
  let response;
  do {
    if (job.chatId) await waitForVoiceState(job, () => true, true);
    response = await fetch("/api/dictation", {
      method: "POST",
      signal: job.signal,
      headers: { Authorization: "Bearer " + token, "Content-Type": "application/octet-stream" },
      body: pcm
    });
    if (!job.chatId || response.status !== 409) break;
    await new Promise(resolve => setTimeout(resolve, 350));
  } while (!job.signal.aborted);
  job.signal.throwIfAborted();
  if (response.status === 401) { lock(true); return ""; }
  const result = await response.json();
  if (!response.ok) throw new Error(result.message || "Phoenix couldn’t transcribe the recording.");
  return result.text?.trim() || "";
}

async function startPcDictation() {
  const controller = new AbortController();
  const job = { controller, signal: controller.signal };
  recording = job;
  noticeText("Opening microphone…");
  controls();
  try {
    await openAudio(job);
    const capture = captureAudio(job, false);
    noticeText("Recording. Finish to transcribe on your PC over Wi‑Fi. Limit 30 seconds.");
    controls();
    const samples = await capture;
    job.stream.getTracks().forEach(track => track.stop());
    noticeText("Transcribing on your PC…");
    controls();
    const text = await transcribeAudio(job, samples);
    if (job.signal.aborted) return;
    if (text) appendDictation(text);
    noticeText(text ? "" : "No speech was detected.");
  } catch (error) {
    if (!job.signal.aborted) noticeText(job.stream ? error.message : "Couldn’t open the microphone. Check its permission.");
  } finally {
    if (recording === job) stopAudioSessions();
  }
}

async function waitForVoiceState(job, ready, needsWorker = false) {
  const until = performance.now() + 180000;
  while (performance.now() < until) {
    job.signal.throwIfAborted();
    if (snapshot?.chatId && snapshot.chatId !== job.chatId) throw new Error("The open chat changed on the PC.");
    if (snapshot?.canSend && (!needsWorker || snapshot.voiceReady) && ready(snapshot.messages)) return;
    await new Promise(resolve => setTimeout(resolve, 180));
  }
  throw new Error("Phoenix took too long to respond. You can check the conversation for the result.");
}

async function playVoiceReply(job, replyIndex, brief = false) {
  async function prepareSentence(offset) {
    while (true) {
      await waitForVoiceState(job, messages => messages[replyIndex]?.role === "assistant", true);
      const response = await request("POST", { chatId: job.chatId, replyIndex, offset, brief }, job.signal, "/api/speech");
      if (response.status === 401) { lock(true); return null; }
      if (response.status === 409) { await new Promise(resolve => setTimeout(resolve, 350)); continue; }
      const part = await response.json();
      if (!response.ok) throw new Error(part.message || "Phoenix couldn’t prepare the voice reply.");
      const buffer = part.audio ? await job.context.decodeAudioData(
        Uint8Array.from(atob(part.audio), value => value.charCodeAt(0)).buffer) : null;
      return { buffer, nextOffset: part.nextOffset };
    }
  }

  let pending = prepareSentence(0);
  while (pending) {
    const sentence = await pending;
    if (!sentence) return;
    pending = Number.isInteger(sentence.nextOffset) ? prepareSentence(sentence.nextOffset) : null;
    // The next request can finish before playback. Its error is handled on the next iteration.
    pending?.catch(() => {});
    if (!sentence.buffer) continue;
    job.signal.throwIfAborted();
    noticeText("Speaking…");
    const source = job.context.createBufferSource();
    source.buffer = sentence.buffer;
    source.connect(job.context.destination);
    await new Promise((resolve, reject) => {
      const aborted = () => {
        source.stop();
        reject(job.signal.reason);
      };
      job.signal.addEventListener("abort", aborted, { once: true });
      source.onended = () => {
        job.signal.removeEventListener("abort", aborted);
        source.disconnect();
        resolve();
      };
      source.start();
    });
  }
}

voiceButton.addEventListener("click", async () => {
  if (voiceSession) {
    const { chatId, userIndex } = voiceSession;
    stopAudioSessions();
    noticeText("Voice mode ended.");
    if (Number.isInteger(userIndex)) {
      try { await request("POST", { chatId, userIndex }, undefined, "/api/chat/stop-lookup"); }
      catch { /* Audio has already stopped on this device. */ }
    }
    return;
  }
  if (!canCaptureAudio() || !snapshot?.canDictate || !snapshot.canSpeak) {
    noticeText("Voice mode needs microphone access and local transcription and Kokoro voice on the PC.");
    return;
  }
  speech?.cancel();
  const controller = new AbortController();
  const job = { controller, signal: controller.signal, chatId: snapshot.chatId };
  voiceSession = job;
  controls();
  noticeText("Opening microphone…");
  try {
    await openAudio(job);
    while (!job.signal.aborted) {
      await waitForVoiceState(job, () => true, true);
      noticeText("Listening… Phoenix sends after a pause.");
      const samples = await captureAudio(job, true);
      if (!samples.length) continue;
      noticeText("Transcribing on your PC…");
      const text = await transcribeAudio(job, samples);
      job.signal.throwIfAborted();
      if (!text) continue;
      noticeText("Waiting for Phoenix…");
      const turn = await submitChat(job.chatId, text, job.signal, draftFiles(job.chatId));
      if (!turn || !Number.isInteger(turn.userIndex)) break;
      attachmentDrafts.delete(job.chatId);
      renderDraftFiles();
      job.userIndex = turn.userIndex;
      await waitForVoiceState(job, messages => messages[turn.userIndex]?.role === "user" &&
        messages[turn.userIndex].text === text && messages[turn.userIndex + 1]?.role === "assistant");
      noticeText("Preparing voice…");
      await playVoiceReply(job, turn.userIndex + 1, true);
      job.userIndex = undefined;
    }
  } catch (error) {
    if (!job.signal.aborted) noticeText(job.stream ? error.message : "Couldn’t open the microphone. Check its permission.");
  } finally {
    if (voiceSession === job) stopAudioSessions();
  }
});

window.addEventListener("pagehide", stopAudioSessions);
document.addEventListener("visibilitychange", () => {
  if (document.hidden && voiceSession) {
    stopAudioSessions();
    noticeText("Voice mode ended when the app went into the background.");
  }
});

function localVoices() { return speech?.getVoices().filter(voice => voice.localService) || []; }
speech?.addEventListener("voiceschanged", controls);

function stopReadingReply() {
  const job = readingReply;
  if (!job) return;
  readingReply = undefined;
  if (job.context) closeAudio(job);
  else speech?.cancel();
}

async function readReply(message) {
  const stop = readingReply?.index === message.replyIndex;
  stopReadingReply();
  if (stop || !snapshot || !message.speech) {
    noticeText("");
    controls();
    return;
  }
  const job = { index: message.replyIndex, chatId: snapshot.chatId, controller: new AbortController() };
  job.signal = job.controller.signal;
  readingReply = job;
  controls();
  function done(error) {
    if (readingReply !== job) return;
    stopReadingReply();
    noticeText(error || "");
    controls();
  }
  if (AudioCapture && snapshot.canSpeak) {
    try {
      job.context = new AudioCapture();
      await job.context.resume();
      noticeText("Preparing voice…");
      await playVoiceReply(job, job.index);
      done();
    } catch (error) {
      if (!job.signal.aborted) done(error.message || "Phoenix couldn’t read the response.");
    }
    return;
  }
  const voices = localVoices();
  const language = navigator.language.split("-")[0];
  const voice = voices.find(item => item.lang.startsWith(language)) || voices[0];
  if (!voice) { done("No local voice is available."); return; }
  const utterance = new SpeechSynthesisUtterance(message.speech);
  utterance.voice = voice;
  utterance.onend = () => done();
  utterance.onerror = () => done("The local voice couldn’t read this response.");
  speech.speak(utterance);
}

const install = byId("install");
const setup = byId("setup");
const installApp = byId("installApp");
let installPrompt;
install.hidden = standalone;

function showSetup(certificateOnly) {
  byId("setupTitle").textContent = certificateOnly ? "Certificate setup" : "Install Phoenix AI";
  byId("installSteps").hidden = certificateOnly;
  byId("certificate").open = certificateOnly;
  byId("certificateHint").textContent = ({
    Android: "In Android Settings, look for Encryption & credentials, then Install a certificate → CA certificate.",
    iOS: "Install the certificate in Settings, then enable full trust under General → About → Certificate Trust Settings.",
    iPadOS: "Install the certificate in Settings, then enable full trust under General → About → Certificate Trust Settings.",
    Windows: "Open the certificate and import it into Trusted Root Certification Authorities for the current user."
  })[device.system] || "Import the certificate as a trusted root using your device’s certificate settings.";
  byId("installHint").textContent = installPrompt ? "Add Phoenix AI to your home screen or app launcher."
    : /^(iOS|iPadOS)$/.test(device.system) ? "In Safari, tap Share → Add to Home Screen, then turn on Open as Web App."
    : device.system === "Android" ? "Open the browser menu and choose Install app or Add to Home screen."
    : "Open the browser menu and look for Install Phoenix AI or Install this site as an app.";
  installApp.hidden = !installPrompt;
  setup.showModal();
}

window.addEventListener("beforeinstallprompt", event => {
  event.preventDefault();
  installPrompt = event;
  if (setup.open && !byId("installSteps").hidden) {
    installApp.hidden = false;
    byId("installHint").textContent = "Add Phoenix AI to your home screen or app launcher.";
  }
});

function installed() {
  install.hidden = true;
  if (setup.open) setup.close();
}

window.addEventListener("appinstalled", installed);
install.addEventListener("click", () => showSetup(false));
byId("certSetup").addEventListener("click", () => showSetup(true));
byId("closeSetup").addEventListener("click", () => setup.close());
installApp.addEventListener("click", async () => {
  const pending = installPrompt;
  if (!pending) return;
  installPrompt = undefined;
  setup.close();
  try {
    await pending.prompt();
    if ((await pending.userChoice).outcome === "accepted") installed();
  } catch {}
});

if (token) refresh();
else if (invite) pairDevice(invite);
else lock();
