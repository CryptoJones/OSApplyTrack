// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
(function () {
  "use strict";
  var defaults = { colorMode: "system", contrast: "system", motion: "system", textSize: "100", density: "comfortable" };
  var legacyLight = ["paper", "mint"];
  try {
    var stored = localStorage.getItem("applytrack-accessibility-v1");
    var value = stored ? JSON.parse(stored) : defaults;
    if (!stored) {
      var legacy = localStorage.getItem("applytrack-theme");
      if (legacy) value = Object.assign({}, defaults, { colorMode: legacyLight.includes(legacy) ? "light" : "dark" });
    }
    var root = document.documentElement;
    root.dataset.colorMode = ["system", "light", "dark"].includes(value.colorMode) ? value.colorMode : defaults.colorMode;
    root.dataset.contrast = ["system", "default", "high"].includes(value.contrast) ? value.contrast : defaults.contrast;
    root.dataset.motion = ["system", "reduce"].includes(value.motion) ? value.motion : defaults.motion;
    root.dataset.textSize = ["100", "125", "150"].includes(String(value.textSize)) ? String(value.textSize) : defaults.textSize;
    root.dataset.density = ["comfortable", "compact"].includes(value.density) ? value.density : defaults.density;
  } catch (_) {}
})();
