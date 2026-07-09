const tabs = document.querySelectorAll(".review-tab");
const views = document.querySelectorAll(".review-view");
const stateChips = document.querySelectorAll(".state-chip");
const floatingCapsule = document.querySelector("#floatingCapsule");
const shortcutButton = document.querySelector("#shortcutButton");
const shortcutSummary = document.querySelector("#shortcutSummary");
const shortcutValue = document.querySelector("#shortcutValue");
const saveKeyButton = document.querySelector("#saveKeyButton");
const apiKeyInput = document.querySelector("#apiKeyInput");
const apiField = document.querySelector("#apiField");

let shortcut = "长按左 Shift";
let capturingShortcut = false;
let hasSavedApiKey = true;

function setView(name) {
  tabs.forEach((tab) => tab.classList.toggle("active", tab.dataset.view === name));
  views.forEach((view) => view.classList.toggle("active", view.dataset.panel === name));
  if (["settings", "overlay", "flow"].includes(name)) {
    const url = new URL(window.location.href);
    url.searchParams.set("view", name);
    window.history.replaceState(null, "", url);
  }
}

function setOverlay(mode) {
  stateChips.forEach((chip) => chip.classList.toggle("active", chip.dataset.overlay === mode));
  floatingCapsule.className = `floating-capsule ${mode}`;
  if (["recording", "processing", "success", "error"].includes(mode)) {
    const url = new URL(window.location.href);
    url.searchParams.set("mode", mode);
    window.history.replaceState(null, "", url);
  }
}

tabs.forEach((tab) => {
  tab.addEventListener("click", () => setView(tab.dataset.view));
});

stateChips.forEach((chip) => {
  chip.addEventListener("click", () => setOverlay(chip.dataset.overlay));
});

shortcutButton.addEventListener("click", () => {
  capturingShortcut = true;
  shortcutButton.classList.add("capturing");
  shortcutButton.textContent = "按键";
  shortcutValue.textContent = "等待输入";
});

window.addEventListener("keydown", (event) => {
  if (!capturingShortcut) return;
  event.preventDefault();
  if (event.code === "Escape") {
    finishShortcutCapture(shortcut);
    return;
  }
  if (isModifierCode(event.code)) {
    shortcutValue.textContent = "继续按主键，或松开确认";
    return;
  }
  finishShortcutCapture(formatShortcut(event));
});

window.addEventListener("keyup", (event) => {
  if (!capturingShortcut || !isModifierCode(event.code)) return;
  event.preventDefault();
  finishShortcutCapture(`长按${keyName(event.code, event.key)}`);
});

saveKeyButton.addEventListener("click", () => {
  const value = apiKeyInput.value.trim();
  if (value) {
    hasSavedApiKey = true;
    apiKeyInput.value = "";
  }
  apiKeyInput.blur();
  renderApiKeyState();
});

apiKeyInput.addEventListener("input", renderApiKeyState);
apiKeyInput.addEventListener("focus", renderApiKeyState);
apiKeyInput.addEventListener("blur", renderApiKeyState);

const initialView = new URLSearchParams(window.location.search).get("view") || "settings";
setView(["settings", "overlay", "flow"].includes(initialView) ? initialView : "settings");
const initialMode = new URLSearchParams(window.location.search).get("mode") || "recording";
setOverlay(["recording", "processing", "success", "error"].includes(initialMode) ? initialMode : "recording");
renderApiKeyState();

function finishShortcutCapture(label) {
  capturingShortcut = false;
  shortcut = label;
  shortcutButton.classList.remove("capturing");
  shortcutButton.textContent = "更改";
  shortcutValue.textContent = shortcut;
  shortcutSummary.textContent = shortcut;
}

function renderApiKeyState() {
  const hasDraft = Boolean(apiKeyInput.value.trim());
  const editing = document.activeElement === apiKeyInput || hasDraft;
  apiField.classList.toggle("saved", hasSavedApiKey && !hasDraft);
  apiField.classList.toggle("editing", editing);
  saveKeyButton.textContent = hasDraft || !hasSavedApiKey ? "保存" : "已保存";
  saveKeyButton.classList.toggle("saved", hasSavedApiKey && !hasDraft);
}

function formatShortcut(event) {
  const parts = [];
  if (event.ctrlKey && !event.code.startsWith("Control")) parts.push("Ctrl");
  if (event.altKey && !event.code.startsWith("Alt")) parts.push("Alt");
  if (event.shiftKey && !event.code.startsWith("Shift")) parts.push("Shift");
  parts.push(keyName(event.code, event.key));
  return `长按${parts.join(" + ")}`;
}

function isModifierCode(code) {
  return code.startsWith("Shift") || code.startsWith("Control") || code.startsWith("Alt");
}

function keyName(code, key) {
  const labels = {
    ShiftLeft: "左 Shift",
    ShiftRight: "右 Shift",
    AltLeft: "左 Alt",
    AltRight: "右 Alt",
    ControlLeft: "左 Ctrl",
    ControlRight: "右 Ctrl",
    Space: "Space",
    Escape: "Esc",
  };
  if (labels[code]) return labels[code];
  if (/^Key[A-Z]$/.test(code)) return code.replace("Key", "");
  if (/^Digit[0-9]$/.test(code)) return code.replace("Digit", "");
  if (/^F[0-9]{1,2}$/.test(code)) return code;
  return key.length === 1 ? key.toUpperCase() : key;
}
