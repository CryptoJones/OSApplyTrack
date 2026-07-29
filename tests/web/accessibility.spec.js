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
  score: "87",
  snippet: "Build accessible software.",
};

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
      body = [application];
    }
    else if (path === "/api/stats") body = { status: { lead: 1 }, lane: { dotnet: 1 } };
    else if (path === `/api/apps/${application.filename}` && method === "GET") body = detail;
    else if (path === "/api/criteria") body = {
      keywords: ["engineer"], default_lane: "dotnet", min_fit_score: 55,
      remote_only: true, exclude_locations: [], sources: {}, ats_boards: [],
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
  await expect(page.getByLabel(/Company/)).toBeFocused();
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
