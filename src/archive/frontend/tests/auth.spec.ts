import { expect, test } from "@playwright/test";

test("Anmelden validates the email address without a backend", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.route("**/api/auth/me", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ authenticated: false }),
    }),
  );

  await page.goto("/anmelden/");
  await expect(
    page.getByRole("heading", { name: "Anmelden", exact: true }),
  ).toBeVisible();
  await expect(page.getByLabel("E-Mail-Adresse")).toBeVisible();

  await page.getByRole("button", { name: "Code anfordern" }).click();
  await expect(page.locator("#email-fehler")).toHaveText(
    "Bitte E-Mail-Adresse eingeben.",
  );

  await page.getByLabel("E-Mail-Adresse").fill("keine-mail");
  await page.getByRole("button", { name: "Code anfordern" }).click();
  await expect(page.locator("#email-fehler")).toHaveText(
    "Bitte gültige E-Mail-Adresse eingeben.",
  );

  expect(errors).toEqual([]);
});

test("Anmelden requests a code with a mocked API", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.route("**/api/auth/me", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ authenticated: false }),
    }),
  );
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ token: "test" }),
    }),
  );
  await page.route("**/api/auth/code/request", (route) =>
    route.fulfill({
      status: 202,
      contentType: "application/json",
      body: JSON.stringify({}),
    }),
  );

  await page.goto("/anmelden/");
  await page.getByLabel("E-Mail-Adresse").fill("mitglied@example.at");
  await page.getByRole("button", { name: "Code anfordern" }).click();

  await expect(page.getByLabel("Code aus der E-Mail")).toBeVisible();
  await expect(page.getByText("Code gesendet.")).toBeVisible();

  expect(errors).toEqual([]);
});

test("Archiv gate links to Anmelden when signed out", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.route("**/api/auth/me", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ authenticated: false }),
    }),
  );

  await page.goto("/archiv/");
  await expect(
    page.getByText("Bitte anmelden, um den Mitgliederbereich zu sehen."),
  ).toBeVisible();
  await page.getByRole("main").getByRole("link", { name: "Anmelden" }).click();
  await expect(page).toHaveURL(/\/anmelden\//);
  await expect(
    page.getByRole("heading", { name: "Anmelden", exact: true }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});
