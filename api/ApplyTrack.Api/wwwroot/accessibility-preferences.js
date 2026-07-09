// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

const STORAGE_KEY = "applytrack-accessibility-v1";
export const PREFERENCE_DEFAULTS = {
  colorMode: "system",
  contrast: "system",
  motion: "system",
  textSize: "100",
  density: "comfortable",
};

export function readPreferences() {
  try {
    return { ...PREFERENCE_DEFAULTS, ...JSON.parse(localStorage.getItem(STORAGE_KEY) || "{}") };
  } catch (_) {
    return { ...PREFERENCE_DEFAULTS };
  }
}

export function applyPreferences(value, persist = true) {
  const prefs = { ...PREFERENCE_DEFAULTS, ...value };
  const root = document.documentElement;
  root.dataset.colorMode = ["system", "light", "dark"].includes(prefs.colorMode) ? prefs.colorMode : "system";
  root.dataset.contrast = ["system", "default", "high"].includes(prefs.contrast) ? prefs.contrast : "system";
  root.dataset.motion = ["system", "reduce"].includes(prefs.motion) ? prefs.motion : "system";
  root.dataset.textSize = ["100", "125", "150"].includes(String(prefs.textSize)) ? String(prefs.textSize) : "100";
  root.dataset.density = ["comfortable", "compact"].includes(prefs.density) ? prefs.density : "comfortable";
  if (persist) {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs)); } catch (_) {}
  }
}

function preferenceSelect(id, label, value, options, help) {
  return `<label class="field" for="${id}"><span class="field-label">${label}</span>
    <select id="${id}">${options.map(([key, text]) =>
      `<option value="${key}"${String(value) === key ? " selected" : ""}>${text}</option>`).join("")}</select>
    <span class="field-help">${help}</span></label>`;
}

export function renderAccessibilityPreferences(body, announce) {
  const preferences = readPreferences();
  body.innerHTML = `<section class="sheet" aria-labelledby="accessibility-heading">
    <h2 id="accessibility-heading">Display and sensory preferences</h2>
    <p class="settings-help">These settings are stored only in this browser and apply before sign-in.</p>
    <div class="preference-grid">
      ${preferenceSelect("pref-color", "Color mode", preferences.colorMode,
        [["system", "Use device setting"], ["light", "Light"], ["dark", "Dark"]],
        "Use device setting follows your operating system light or dark preference.")}
      ${preferenceSelect("pref-contrast", "Contrast", preferences.contrast,
        [["system", "Use device setting"], ["default", "Default"], ["high", "High contrast"]],
        "High contrast uses stronger boundaries and system colors where supported.")}
      ${preferenceSelect("pref-motion", "Motion", preferences.motion,
        [["system", "Use device setting"], ["reduce", "Reduce motion"]],
        "Reduced motion removes nonessential transitions.")}
      ${preferenceSelect("pref-text", "Text size", preferences.textSize,
        [["100", "100%"], ["125", "125%"], ["150", "150%"]],
        "Browser zoom remains available in addition to this setting.")}
      ${preferenceSelect("pref-density", "Spacing", preferences.density,
        [["comfortable", "Comfortable"], ["compact", "Compact"]],
        "Comfortable spacing provides more separation between controls.")}
    </div>
    <div class="action-row section-divider">
      <button type="button" class="btn btn-ghost" data-act="pref-reset">Reset to device settings</button>
    </div>
  </section>`;
  const byId = (id) => body.querySelector(`#${id}`);
  const update = () => {
    applyPreferences({
      colorMode: byId("pref-color").value,
      contrast: byId("pref-contrast").value,
      motion: byId("pref-motion").value,
      textSize: byId("pref-text").value,
      density: byId("pref-density").value,
    });
    announce("Display preferences updated.");
  };
  body.querySelectorAll("select").forEach((select) => select.addEventListener("change", update));
  body.querySelector('[data-act="pref-reset"]').addEventListener("click", () => {
    applyPreferences(PREFERENCE_DEFAULTS);
    renderAccessibilityPreferences(body, announce);
    announce("Display preferences reset to device settings.");
    byId("pref-color").focus();
  });
}
