import { expect, test } from "@playwright/test";

const mailUrl = process.env.ARCHIVE_MAIL_URL;

test("Vollständiger Anmeldefluss mit E-Mail-Code", async ({
  page,
  request,
}, testInfo) => {
  // Codes are single-use and resends are suppressed for 60 seconds, so
  // parallel projects must not share one address (both would verify the same
  // code and exactly one would lose by design).
  const memberEmail =
    testInfo.project.name === "mobile"
      ? "redaktion@liedertafel.test"
      : "mitglied@liedertafel.test";
  const build = await request.get("/api/build");
  const development = build.ok()
    ? (await build.json()).development === true
    : false;
  test.skip(
    !development || !mailUrl,
    "Nur mit Entwicklungs-Backend und Mailpit ausführbar.",
  );
  // Leeres Postfach: Der erste Treffer ist danach garantiert der eigene Code.
  await request.delete(`${mailUrl}/api/v1/messages`);
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));

  const tokenResponse = await request.get("/api/antiforgery");
  const setCookie =
    (await tokenResponse.headersArray()).find(
      (header) => header.name.toLowerCase() === "set-cookie",
    )?.value ?? "";
  const csrfCookie = setCookie.split(";")[0];
  const { token } = await tokenResponse.json();
  const seed = await request.post("/api/dev/auth/seed", {
    headers: { Cookie: csrfCookie, "X-CSRF-TOKEN": token },
    data: {},
  });
  expect(seed.ok()).toBe(true);

  await page.goto("/anmelden/");
  await page.getByLabel("E-Mail-Adresse").fill(memberEmail);
  await page.getByRole("button", { name: "Code anfordern" }).click();
  await expect(page.getByLabel("Code aus der E-Mail")).toBeVisible();

  let code = "";
  for (let attempt = 0; attempt < 20 && !code; attempt++) {
    const list = await request.get(`${mailUrl}/api/v1/messages?limit=50`);
    const { messages } = await list.json();
    for (const message of messages ?? []) {
      const detail = await (
        await request.get(`${mailUrl}/api/v1/message/${message.ID}`)
      ).json();
      if (!JSON.stringify(detail).toLowerCase().includes(memberEmail)) {
        continue;
      }
      const match = /Ihr Anmeldecode lautet:\s*(\d{6})/.exec(detail.Text ?? "");
      if (match) {
        code = match[1];
        break;
      }
    }
    if (!code) {
      await page.waitForTimeout(500);
    }
  }
  expect(code).toMatch(/^\d{6}$/);

  await page.getByLabel("Code aus der E-Mail").fill(code);
  await page.getByRole("button", { name: "Anmelden" }).click();
  await expect(page).toHaveURL(/\/archiv\//);
  await expect(page.getByText("Willkommen im Archiv.")).toBeVisible();

  await page
    .getByRole("main")
    .getByRole("button", { name: "Abmelden" })
    .click();
  await expect(page).toHaveURL(/\/anmelden\//);
  await expect(
    page.getByRole("heading", { name: "Anmelden", exact: true }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});
