// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
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
  stats: { status: {}, lane: {} },
  query: "",
  filterLane: "",
  filterStatus: "",
  current: null,
  currentVersion: "",
  mode: "empty", // empty | view | edit | raw | new | settings
  settingsTab: "criteria",
  coverLettersEnabled: true,
};

const $ = (sel) => document.querySelector(sel);
const listEl = $("#app-list");
const pipelineEl = $("#pipeline");
const contentEl = $("#content");
const countEl = $("#app-count");
const searchEl = $("#search");
const toastEl = $("#toast");
const laneSel = $("#filter-lane");
const statusSel = $("#filter-status");

// ---- API ------------------------------------------------------------------

async function api(method, path, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  }
  const res = await fetch(path, opts);
  if (!res.ok) {
    // No session (or it was revoked): drop straight to the login view. The poll
    // loop swallows the throw, so an expired session bounces here on its own.
    if (res.status === 401) showLogin();
    let detail = res.statusText;
    try { detail = (await res.json()).detail || detail; } catch (_) {}
    const err = new Error(detail);
    err.status = res.status;
    throw err;
  }
  if (res.status === 204) return null;
  return res.json();
}

// PUT guarding against external edits (the hourly poller also writes files).
// Send the version we last read; a 409 means it changed underneath us.
async function saveWithConflict(path, body, label) {
  const versioned = state.currentVersion
    ? `${path}?expected_version=${encodeURIComponent(state.currentVersion)}`
    : path;
  try {
    return await api("PUT", versioned, body);
  } catch (e) {
    if (e.status === 409 && confirm(`"${label}" changed on disk since you opened it. Overwrite with your version?`)) {
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
  overlay.innerHTML = `
    <form id="login-form" class="login-card">
      <h1 class="login-mark font-display"><span class="text-stamp">apply</span><span>track</span></h1>
      <p class="login-sub">Sign in with a one-time magic link.</p>
      ${badLink ? `<p class="login-error">That link was invalid or expired — request a fresh one.</p>` : ""}
      <input id="login-email" class="login-input" type="email" required autocomplete="email"
        inputmode="email" placeholder="you@example.com" aria-label="Email address" />
      <button type="submit" class="btn btn-primary login-btn">Send magic link</button>
      <p class="login-note">We email you a link to sign in. Self-hosting? It's printed to the server logs.</p>
    </form>`;
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
      <h1 class="login-mark font-display"><span class="text-stamp">apply</span><span>track</span></h1>
      <p class="login-sub">Check your inbox.</p>
      <p class="login-note">If <strong>${escapeHtml(email)}</strong> can sign in, a link is on its way.
        Self-hosting? Look for it in the server logs.</p>`;
  });
  overlay.querySelector("#login-email").focus();
}

// ---- Helpers --------------------------------------------------------------

const escapeHtml = (s) =>
  String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

function toast(msg) {
  toastEl.textContent = msg;
  toastEl.classList.add("show");
  clearTimeout(toast._t);
  toast._t = setTimeout(() => toastEl.classList.remove("show"), 2600);
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
    const active = state.filterStatus === s ? " active" : "";
    return `<span class="pipe-stat${active}" data-status="${s}">
      <span class="n">${counts[s]}</span>${escapeHtml(STATUS_LABEL[s] || s)}</span>`;
  });
  const total = Object.values(counts).reduce((sum, n) => sum + n, 0);
  if (total) parts.push(`<span class="pipe-total"><span class="n">${total}</span>total</span>`);
  pipelineEl.innerHTML = parts.join("") || `<span class="pipe-stat">no applications yet</span>`;
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
    listEl.innerHTML = `<li class="px-2 py-6 text-center font-mono text-[10px] uppercase tracking-widest text-ink-faint">no matches</li>`;
  }
  apps.forEach((a, i) => {
    const li = document.createElement("li");
    li.className = "index-card" + (a.filename === state.current ? " active" : "");
    li.style.animationDelay = `${Math.min(i, 12) * 28}ms`;
    li.dataset.name = a.filename;
    const score = a.score ? `<span class="score-chip">fit ${escapeHtml(a.score)}</span>` : "";
    const contactLine =
      a.contact || a.contact_email
        ? `<div class="ic-meta flex items-center gap-2">✉ ${escapeHtml(a.contact || a.contact_email)}${
            a.contact && a.contact_email ? " · " + escapeHtml(a.contact_email) : ""
          }</div>`
        : "";
    li.innerHTML = `
      <div class="flex items-center justify-between gap-2">
        <div class="ic-title">${escapeHtml(a.company)}</div>
        ${statusBadge(a.status)}
      </div>
      <div class="ic-meta flex items-center gap-2">${lanePill(a.lane)}${score}
        ${a.applied ? "· applied " + escapeHtml(a.applied) : ""}
        ${a.followup ? "· ↻ " + escapeHtml(a.followup) : ""}</div>
      ${a.role ? `<div class="ic-snippet">${escapeHtml(a.role)}</div>` : ""}
      ${contactLine}`;
    li.addEventListener("click", () => openApp(a.filename));
    listEl.appendChild(li);
  });
  const total = state.apps.length;
  const shown = apps.length;
  countEl.textContent = shown === total ? `${total} apps` : `${shown}/${total}`;
}

// ---- Main pane ------------------------------------------------------------

function renderEmpty() {
  state.mode = "empty";
  state.current = null;
  renderSidebar();
  contentEl.innerHTML = `
    <div class="empty">
      <div class="card-glyph"></div>
      <div class="empty-title">No application selected</div>
      <p class="font-mono text-[11px] uppercase tracking-[0.18em]">
        Pick one · or press <span class="text-stamp">+ New</span>
      </p>
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
      <div class="flex items-start justify-between gap-4">
        <div>
          <div class="sheet-eyebrow">${statusBadge(f.status)} · ${escapeHtml(data.filename)}</div>
          <h2 class="sheet-title">${escapeHtml(f.company || stem(data.filename))}</h2>
          ${f.role ? `<div class="font-body text-lg text-ink-faint">${escapeHtml(f.role)}</div>` : ""}
        </div>
        <div class="seg shrink-0">
          <button data-act="edit" class="active">Edit</button>
          <button data-act="raw">Raw</button>
        </div>
      </div>
      ${metaRow(f)}
      <div class="mt-4 flex flex-wrap gap-2">${applyBtn}${checkBtn}</div>
      <div id="link-status" class="mt-2 text-sm"></div>
      <div class="prose-omi mt-5">${DOMPurify.sanitize(marked.parse(f.notes || "_No notes yet._", { gfm: true, breaks: false }))}</div>
      ${materialSection(data)}
      <div class="mt-8 flex items-center gap-2 border-t border-rule pt-4">
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
            <p class="font-mono text-[11px] text-ink-faint">Draft a tailored letter from your résumé via the configured AI.</p>
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

async function discardMaterial(name) {
  if (!confirm("Discard this cover letter? You can regenerate it later.")) return;
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
  if (!confirm(`Blacklist "${company}"? Future polls will skip it and its open leads will be passed.`))
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
      <div class="sheet-eyebrow">${eyebrow}</div>
      <input id="f-company" class="title-input mt-1" placeholder="Company…" value="${escapeHtml(f.company || "")}" />

      <div class="mt-4">
        <label class="field-label">Role</label>
        <input id="f-role" class="field-input" value="${escapeHtml(f.role || "")}" placeholder="Senior .NET Engineer" />
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label">Lane</label>
          <select id="f-lane" class="field-input mono">${laneOptions(f.lane || "ai")}</select>
        </div>
        <div>
          <label class="field-label">Status</label>
          <select id="f-status" class="field-input mono">${statusOptions(f.status || "lead")}</select>
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label">Posting link</label>
        <div class="flex gap-2">
          <input id="f-link" class="field-input mono flex-1" value="${escapeHtml(f.link || "")}" placeholder="https://…" />
          <button id="f-autofill" class="btn btn-ghost shrink-0" type="button"
            title="Fetch the posting and fill in the empty fields">⤓ Autofill</button>
        </div>
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label">Location</label>
          <input id="f-location" class="field-input" value="${escapeHtml(f.location || "")}" placeholder="Remote / Lincoln, NE" />
        </div>
        <div>
          <label class="field-label">Comp</label>
          <input id="f-salary" class="field-input" value="${escapeHtml(f.salary || "")}" placeholder="optional" />
        </div>
      </div>

      <div class="mt-4 grid grid-cols-3 gap-4">
        <div>
          <label class="field-label">Applied</label>
          <input id="f-applied" type="date" class="field-input mono" value="${escapeHtml(f.applied || "")}" />
        </div>
        <div>
          <label class="field-label">Follow-up</label>
          <input id="f-followup" type="date" class="field-input mono" value="${escapeHtml(f.followup || "")}" />
        </div>
        <div>
          <label class="field-label">Fit score</label>
          <input id="f-score" class="field-input mono" value="${escapeHtml(f.score || "")}" placeholder="0–100" />
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label">Source</label>
        <input id="f-source" class="field-input mono" value="${escapeHtml(f.source || "")}" placeholder="company careers / auto:remotive" />
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label">Contact</label>
          <input id="f-contact" class="field-input" value="${escapeHtml(f.contact || "")}" placeholder="Hiring manager / recruiter" />
        </div>
        <div>
          <label class="field-label">Contact email</label>
          <input id="f-contact-email" class="field-input mono" value="${escapeHtml(f.contact_email || "")}" placeholder="name@company.com" />
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label">Notes</label>
        <textarea id="f-notes" class="field-textarea" rows="9" placeholder="Why it fits, contacts, JD keywords, interview notes…">${escapeHtml(f.notes || "")}</textarea>
      </div>

      <div class="mt-7 flex items-center justify-between border-t border-rule pt-4">
        <div>${isNew ? "" : `<button class="btn btn-danger" data-act="delete">Delete</button>`}</div>
        <div class="flex gap-2">
          <button class="btn btn-ghost" data-act="cancel">Cancel</button>
          <button class="btn btn-primary" data-act="save">${isNew ? "Create" : "Save"}</button>
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
}

function openNew() {
  state.mode = "new";
  state.current = null;
  renderSidebar();
  const today = new Date().toISOString().slice(0, 10);
  contentEl.innerHTML = formMarkup({ created: today, lane: "ai", status: "lead" }, { isNew: true });
  wireForm({ isNew: true });
  $("#f-company").focus();
}

function wireForm({ isNew }) {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    const fields = gatherFields();
    if (!fields.company) return toast("An application needs a company.");
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
      <div class="flex items-center justify-between">
        <div class="sheet-eyebrow">Source · ${escapeHtml(data.filename)}</div>
        <div class="seg">
          <button data-act="form">Form</button>
          <button class="active">Raw</button>
        </div>
      </div>
      <textarea id="f-raw" class="field-textarea mono mt-4" rows="24" spellcheck="false">${escapeHtml(data.raw)}</textarea>
      <div class="mt-5 flex justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="save">Save source</button>
      </div>
    </article>`;
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
  if (!confirm(`Delete "${stem(name)}"? This removes the file.`)) return;
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
    return `<div class="board-empty font-mono text-[11px] text-ink-faint">No ATS boards added.</div>`;
  }
  return criteriaBoards.map((b, i) =>
    `<div class="board-row">
      <span class="board-prov">${escapeHtml(b.provider)}</span>
      <span class="board-slug mono">${escapeHtml(b.slug)}</span>
      <button class="btn btn-ghost btn-xs" data-remove-board="${i}" type="button">✕</button>
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
      <p class="mt-1 font-mono text-[11px] text-ink-faint">
        Saved to <span class="mono">applications/.criteria.json</span> · used by every poll.
      </p>

      <div class="mt-5">
        <label class="field-label">Keywords — one per line · any match stages a lead</label>
        <textarea id="c-keywords" class="field-textarea mono" rows="8"
          placeholder="developer advocate&#10;ai engineer&#10;.net">${escapeHtml((c.keywords || []).join("\n"))}</textarea>
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label">Default lane — routes the cover letter</label>
          <select id="c-lane" class="field-input mono">${laneOptions(c.default_lane || "ai")}</select>
        </div>
        <div>
          <label class="field-label">Min fit score (0–100)</label>
          <input id="c-score" type="number" min="0" max="100" class="field-input mono"
            value="${escapeHtml(String(c.min_fit_score ?? 55))}" />
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label">Location filters</label>
        <label class="source-row">
          <input id="c-remote-only" type="checkbox"${c.remote_only ? " checked" : ""} />
          <span>Remote-friendly roles only</span>
        </label>
        <input id="c-exclude" class="field-input mono mt-2"
          value="${escapeHtml((c.exclude_locations || []).join(", "))}"
          placeholder="exclude locations, comma-separated (e.g. India, Brazil)" />
      </div>

      <div class="mt-4">
        <label class="field-label">Sources</label>
        <div class="source-grid">${sourceToggles(c.sources || {})}</div>
      </div>

      <div class="mt-4">
        <label class="field-label">ATS boards — follow a company's Greenhouse/Lever board</label>
        <div id="criteria-boards" class="board-list"></div>
        <div class="board-add mt-2">
          <select id="c-board-provider" class="field-input mono">
            ${ATS_PROVIDERS.map((p) => `<option value="${p}">${p}</option>`).join("")}
          </select>
          <input id="c-board-slug" class="field-input mono" placeholder="company slug (e.g. stripe)" />
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

// The structured résumé feeds the cover-letter prompt as the only facts the model
// may assert. Experience + links are dynamic lists (like ATS boards); because their
// rows hold live inputs, we sync the DOM back into these arrays before any add/remove
// re-render so in-progress edits aren't clobbered.
let resumeExperience = [];
let resumeLinks = [];

function expRows() {
  if (!resumeExperience.length)
    return `<div class="board-empty font-mono text-[11px] text-ink-faint">No experience added.</div>`;
  return resumeExperience.map((e, i) => `
    <div class="exp-row" data-exp="${i}">
      <div class="grid grid-cols-3 gap-2">
        <input class="field-input" data-exp-field="title" placeholder="Title" value="${escapeHtml(e.title || "")}" />
        <input class="field-input" data-exp-field="company" placeholder="Company" value="${escapeHtml(e.company || "")}" />
        <input class="field-input mono" data-exp-field="dates" placeholder="2020–2024" value="${escapeHtml(e.dates || "")}" />
      </div>
      <textarea class="field-textarea mono mt-2" rows="3" data-exp-field="highlights"
        placeholder="One highlight per line">${escapeHtml((e.highlights || []).join("\n"))}</textarea>
      <div class="mt-1 flex justify-end">
        <button class="btn btn-ghost btn-xs" data-remove-exp="${i}" type="button">✕ Remove</button>
      </div>
    </div>`).join("");
}

function syncExperienceFromDom() {
  contentEl.querySelectorAll("[data-exp]").forEach((row) => {
    const i = Number(row.dataset.exp);
    const val = (f) => row.querySelector(`[data-exp-field="${f}"]`).value;
    resumeExperience[i] = {
      title: val("title").trim(),
      company: val("company").trim(),
      dates: val("dates").trim(),
      highlights: val("highlights").split("\n").map((s) => s.trim()).filter(Boolean),
    };
  });
}

function renderExperience() {
  const el = $("#resume-experience");
  if (!el) return;
  el.innerHTML = expRows();
  el.querySelectorAll("[data-remove-exp]").forEach((btn) => {
    btn.onclick = () => {
      syncExperienceFromDom();
      resumeExperience.splice(Number(btn.dataset.removeExp), 1);
      renderExperience();
    };
  });
}

function linkRows() {
  if (!resumeLinks.length)
    return `<div class="board-empty font-mono text-[11px] text-ink-faint">No links added.</div>`;
  return resumeLinks.map((l, i) => `
    <div class="link-row grid grid-cols-[1fr_2fr_auto] gap-2 items-center" data-link="${i}">
      <input class="field-input" data-link-field="label" placeholder="Label (GitHub)" value="${escapeHtml(l.label || "")}" />
      <input class="field-input mono" data-link-field="url" placeholder="https://…" value="${escapeHtml(l.url || "")}" />
      <button class="btn btn-ghost btn-xs" data-remove-link="${i}" type="button">✕</button>
    </div>`).join("");
}

function syncLinksFromDom() {
  contentEl.querySelectorAll("[data-link]").forEach((row) => {
    const i = Number(row.dataset.link);
    const val = (f) => row.querySelector(`[data-link-field="${f}"]`).value;
    resumeLinks[i] = { label: val("label").trim(), url: val("url").trim() };
  });
}

function renderLinks() {
  const el = $("#resume-links");
  if (!el) return;
  el.innerHTML = linkRows();
  el.querySelectorAll("[data-remove-link]").forEach((btn) => {
    btn.onclick = () => {
      syncLinksFromDom();
      resumeLinks.splice(Number(btn.dataset.removeLink), 1);
      renderLinks();
    };
  });
}

function resumeMarkup(r) {
  return `
    <article class="sheet">
      <div class="sheet-eyebrow">Résumé</div>
      <h2 class="sheet-title">The facts behind your cover letters</h2>
      <p class="mt-1 font-mono text-[11px] text-ink-faint">
        The drafting AI may assert only what's here — no invented employers, titles, or metrics.
      </p>

      <div class="mt-5 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label">Full name</label>
          <input id="r-name" class="field-input" value="${escapeHtml(r.full_name || "")}" placeholder="Ada Lovelace" />
        </div>
        <div>
          <label class="field-label">Location</label>
          <input id="r-location" class="field-input" value="${escapeHtml(r.location || "")}" placeholder="Remote / Lincoln, NE" />
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label">Headline</label>
        <input id="r-headline" class="field-input" value="${escapeHtml(r.headline || "")}" placeholder="Backend engineer · distributed systems" />
      </div>

      <div class="mt-4">
        <label class="field-label">Summary</label>
        <textarea id="r-summary" class="field-textarea" rows="4" placeholder="A few sentences on what you do and the value you bring.">${escapeHtml(r.summary || "")}</textarea>
      </div>

      <div class="mt-4">
        <label class="field-label">Experience</label>
        <div id="resume-experience" class="board-list space-y-3"></div>
        <button class="btn btn-ghost mt-2" data-act="add-exp" type="button">+ Add role</button>
      </div>

      <div class="mt-4 grid grid-cols-2 gap-4">
        <div>
          <label class="field-label">Skills — one per line or comma-separated</label>
          <textarea id="r-skills" class="field-textarea mono" rows="6"
            placeholder="C#&#10;PostgreSQL&#10;Distributed systems">${escapeHtml((r.skills || []).join("\n"))}</textarea>
        </div>
        <div>
          <label class="field-label">Certifications — one per line</label>
          <textarea id="r-certs" class="field-textarea mono" rows="6"
            placeholder="AWS Solutions Architect&#10;CKA">${escapeHtml((r.certifications || []).join("\n"))}</textarea>
        </div>
      </div>

      <div class="mt-4">
        <label class="field-label">Links</label>
        <div id="resume-links" class="board-list space-y-2"></div>
        <button class="btn btn-ghost mt-2" data-act="add-link" type="button">+ Add link</button>
      </div>

      <div class="mt-7 flex items-center justify-end gap-2 border-t border-rule pt-4">
        <button class="btn btn-ghost" data-act="cancel">Cancel</button>
        <button class="btn btn-primary" data-act="save">Save résumé</button>
      </div>
    </article>`;
}

function gatherResume() {
  syncExperienceFromDom();
  syncLinksFromDom();
  const splitList = (sel) => $(sel).value.split(/[\n,]/).map((s) => s.trim()).filter(Boolean);
  return {
    full_name: $("#r-name").value.trim(),
    headline: $("#r-headline").value.trim(),
    location: $("#r-location").value.trim(),
    summary: $("#r-summary").value.trim(),
    skills: splitList("#r-skills"),
    certifications: splitList("#r-certs"),
    experience: resumeExperience.filter(
      (e) => e.company || e.title || e.dates || (e.highlights && e.highlights.length)),
    links: resumeLinks.filter((l) => l.url),
  };
}

function wireResume() {
  contentEl.querySelector('[data-act="cancel"]').onclick = () =>
    state.current ? openApp(state.current) : renderEmpty();
  contentEl.querySelector('[data-act="add-exp"]').onclick = () => {
    syncExperienceFromDom();
    resumeExperience.push({ title: "", company: "", dates: "", highlights: [] });
    renderExperience();
  };
  contentEl.querySelector('[data-act="add-link"]').onclick = () => {
    syncLinksFromDom();
    resumeLinks.push({ label: "", url: "" });
    renderLinks();
  };
  contentEl.querySelector('[data-act="save"]').onclick = async () => {
    try {
      await api("PUT", "/api/resume", gatherResume());
      toast("Résumé saved.");
    } catch (e) {
      toast(e.message);
    }
  };
}

// Settings · Résumé tab.
async function loadResumeTab(body, gen = settingsGen) {
  const r = await api("GET", "/api/resume");
  if (settingsSuperseded(gen)) return;
  resumeExperience = (r.experience || []).map((e) => ({
    title: e.title || "", company: e.company || "", dates: e.dates || "",
    highlights: e.highlights || [],
  }));
  resumeLinks = (r.links || []).map((l) => ({ label: l.label || "", url: l.url || "" }));
  body.innerHTML = resumeMarkup(r);
  renderExperience();
  renderLinks();
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
      <p class="mt-1 font-mono text-[11px] text-ink-faint">
        Any OpenAI-compatible endpoint — a local model (Ollama / vLLM) or a hosted provider.
        Leave a field blank to inherit the instance default.
      </p>

      <div class="mt-5">
        <label class="source-row">
          <input id="l-enabled" type="checkbox"${s.cover_letters_enabled === false ? "" : " checked"} />
          <span>Enable cover-letter drafting</span>
        </label>
        <p class="mt-1 font-mono text-[11px] text-ink-faint">
          Untick if you don't want to run a model — the app hides every drafting affordance
          and never calls an LLM for this account.
        </p>
      </div>

      <div class="mt-5">
        <label class="field-label">Base URL</label>
        <input id="l-base" class="field-input mono" value="${escapeHtml(s.base_url || "")}"
          placeholder="${escapeHtml(inst.base_url || "http://localhost:11434/v1")}" />
      </div>

      <div class="mt-4">
        <label class="field-label">Model</label>
        <input id="l-model" class="field-input mono" value="${escapeHtml(s.model || "")}"
          placeholder="${escapeHtml(inst.model || "llama3.1")}" />
      </div>

      <div class="mt-4">
        <label class="field-label">API key ${secretsOff ? "— unavailable on this instance" : "— write-only"}</label>
        <input id="l-key" type="password" class="field-input mono" autocomplete="off"
          placeholder="${secretsOff ? "operator hasn't enabled per-tenant keys" : "leave blank to keep the saved key"}"
          ${secretsOff ? "disabled" : ""} />
        <label class="source-row mt-2 ${s.has_api_key && !secretsOff ? "" : "hidden"}">
          <input id="l-clear" type="checkbox" /> <span>Remove the saved key</span>
        </label>
        <p class="mt-1 font-mono text-[11px] text-ink-faint">${escapeHtml(keyNote)}</p>
      </div>

      <div class="mt-4 rounded-md border border-rule px-3 py-2 font-mono text-[11px] text-ink-faint">
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
  const [apps, stats] = await Promise.all([
    api("GET", "/api/apps"),
    api("GET", "/api/stats"),
  ]);
  state.apps = apps;
  state.stats = stats;
  renderPipeline();
  renderSidebar();
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
  if (!confirm(prompt)) return;
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

async function openSettings(tab) {
  if (SETTINGS_TABS.some(([k]) => k === tab)) state.settingsTab = tab;
  const gen = ++settingsGen;
  state.mode = "settings";
  state.current = null;
  renderSidebar();
  contentEl.innerHTML = `
    <nav class="flex flex-wrap items-center gap-2">
      ${SETTINGS_TABS.map(([k, label]) =>
        `<button class="btn ${k === state.settingsTab ? "btn-primary" : "btn-ghost"}"
           data-tab="${k}" type="button">${label}</button>`).join("")}
    </nav>
    <div id="settings-body" class="mt-4"></div>`;
  contentEl.querySelectorAll("[data-tab]").forEach((b) => {
    b.onclick = () => openSettings(b.dataset.tab);
  });
  const body = $("#settings-body");
  try {
    if (state.settingsTab === "criteria") await loadCriteriaTab(body, gen);
    else if (state.settingsTab === "resume") await loadResumeTab(body, gen);
    else if (state.settingsTab === "ai") await loadLlmTab(body, gen);
    else if (state.settingsTab === "blacklist") await loadBlacklistTab(body);
    else await loadAccountTab(body);
  } catch (e) {
    toast(e.message);
  }
}

// True once a newer openSettings has started — a stale loader must not touch the DOM.
function settingsSuperseded(gen) {
  return gen !== settingsGen;
}
$("#settings-btn").addEventListener("click", () => openSettings());

// Settings · Blacklist tab — the first place blacklisted companies can be seen and
// un-blacklisted (adding happens here or via "Blacklist company" on an app).
async function loadBlacklistTab(body) {
  const companies = await api("GET", "/api/blacklist");
  body.innerHTML = `
    <article class="sheet">
      <div class="sheet-eyebrow">Blacklist</div>
      <h2 class="sheet-title">Companies the poller skips</h2>
      <p class="mt-1 font-mono text-[11px] text-ink-faint">
        Leads from these companies are never staged. Names are stored normalized
        (lowercased, punctuation-insensitive).
      </p>
      <div id="bl-list" class="board-list mt-5">
        ${companies.length
          ? companies.map((c) =>
              `<div class="board-row">
                <span class="board-slug mono">${escapeHtml(c)}</span>
                <button class="btn btn-ghost btn-xs" data-bl-remove="${escapeHtml(c)}" type="button">✕</button>
              </div>`).join("")
          : `<div class="board-empty font-mono text-[11px] text-ink-faint">No blacklisted companies.</div>`}
      </div>
      <div class="board-add mt-3">
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
        <p class="font-mono text-[11px] text-ink-faint">
          Everything: applications, criteria, blacklist. Import it on another instance to move home.
        </p>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <button class="btn btn-ghost" data-act="export" type="button">⤓ Export my data</button>
          <button class="btn btn-ghost" data-act="import" type="button">⤒ Import a file</button>
        </div>
      </div>

      <div class="mt-5 border-t border-rule pt-4">
        <div class="field-label">Share — anonymized opportunity list</div>
        <p class="font-mono text-[11px] text-ink-faint">
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
        <p class="font-mono text-[11px] text-ink-faint">
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
    if (!confirm("Delete your account? Every application, setting, and cover letter goes with it.")) return;
    if (!confirm("Last chance — this is immediate and unrecoverable. Really delete?")) return;
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
    const [apps, stats] = await Promise.all([
      api("GET", "/api/apps"),
      api("GET", "/api/stats"),
    ]);
    if (JSON.stringify([apps, stats]) === JSON.stringify([state.apps, state.stats])) return;
    state.apps = apps;
    state.stats = stats;
    renderPipeline();
    renderSidebar();
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
  const active = listEl.querySelector(".index-card.active");
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

// ---- Theme switcher -------------------------------------------------------

const THEMES = ["midnight", "carbon", "dusk", "paper", "mint"];
function applyTheme(name) {
  const theme = THEMES.includes(name) ? name : "midnight";
  document.documentElement.dataset.theme = theme;
  try { localStorage.setItem("applytrack-theme", theme); } catch (_) {}
  document.querySelectorAll(".swatch").forEach((s) => {
    s.classList.toggle("active", s.dataset.theme === theme);
  });
}
function initTheme() {
  let saved = "midnight";
  try { saved = localStorage.getItem("applytrack-theme") || saved; } catch (_) {}
  applyTheme(saved);
  document.querySelectorAll(".swatch").forEach((s) => {
    s.addEventListener("click", () => applyTheme(s.dataset.theme));
  });
}
initTheme();

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
