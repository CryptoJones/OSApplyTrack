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

async function mockApi(page) {
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
    else if (path === "/api/criteria") body = {
      keywords: ["engineer"], default_lane: "dotnet", min_fit_score: 55,
      remote_only: true, exclude_locations: [], sources: {}, ats_boards: [], rss_feeds: [],
    };
    else if (path === "/api/resume") body = {
      full_name: "", location: "", headline: "", summary: "",
      experience: [], skills: [], certifications: [], links: [],
    };
    else if (path === "/api/blacklist") body = [];
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
  for (const tab of ["Criteria", "Résumé", "AI", "Blacklist", "Account"]) {
    await page.getByRole("tab", { name: tab, exact: true }).click();
    await expect(page.getByRole("tabpanel")).not.toBeEmpty();
    await expectNoSeriousViolations(page);
  }
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

test("a populated desktop list scrolls without moving the application shell", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== "desktop", "Desktop master-detail layout");
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
