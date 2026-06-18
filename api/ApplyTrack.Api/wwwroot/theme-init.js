// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
// Apply the saved theme before first paint to avoid a flash of defaults. Loaded
// synchronously in <head> (extracted from an inline script so the CSP stays
// script-src 'self').
(function () {
  try {
    // Phones are locked to the cyberdeck theme (the mobile app look); desktop
    // honors the saved choice. Decided before first paint so there's no flash.
    var mobile = window.matchMedia("(max-width: 767px)").matches;
    var th = mobile ? "cyberdeck" : localStorage.getItem("applytrack-theme");
    if (th) document.documentElement.setAttribute("data-theme", th);
  } catch (e) {}
})();
