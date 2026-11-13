// Run the frozen frontend test without adding/changing anything in frontend/.
// NODE_PATH points at a separately installed @playwright/test (see auth-parity.md).
const path = require("node:path");
module.exports = {
  testDir: path.resolve(__dirname, "../../../frontend/tests"),
  testMatch: process.env.ARCHIVE_FROZEN_TEST || "passkey.spec.ts",
  workers: 1,
  timeout: 60000,
  use: { baseURL: process.env.ARCHIVE_BASE_URL, browserName: "chromium", launchOptions: { executablePath: process.env.ARCHIVE_CHROMIUM } },
  outputDir: process.env.ARCHIVE_BROWSER_OUTPUT || "/tmp/opencode/221-browser-results",
  reporter: "line",
};
