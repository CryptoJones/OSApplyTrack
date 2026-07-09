// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark
import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "tests/web",
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? "github" : "list",
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "retain-on-failure",
  },
  webServer: {
    command: "npx http-server api/ApplyTrack.Api/wwwroot -a 127.0.0.1 -p 4173 -c-1",
    url: "http://127.0.0.1:4173",
    reuseExistingServer: !process.env.CI,
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"], channel: "chromium" } },
    { name: "mobile", use: { ...devices["iPhone 13"], browserName: "chromium", channel: "chromium" } },
  ],
});
