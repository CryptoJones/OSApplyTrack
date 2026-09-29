// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
//
// Regenerate docs/screenshot.png + docs/mobile.png from a *running* app — not from
// the mocked Playwright suite, whose fixtures are deliberately thin. The account it
// shoots is `scripts/demo-seed.json`, imported through POST /api/account/import; the
// companies below must exist in whatever account the session cookie belongs to.
// See "Regenerating the README screenshots" in README.md for the full recipe.
import { chromium } from "@playwright/test";
import path from "node:path";

const baseUrl = process.env.APPLYTRACK_SCREENSHOT_URL || "http://localhost:5049";
const cookieName = process.env.APPLYTRACK_SESSION_NAME;
const cookieValue = process.env.APPLYTRACK_SESSION_VALUE;
if (!cookieName || !cookieValue) {
  throw new Error("Set APPLYTRACK_SESSION_NAME and APPLYTRACK_SESSION_VALUE from an authenticated local session.");
}

const browser = await chromium.launch({ channel: "chromium" });
const cookie = {
  name: cookieName,
  value: cookieValue,
  url: baseUrl,
  httpOnly: true,
  sameSite: "Lax",
};

// Phone shots render the text a step smaller (87.5% root size) so more of the sheet fits
// in 390x844 without looking cramped. Capture-only: the app itself is unchanged.
const MOBILE_FONT_SCALE = "87.5%";

async function capture({ viewport, deviceScaleFactor, mobile, company, output }) {
  const context = await browser.newContext({
    viewport,
    deviceScaleFactor,
    isMobile: mobile,
    hasTouch: mobile,
    colorScheme: "light",
  });
  await context.addCookies([cookie]);
  // The public-beta terms dialog opens once per session and is modal; acknowledge it
  // up front (the same sessionStorage key the SPA writes) so it never covers the shot.
  await context.addInitScript(() => {
    try { sessionStorage.setItem("applytrack.beta-ack", "1"); } catch (_) {}
  });
  const page = await context.newPage();
  await page.goto(baseUrl, { waitUntil: "networkidle" });
  if (mobile) await page.addStyleTag({ content: `html { font-size: ${MOBILE_FONT_SCALE}; }` });
  await page.getByRole("button", { name: new RegExp(company) }).click();
  await page.getByRole("heading", { name: company, exact: true }).waitFor();
  // Opening an application moves focus to its heading, which leaves a scroll
  // container part-way down the sheet — on mobile that is #workspace, and a viewport
  // shot then starts mid-page with the company name cropped off. Reset every scroll
  // position so the capture is deterministic and framed from the top of the sheet.
  await page.evaluate(() => {
    window.scrollTo(0, 0);
    document.querySelectorAll("*").forEach((el) => {
      if (el.scrollTop) el.scrollTop = 0;
    });
  });
  await page.screenshot({ path: path.resolve(output), fullPage: false });
  await context.close();
}

try {
  await capture({
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
    mobile: false,
    company: "Kindred Robotics",
    output: "docs/screenshot.png",
  });
  await capture({
    viewport: { width: 390, height: 844 },
    deviceScaleFactor: 2,
    mobile: true,
    company: "Signal Harbor",
    output: "docs/mobile.png",
  });
  console.log("Updated docs/screenshot.png and docs/mobile.png from the local app.");
} finally {
  await browser.close();
}
