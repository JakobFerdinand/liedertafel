import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "./tests",
  timeout: 60_000,
  use: {
    baseURL: process.env.ARCHIVE_BASE_URL ?? "http://localhost:3000",
    // Die Veröffentlichungszeit liest sich in Wien (lib/events.ts
    // publishedAtText); ohne feste Zone würde der Testläufer-Rechner die
    // Anzeige verschieben.
    timezoneId: "Europe/Vienna",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
    { name: "mobile", use: { ...devices["Pixel 7"] } },
  ],
});
