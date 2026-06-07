// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
// Apply the saved theme before first paint to avoid a flash of defaults. Loaded
// synchronously in <head> (extracted from an inline script so the CSP stays
// script-src 'self').
(function () {
  try {
    var th = localStorage.getItem("applytrack-theme");
    if (th) document.documentElement.setAttribute("data-theme", th);
  } catch (e) {}
})();
