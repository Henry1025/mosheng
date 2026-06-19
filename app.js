const state = {
  phase: "setup",
  listening: false,
  recognition: null,
  transcript: "",
  clipboardRestore: true,
  inputMode: "preview",
  autoPunctuation: true,
  keepPreviewOnFailure: true,
  credentialVisible: false,
  credentials: {
    cloud: { saved: false, tail: "" },
  },
  capturingShortcut: false,
  shortcut: {
    type: "hold",
    code: "ShiftLeft",
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    label: "长按左 Shift",
    idleLabel: "按住左 Shift",
  },
  audioStream: null,
  audioContext: null,
  meterTimer: null,
};

const els = {
  phaseTabs: document.querySelectorAll(".phase-tab"),
  phaseTriggers: document.querySelectorAll("[data-phase]"),
  phaseViews: document.querySelectorAll("[data-view]"),
  floatingIme: document.querySelector("#floatingIme"),
  recordButton: document.querySelector("#recordButton"),
  recordLabel: document.querySelector("#recordLabel"),
  transcript: document.querySelector("#transcript"),
  targetInput: document.querySelector("#targetInput"),
  insertButton: document.querySelector("#insertButton"),
  copyButton: document.querySelector("#copyButton"),
  cancelButton: document.querySelector("#cancelButton"),
  inputMode: document.querySelector("#inputMode"),
  punctuationSwitch: document.querySelector("#punctuationSwitch"),
  retrySwitch: document.querySelector("#retrySwitch"),
  resetButton: document.querySelector("#resetButton"),
  statusDot: document.querySelector("#statusDot"),
  statusText: document.querySelector("#statusText"),
  signalBars: document.querySelector("#signalBars"),
  providerSummary: document.querySelector("#providerSummary"),
  providerNotes: document.querySelector("#providerNotes"),
  providerCredentialLabel: document.querySelector("#providerCredentialLabel"),
  providerCredentialInput: document.querySelector("#providerCredentialInput"),
  credentialState: document.querySelector("#credentialState"),
  credentialReveal: document.querySelector("#credentialReveal"),
  credentialSave: document.querySelector("#credentialSave"),
  credentialTest: document.querySelector("#credentialTest"),
  credentialClear: document.querySelector("#credentialClear"),
  shortcutPreset: document.querySelector("#shortcutPreset"),
  shortcutCapture: document.querySelector("#shortcutCapture"),
  shortcutLabel: document.querySelector("#shortcutLabel"),
  shortcutSummary: document.querySelector("#shortcutSummary"),
  clipboardSwitch: document.querySelector("#clipboardSwitch"),
};

const shortcutPresets = {
  shiftLeft: {
    type: "hold",
    code: "ShiftLeft",
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    label: "长按左 Shift",
    idleLabel: "按住左 Shift",
  },
  shiftRight: {
    type: "hold",
    code: "ShiftRight",
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    label: "长按右 Shift",
    idleLabel: "按住右 Shift",
  },
  altLeft: {
    type: "hold",
    code: "AltLeft",
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    label: "长按左 Alt",
    idleLabel: "按住左 Alt",
  },
  ctrlSpace: {
    type: "toggle",
    code: "Space",
    ctrlKey: true,
    altKey: false,
    metaKey: false,
    label: "Ctrl + Space（开关）",
    idleLabel: "按 Ctrl + Space",
  },
};

const sampleTranscripts = [
  "这是一段通过墨声插入的文字。",
  "我们先把 Windows 语音输入体验做到足够轻。",
  "识别成功后，文字会进入当前光标所在的位置。",
];

const useLiveSpeech = false;

const providerProfile = {
  summary: "识别: 云端 AI 识别",
  notes: ["自动识别中英粤混说，不需要手动选择语言", "准确率和响应体验最好，需要网络和 API Key"],
  credentialLabel: "API Key",
  credentialPlaceholder: "粘贴后保存到系统安全区",
  saveLabel: "保存密钥",
  testLabel: "测试连接",
};

function currentCredential() {
  return state.credentials.cloud;
}

function setPhase(phase) {
  state.phase = phase;
  els.phaseTabs.forEach((button) => {
    button.classList.toggle("active", button.dataset.phase === phase);
  });
  els.phaseViews.forEach((view) => {
    view.classList.toggle("active", view.dataset.view === phase);
  });

  if (phase === "input") {
    window.setTimeout(() => els.targetInput.focus(), 80);
  }
}

function setStatus(text, recording = false) {
  els.statusText.textContent = text;
  els.statusDot.classList.toggle("recording", recording);
  if (els.signalBars) els.signalBars.classList.toggle("recording", recording);
  els.floatingIme.classList.toggle("recording", recording);
  els.recordLabel.textContent = recording ? "正在收音" : state.shortcut.idleLabel;
}

function setFloatingMode(mode) {
  els.floatingIme.classList.toggle("ready", mode === "ready");
}

function syncProviderSummary() {
  els.providerSummary.textContent = providerProfile.summary;
  if (els.providerNotes) {
    els.providerNotes.innerHTML = providerProfile.notes.map((note) => `<span>${note}</span>`).join("");
  }
  els.providerCredentialLabel.textContent = providerProfile.credentialLabel;
  els.credentialSave.textContent = providerProfile.saveLabel;
  if (els.credentialTest) els.credentialTest.textContent = providerProfile.testLabel;
  syncCredentialUI();
}

function syncCredentialUI() {
  const credential = currentCredential();

  if (els.credentialReveal) els.credentialReveal.hidden = credential.saved;
  els.providerCredentialInput.disabled = false;
  els.providerCredentialInput.type = state.credentialVisible ? "text" : "password";
  els.providerCredentialInput.placeholder = providerProfile.credentialPlaceholder;

  if (credential.saved) {
    els.credentialState.textContent = `已保存 · 末尾 ${credential.tail}`;
    els.providerCredentialInput.value = "";
    els.providerCredentialInput.placeholder = `已保存 · 末尾 ${credential.tail}`;
    if (els.credentialReveal) els.credentialReveal.textContent = "显示";
    return;
  }

  els.credentialState.textContent = "未保存";
  if (!els.providerCredentialInput.value) {
    els.providerCredentialInput.placeholder = providerProfile.credentialPlaceholder;
  }
}

function syncShortcutUI() {
  if (els.shortcutLabel) els.shortcutLabel.textContent = state.shortcut.label;
  els.shortcutSummary.textContent = state.shortcut.label;
  if (!state.listening) els.recordLabel.textContent = state.shortcut.idleLabel;
}

function beginShortcutCapture() {
  state.capturingShortcut = true;
  if (els.shortcutPreset) els.shortcutPreset.value = "custom";
  if (els.shortcutCapture) els.shortcutCapture.classList.add("capturing");
  if (els.shortcutLabel) els.shortcutLabel.textContent = "按下想用的键";
}

function formatKeyLabel(event) {
  const keyName = {
    Space: "Space",
    ShiftLeft: "左 Shift",
    ShiftRight: "右 Shift",
    AltLeft: "左 Alt",
    AltRight: "右 Alt",
    ControlLeft: "左 Ctrl",
    ControlRight: "右 Ctrl",
    MetaLeft: "Win",
    MetaRight: "Win",
  }[event.code] || event.key.toUpperCase();

  const parts = [];
  if (event.ctrlKey && !event.code.startsWith("Control")) parts.push("Ctrl");
  if (event.altKey && !event.code.startsWith("Alt")) parts.push("Alt");
  if (event.shiftKey && !event.code.startsWith("Shift")) parts.push("Shift");
  if (event.metaKey && !event.code.startsWith("Meta")) parts.push("Win");
  parts.push(keyName);
  return parts.join(" + ");
}

function captureShortcut(event) {
  if (!state.capturingShortcut) return false;
  event.preventDefault();

  if (event.code === "Escape") {
    state.capturingShortcut = false;
    if (els.shortcutCapture) els.shortcutCapture.classList.remove("capturing");
    syncShortcutUI();
    return true;
  }

  const label = formatKeyLabel(event);
  state.shortcut = {
    type: "hold",
    code: event.code,
    ctrlKey: event.ctrlKey && !event.code.startsWith("Control"),
    altKey: event.altKey && !event.code.startsWith("Alt"),
    metaKey: event.metaKey && !event.code.startsWith("Meta"),
    label: `长按${label}`,
    idleLabel: `按住${label}`,
  };
  state.capturingShortcut = false;
  if (els.shortcutPreset) els.shortcutPreset.value = "custom";
  if (els.shortcutCapture) els.shortcutCapture.classList.remove("capturing");
  syncShortcutUI();
  return true;
}

function matchesShortcut(event) {
  const shortcut = state.shortcut;
  return (
    event.code === shortcut.code &&
    Boolean(event.ctrlKey) === Boolean(shortcut.ctrlKey) &&
    Boolean(event.altKey) === Boolean(shortcut.altKey) &&
    Boolean(event.metaKey) === Boolean(shortcut.metaKey)
  );
}

function getSpeechRecognition() {
  return window.SpeechRecognition || window.webkitSpeechRecognition || null;
}

function normalizeTranscript(text) {
  const trimmed = text.trim();
  if (!trimmed) return "";
  const hasPunctuation = /[。！？.!?]$/.test(trimmed);
  return state.autoPunctuation && !hasPunctuation ? `${trimmed}。` : trimmed;
}

async function startAudioMeter() {
  state.meterTimer = window.setInterval(() => {
    const level = 0.8 + Math.random() * 1.8;
    document.documentElement.style.setProperty("--meter", level.toFixed(2));
  }, 80);
}

function stopAudioMeter() {
  if (state.meterTimer) window.clearInterval(state.meterTimer);
  state.meterTimer = null;

  if (state.audioStream) {
    state.audioStream.getTracks().forEach((track) => track.stop());
  }

  if (state.audioContext) {
    state.audioContext.close();
  }

  state.audioStream = null;
  state.audioContext = null;
}

async function startListening() {
  if (state.listening) return;
  if (state.phase !== "input") setPhase("input");

  state.listening = true;
  els.transcript.value = "";
  state.transcript = "";
  setFloatingMode("recording");
  setStatus("正在输入", true);
  await startAudioMeter();

  const SpeechRecognition = getSpeechRecognition();
  if (useLiveSpeech && SpeechRecognition) {
    const recognition = new SpeechRecognition();
    recognition.lang = "zh-CN";
    recognition.interimResults = true;
    recognition.continuous = true;

    recognition.onresult = (event) => {
      let finalText = "";
      let interimText = "";
      for (let index = event.resultIndex; index < event.results.length; index += 1) {
        const text = event.results[index][0].transcript;
        if (event.results[index].isFinal) finalText += text;
        else interimText += text;
      }
      const nextText = normalizeTranscript(finalText || interimText);
      if (nextText) {
        state.transcript = nextText;
        els.transcript.value = nextText;
      }
    };

    recognition.onerror = () => {
      useMockTranscript();
    };

    recognition.onend = () => {
      if (state.listening) recognition.start();
    };

    state.recognition = recognition;
    recognition.start();
    return;
  }

  window.setTimeout(useMockTranscript, 700);
}

function stopListening() {
  if (!state.listening) return;
  state.listening = false;
  stopAudioMeter();

  if (state.recognition) {
    state.recognition.onend = null;
    state.recognition.stop();
    state.recognition = null;
  }

  if (!els.transcript.value.trim()) {
    useMockTranscript();
  }

  if (state.inputMode === "auto") {
    insertTranscript();
    return;
  }

  if (state.inputMode === "copy") {
    copyTranscript();
    setStatus("已复制到剪贴板", false);
    setFloatingMode("idle");
    return;
  }

  setFloatingMode("ready");
  setStatus("识别完成 · 等待确认", false);
}

function useMockTranscript() {
  const text = sampleTranscripts[Math.floor(Math.random() * sampleTranscripts.length)];
  state.transcript = text;
  els.transcript.value = text;
}

async function copyTranscript() {
  const text = els.transcript.value.trim();
  if (!text) {
    setStatus("没有可复制文本", false);
    return;
  }

  try {
    await navigator.clipboard.writeText(text);
    setStatus("已复制", false);
    setFloatingMode("idle");
  } catch {
    els.transcript.select();
    document.execCommand("copy");
    setStatus("已复制", false);
    setFloatingMode("idle");
  }
}

async function insertTranscript() {
  const text = els.transcript.value.trim();
  if (!text) {
    setStatus("没有可输入文本", false);
    return;
  }

  const target = els.targetInput;
  const start = target.selectionStart;
  const end = target.selectionEnd;
  const before = target.value.slice(0, start);
  const after = target.value.slice(end);
  const spacer = before && !/\s$/.test(before) ? "" : "";
  target.value = `${before}${spacer}${text}${after}`;
  const cursor = before.length + spacer.length + text.length;
  target.focus();
  target.setSelectionRange(cursor, cursor);
  setStatus("已输入到当前光标", false);
  setFloatingMode("idle");
}

function resetDemo() {
  stopListening();
  els.transcript.value = "";
  state.transcript = "";
  setStatus("待机", false);
  setFloatingMode("idle");
}

function cancelTranscript() {
  els.transcript.value = "";
  state.transcript = "";
  setStatus("已取消", false);
  setFloatingMode("idle");
  els.targetInput.focus();
}

els.phaseTriggers.forEach((button) => {
  button.addEventListener("click", () => setPhase(button.dataset.phase));
});

els.recordButton.addEventListener("pointerdown", startListening);
els.recordButton.addEventListener("pointerup", stopListening);
els.recordButton.addEventListener("pointerleave", stopListening);
els.recordButton.addEventListener("keydown", (event) => {
  if (event.code === "Space" || event.code === "Enter") {
    event.preventDefault();
    startListening();
  }
});
els.recordButton.addEventListener("keyup", (event) => {
  if (event.code === "Space" || event.code === "Enter") {
    event.preventDefault();
    stopListening();
  }
});

els.insertButton.addEventListener("click", insertTranscript);
els.copyButton.addEventListener("click", copyTranscript);
els.cancelButton.addEventListener("click", cancelTranscript);
els.resetButton.addEventListener("click", resetDemo);

els.transcript.addEventListener("input", () => {
  state.transcript = els.transcript.value;
});

if (els.inputMode) {
  els.inputMode.addEventListener("change", () => {
    state.inputMode = els.inputMode.value;
  });
}

if (els.punctuationSwitch) {
  els.punctuationSwitch.addEventListener("click", () => {
    state.autoPunctuation = !state.autoPunctuation;
    els.punctuationSwitch.classList.toggle("active", state.autoPunctuation);
  });
}

if (els.retrySwitch) {
  els.retrySwitch.addEventListener("click", () => {
    state.keepPreviewOnFailure = !state.keepPreviewOnFailure;
    els.retrySwitch.classList.toggle("active", state.keepPreviewOnFailure);
  });
}

if (els.credentialReveal) {
  els.credentialReveal.addEventListener("click", () => {
    state.credentialVisible = !state.credentialVisible;
    els.credentialReveal.textContent = state.credentialVisible ? "隐藏" : "显示";
    syncCredentialUI();
  });
}

els.credentialSave.addEventListener("click", () => {
  const credential = currentCredential();
  const value = els.providerCredentialInput.value.trim();

  if (!value) {
    els.credentialState.textContent = "请输入密钥";
    return;
  }

  credential.saved = true;
  credential.tail = value.slice(-4);
  state.credentialVisible = false;

  els.providerCredentialInput.value = "";
  syncCredentialUI();
  setPhase("input");
});

if (els.credentialTest) {
  els.credentialTest.addEventListener("click", () => {
    const credential = currentCredential();
    const hasDraft = Boolean(els.providerCredentialInput.value.trim());

    if (!credential.saved && !hasDraft) {
      els.credentialState.textContent = "先保存密钥";
      return;
    }

    els.credentialState.textContent = "连接正常";
  });
}

if (els.credentialClear) {
  els.credentialClear.addEventListener("click", () => {
    const credential = currentCredential();
    credential.saved = false;
    credential.tail = "";
    state.credentialVisible = false;
    els.providerCredentialInput.value = "";
    syncCredentialUI();
  });
}

if (els.shortcutPreset) {
  els.shortcutPreset.addEventListener("change", () => {
    const preset = shortcutPresets[els.shortcutPreset.value];
    if (preset) {
      state.shortcut = { ...preset };
      syncShortcutUI();
      return;
    }

    beginShortcutCapture();
  });
}

document.addEventListener("click", (event) => {
  const eventTarget = event.target?.nodeType === Node.TEXT_NODE ? event.target.parentElement : event.target;
  const target = typeof eventTarget?.closest === "function" ? eventTarget.closest("#shortcutCapture") : null;
  if (!target) return;
  event.preventDefault();
  beginShortcutCapture();
});

if (els.clipboardSwitch) {
  els.clipboardSwitch.addEventListener("click", () => {
    state.clipboardRestore = !state.clipboardRestore;
    els.clipboardSwitch.classList.toggle("active", state.clipboardRestore);
  });
}

window.addEventListener("keydown", (event) => {
  if (captureShortcut(event)) return;

  if (matchesShortcut(event) && !event.repeat) {
    event.preventDefault();
    if (state.shortcut.type === "toggle" && state.listening) stopListening();
    else startListening();
  }
});

window.addEventListener("keyup", (event) => {
  if (state.shortcut.type === "hold" && event.code === state.shortcut.code) {
    event.preventDefault();
    stopListening();
  }
});

syncProviderSummary();
syncShortcutUI();
setPhase("setup");
setStatus("待机", false);
