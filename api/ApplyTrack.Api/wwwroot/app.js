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

const state = {
  apps: [],
  appsEtag: "",
  stats: { status: {}, lane: {} },
  query: "",
  filterLane: "",
  filterStatus: "",
  current: null,
  currentVersion: "",
  mode: "empty", // empty | view | edit | raw | new | settings
  settingsTab: "accessibility",
  coverLettersEnabled: true,
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
  const badLink = new URLSearchParams(location.search).get("error") === "invalid_link";
  const overlay = document.createElement("div");
  overlay.id = "login-overlay";
  overlay.className = "login-overlay";
  $("#app-shell").inert = true;
  $(".mobile-actions").inert = true;
  overlay.innerHTML = `
    <main class="login-card" aria-labelledby="login-title">
    <form id="login-form">
      <h1 id="login-title" class="login-mark">ApplyTrack</h1>
      <p class="login-sub">Sign in with a one-time magic link.</p>
      ${badLink ? `<p class="login-error">That link was invalid or expired — request a fresh one.</p>` : ""}
      <p class="login-signup-head">NEW HERE?</p>
      <p class="login-signup">Just enter your email — your account is created automatically.</p>
      <label class="field" for="login-email"><span class="field-label">Email address</span>
      <input id="login-email" class="login-input" type="email" required autocomplete="email"
        inputmode="email" placeholder="you@example.com" /></label>
      <button type="submit" class="btn btn-primary login-btn">Send magic link</button>
      <p class="login-note">We email you a link to sign in — check your spam folder if it doesn't arrive. Self-hosting? It's printed to the server logs.</p>
      <p class="login-note"><a href="https://github.com/CryptoJones/OSApplyTrack" target="_blank" rel="noopener" style="color:var(--accent);text-decoration:none">Open source on GitHub ↗</a></p>
    </form></main>`;
  document.body.appendChild(overlay);

  const form = overlay.querySelector("#login-form");
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const email = overlay.querySelector("#login-email").value.trim();
    if (!email) return;
    const btn = form.querySelector("button");
    btn.disabled = true;
    btn.textContent = "Sending…";
    // Always reports success: the API returns 200 either way (no account enumeration).
    try { await api("POST", "/api/auth/request", { email }); } catch (_) {}
    form.innerHTML = `
      <h1 class="login-mark">ApplyTrack</h1>
      <h2 tabindex="-1" class="login-sub">Check your inbox</h2>
      <p class="login-note">If <strong>${escapeHtml(email)}</strong> can sign in, a link is on its way —
        check your spam folder too. Self-hosting? Look for it in the server logs.</p>
      <p class="login-note"><a href="https://github.com/CryptoJones/OSApplyTrack" target="_blank" rel="noopener" style="color:var(--accent);text-decoration:none">Open source on GitHub ↗</a></p>`;
    form.querySelector("h2").focus();
  });
  overlay.querySelector("#login-email").focus();
}

// ---- Helpers --------------------------------------------------------------

const escapeHtml = (s) =>
  String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

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
  const counts = state.stats.status || {};
  const parts = STATUSES.filter((s) => counts[s]).map((s) => {
    const active = state.filterStatus === s;
    const label = STATUS_LABEL[s] || s;
    return `<button type="button" class="pipe-stat" data-status="${s}" aria-pressed="${active}"
      aria-label="${escapeHtml(label)}, ${counts[s]} applications">
      <span class="n" aria-hidden="true">${counts[s]}</span><span>${escapeHtml(label)}</span></button>`;
  });
  const total = Object.values(counts).reduce((sum, n) => sum + n, 0);
  if (total) parts.push(`<span class="pipe-total"><span class="n">${total}</span>total</span>`);
  pipelineEl.innerHTML = parts.join("") || `<span>No applications yet</span>`;
  pipelineEl.querySelectorAll(".pipe-stat[data-status]").forEach((el) => {
    el.addEventListener("click", () => {
      state.filterStatus = state.filterStatus === el.dataset.status ? "" : el.dataset.status;
      statusSel.value = state.filterStatus;
      renderPipeline();
      renderSidebar();
    });
  });
}

// ---- Sidebar --------------------------------------------------------------

function filteredApps() {
  const q = state.query.trim().toLowerCase();
  const apps = state.apps.filter((a) => {
    if (state.filterLane && a.lane !== state.filterLane) return false;
    if (state.filterStatus && a.status !== state.filterStatus) return false;
    if (!q) return true;
    return (
      a.company.toLowerCase().includes(q) ||
      a.role.toLowerCase().includes(q) ||
      (a.snippet || "").toLowerCase().includes(q)
    );
  });
  return apps.sort((a, b) => statusGroup(a.status) - statusGroup(b.status));
}

// First still-open role (not applied, not passed) — where Pass jumps next.
function nextActionable(excludeName) {
  const app = filteredApps().find(
    (a) => a.filename !== excludeName && statusGroup(a.status) === 0);
  return app ? app.filename : null;
}

function renderSidebar() {
  const apps = filteredApps();
  listEl.innerHTML = "";
  if (apps.length === 0) {
    listEl.innerHTML = `<li class="empty-result">No applications match the current filters.</li>`;
  }
  apps.forEach((a, i) => {
    const li = document.createElement("li");
    li.className = "application-list-item";
    li.dataset.name = a.filename;
    const score = a.score ? `<span class="score-chip">fit ${escapeHtml(a.score)}</span>` : "";
    const contactLine =
      a.contact || a.contact_email
        ? `<span class="ic-meta">Contact: ${escapeHtml(a.contact || a.contact_email)}${
            a.contact && a.contact_email ? " · " + escapeHtml(a.contact_email) : ""
          }</span>`
        : "";
    const selected = a.filename === state.current;
    li.innerHTML = `<button type="button" class="application-card" aria-current="${selected}" data-name="${escapeHtml(a.filename)}">
      <span class="application-card-header">
        <div class="ic-title">${escapeHtml(a.company)}</div>
        ${statusBadge(a.status)}
      </span>
      <span class="ic-meta">${lanePill(a.lane)} ${score}
        ${a.applied ? "· applied " + escapeHtml(a.applied) : ""}
        ${a.followup ? "· follow-up " + escapeHtml(a.followup) : ""}</span>
      ${a.role ? `<span class="ic-snippet">${escapeHtml(a.role)}</span>` : ""}
      ${contactLine}</button>`;
    li.querySelector("button").addEventListener("click", () => openApp(a.filename));
    listEl.appendChild(li);
  });
  const total = state.apps.length;
  const shown = apps.length;
  countEl.textContent = shown === total
    ? `${total} application${total === 1 ? "" : "s"}`
    : `${shown} of ${total} applications`;
}

// ---- Main pane ------------------------------------------------------------

function renderEmpty() {
  state.mode = "empty";
  state.current = null;
  showListPane({ restoreFocus: false });
  document.title = "Applications | ApplyTrack";
  renderSidebar();
  contentEl.innerHTML = `
    <div class="empty">
      <h2 class="empty-title">No application selected</h2>
      <p>Choose an application from the list or create a new one.</p>
    </div>`;
}

async function openApp(name) {
  try {
    const data = await api("GET", `/api/apps/${encodeURIComponent(name)}`);
    state.current = name;
    state.currentVersion = data.version || "";
    state.mode = "view";
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
  parts.push(`<span>${lanePill(f.lane)}</span>`);
  if (f.location) parts.push(`<span><span class="label">where</span> ${escapeHtml(f.location)}</span>`);
  if (f.salary) parts.push(`<span><span class="label">comp</span> ${escapeHtml(f.salary)}</span>`);
  if (f.applied) parts.push(`<span><span class="label">applied</span> ${escapeHtml(f.applied)}</span>`);
  if (f.followup) parts.push(`<span><span class="label">follow-up</span> ${escapeHtml(f.followup)}</span>`);
  if (f.contact) parts.push(`<span><span class="label">contact</span> ${escapeHtml(f.contact)}</span>`);
  if (f.contact_email) parts.push(`<span><span class="label">email</span> <a href="mailto:${escapeHtml(f.contact_email)}">${escapeHtml(f.contact_email)}</a></span>`);
  if (f.source) parts.push(`<span><span class="label">source</span> ${escapeHtml(f.source)}</span>`);
  if (f.score) parts.push(`<span class="score-chip">fit ${escapeHtml(f.score)}</span>`);
  return `<div class="meta-row">${parts.join("")}</div>`;
}

function renderView(data) {
  const f = data.fields;
  const url = safeUrl(f.link);
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
        <div>
          <div class="sheet-eyebrow">${statusBadge(f.status)} · ${escapeHtml(data.filename)}</div>
          <h2 class="sheet-title">${escapeHtml(f.company || stem(data.filename))}</h2>
          ${f.role ? `<div class="font-body text-lg text-ink-faint">${escapeHtml(f.role)}</div>` : ""}
        </div>
        <div class="seg" role="tablist" aria-label="Application editor mode">
          <button type="button" role="tab" aria-selected="true" data-act="edit">Form</button>
          <button type="button" role="tab" aria-selected="false" data-act="raw">Raw source</button>
        </div>
      </header>
      ${metaRow(f)}
      <div class="action-row">${applyBtn}${checkBtn}</div>
      <div id="link-status" class="mt-2 text-sm" role="status" aria-live="polite"></div>
      <div class="prose-omi mt-5">${DOMPurify.sanitize(marked.parse(f.notes || "_No notes yet._", { gfm: true, breaks: false }))}</div>
      ${materialSection(data)}
      <div class="action-row section-divider">
        <button class="btn btn-primary" data-act="applied">Mark applied</button>
        <button class="btn btn-ghost" data-act="pass">Pass</button>
        <button class="btn btn-ghost" data-act="blacklist">Blacklist company</button>
        <span class="flex-1"></span>
        <button class="btn btn-danger" data-act="delete">Delete</button>
      </div>
    </article>`;
  contentEl.querySelector('[data-act="edit"]').onclick = () => openEdit(data);
  contentEl.querySelector('[data-act="raw"]').onclick = () => openRaw(data);
  contentEl.querySelector('[data-act="applied"]').onclick = () => markStatus(data, "applied");
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
      <div class="material-block mt-6 border-t border-rule pt-4">
        <div class="flex items-center justify-between gap-3">
          <div>
            <div class="field-label">Cover letter</div>
            <p class="field-help">Draft a tailored letter from your résumé via the configured AI.</p>
          </div>
          <button class="btn btn-primary shrink-0" data-act="draft">Generate cover letter</button>
        </div>
      </div>`;
  }
  const html = DOMPurify.sanitize(marked.parse(data.material, { gfm: true, breaks: true }));
  // Regenerate is a drafting affordance — hide it when the engine is off (the server
  // refuses the draft anyway). Copy / Download / Discard act on the stored letter, so
  // they stay available.
  const regenerate = state.coverLettersEnabled
    ? `<button class="btn btn-ghost btn-xs" data-act="draft">Regenerate</button>`
    : "";
  return `
    <div class="material-block mt-6 border-t border-rule pt-4">
      <div class="flex items-center justify-between gap-3">
        <div class="field-label">Cover letter</div>
        <div class="flex flex-wrap gap-2">
          <button class="btn btn-ghost btn-xs" data-act="mat-copy">Copy</button>
          <button class="btn btn-ghost btn-xs" data-act="mat-download">Download .md</button>
          <button class="btn btn-ghost btn-xs" data-act="mat-pdf">Download PDF</button>
          ${regenerate}
          <button class="btn btn-ghost btn-xs" data-act="mat-discard">Discard</button>
        </div>
      </div>
      <div class="prose-omi material-body mt-3">${html}</div>
    </div>`;
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

      <div class="mt-4 grid grid-cols-3 gap-4">
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
  contentEl.innerHTML = formMarkup(data.fields, { isNew: false });
  wireForm({ isNew: false });
  document.title = `Edit ${data.fields.company || stem(data.filename)} | ApplyTrack`;
  focusView("h2");
}

function openNew() {
  state.mode = "new";
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
const SOURCES = ["remotive", "remoteok", "arbeitnow", "jobicy", "weworkremotely", "hn_whoishiring"];
const SOURCE_LABEL = {
  remotive: "Remotive",
  remoteok: "RemoteOK",
  arbeitnow: "Arbeitnow",
  jobicy: "Jobicy",
  weworkremotely: "We Work Remotely",
  hn_whoishiring: "HN “Who is hiring”",
};
const ATS_PROVIDERS = ["greenhouse", "lever"];

// Holds the ATS boards being edited (add/remove re-render only that list).
let criteriaBoards = [];

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
        <div class="field-label">ATS boards — follow a company's Greenhouse or Lever board</div>
        <div id="criteria-boards" class="board-list"></div>
        <div class="board-add mt-2">
          <select id="c-board-provider" class="field-input mono" aria-label="ATS provider">
            ${ATS_PROVIDERS.map((p) => `<option value="${p}">${p}</option>`).join("")}
          </select>
          <input id="c-board-slug" class="field-input mono" aria-label="Company board slug" placeholder="company slug (e.g. stripe)" />
          <button class="btn btn-ghost" data-act="add-board" type="button">+ Add board</button>
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
  };
}

function wireCriteria() {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="add-board"]').onclick = () => {
    const provider = $("#c-board-provider").value;
    const slug = $("#c-board-slug").value.trim();
    if (!slug) return toast("Enter a company slug to add a board.");
    const dup = criteriaBoards.some(
      (b) => b.provider === provider && b.slug.toLowerCase() === slug.toLowerCase());
    if (dup) return toast("That board is already in the list.");
    criteriaBoards.push({ provider, slug });
    $("#c-board-slug").value = "";
    renderBoards();
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
  body.innerHTML = criteriaMarkup(c);
  renderBoards();
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

// ---- Boot + refresh -------------------------------------------------------

async function refresh() {
  const list = await api.getConditional("/api/apps", state.appsEtag);
  if (!list.modified) return false;

  // Stats are derived from the same applications table revision, so an unchanged
  // list means they are unchanged too. Fetch them only after an ETag miss.
  const stats = await api("GET", "/api/stats");
  state.apps = list.data;
  state.stats = stats;
  state.appsEtag = list.etag;
  renderPipeline();
  renderSidebar();
  return true;
}

searchEl.addEventListener("input", () => { state.query = searchEl.value; renderSidebar(); });
laneSel.addEventListener("change", () => { state.filterLane = laneSel.value; renderSidebar(); });
statusSel.addEventListener("change", () => {
  state.filterStatus = statusSel.value;
  renderPipeline();
  renderSidebar();
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
  state.current = null;
  showDetailPane();
  renderSidebar();
  contentEl.innerHTML = `
    <h1>Settings</h1>
    <nav class="settings-tabs" role="tablist" aria-label="Settings sections">
      ${SETTINGS_TABS.map(([k, label]) =>
        `<button class="btn ${k === state.settingsTab ? "btn-primary" : "btn-ghost"}"
           role="tab" aria-selected="${k === state.settingsTab}" data-tab="${k}" type="button">${label}</button>`).join("")}
    </nav>
    <div id="settings-body" role="tabpanel" class="mt-4" aria-live="polite"></div>`;
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
        <div class="field-label">Session</div>
        <div class="mt-3">
          <button class="btn btn-ghost" data-act="logout" type="button">Sign out</button>
        </div>
      </div>

      <div class="mt-5 border-t border-rule pt-4">
        <div class="field-label">Danger zone</div>
        <p class="field-help">
          Deletes your account and every application, setting, and session with it. Immediate and unrecoverable.
        </p>
        <div class="mt-3">
          <button class="btn btn-danger" data-act="delete-account" type="button">Delete my account</button>
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
  act("delete-account").onclick = async () => {
    if (!await confirmAction({
      title: "Delete your account?",
      message: "Every application, setting, cover letter, and session will be deleted immediately. This cannot be undone.",
      confirmLabel: "Delete my account",
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

(async function boot() {
  try {
    // Gate the app on a live session; a 401 here pops the login view (via api())
    // and we stop booting until the user signs in and reloads.
    await api("GET", "/api/auth/me");
  } catch (e) {
    if (e.status === 401) return;
  }
  // Whether this tenant wants cover letters decides if the app sheet renders any
  // drafting UI at all; default ON when the lookup fails so nothing is hidden by error.
  try {
    const s = await api("GET", "/api/llm-settings");
    state.coverLettersEnabled = s.cover_letters_enabled !== false;
  } catch (_) {}
  try {
    await refresh();
    renderEmpty();
  } catch (e) {
    contentEl.innerHTML = `<div class="empty"><div class="empty-title">Couldn't load applications.</div>
      <p class="font-mono text-xs">${escapeHtml(e.message)}</p></div>`;
  }
})();
