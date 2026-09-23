import { expect, test } from "@playwright/test";

test("German deep link renders the actual API version and survives refresh", async ({
  page,
  request,
  isMobile,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const build = await (await request.get("/api/build")).json();
  await page.goto("/system/status/");
  await expect(
    page.getByRole("heading", { name: "Systemstatus", exact: true }),
  ).toBeVisible();
  await expect(page.getByTestId("build-version")).toHaveText(build.version);
  await expect(page.locator("html")).toHaveAttribute("lang", "de-AT");
  await page.reload();
  await expect(page.getByTestId("build-version")).toHaveText(build.version);
  if (isMobile)
    await page.getByRole("button", { name: "Menü", exact: true }).click();
  await page.getByRole("link", { name: "Archiv", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Was wir singen, bleibt bei uns." }),
  ).toBeVisible();
  if (isMobile)
    await page.getByRole("button", { name: "Menü", exact: true }).click();
  await page.getByRole("link", { name: "Systemstatus", exact: true }).click();
  await expect(page.getByTestId("build-version")).toHaveText(build.version);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  expect(errors).toEqual([]);
});

test("API failures stay JSON and development operations stay local", async ({
  page,
  request,
}) => {
  const build = await (await request.get("/api/build")).json();
  const missing = await request.get("/api/not-found.js");
  expect(missing.status()).toBe(404);
  expect(missing.headers()["content-type"]).toContain(
    "application/problem+json",
  );
  const rejected = await request.post("/api/dev/exercise");
  expect(rejected.status()).toBe(build.development ? 400 : 404);
  await page.goto("/system/status/");
  await expect(page.getByTestId("build-version")).toBeVisible();
  if (build.development) {
    await page.getByRole("button", { name: "Lokale Dienste prüfen" }).click();
    await expect(page.getByRole("status")).toContainText(
      "Testmail wurde gesendet.",
      { timeout: 45_000 },
    );
  } else {
    await expect(
      page.getByRole("button", { name: "Lokale Dienste prüfen" }),
    ).toHaveCount(0);
  }
});

test("API outage offers a working retry", async ({ page }) => {
  await page.route("**/api/build", (route) =>
    route.fulfill({ status: 503, body: "unavailable" }),
  );
  await page.goto("/system/status/");
  await expect(
    page.getByText("Das Archiv antwortet nicht. Bitte erneut versuchen."),
  ).toBeVisible();
  await page.unroute("**/api/build");
  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(page.getByTestId("build-version")).toBeVisible();
});
