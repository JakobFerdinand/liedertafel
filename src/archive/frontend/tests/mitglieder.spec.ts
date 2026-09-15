import { expect, type Page, test } from "@playwright/test";

const mailUrl = process.env.ARCHIVE_MAIL_URL;

const adminMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000001",
  email: "verwaltung@liedertafel.test",
  displayName: "Testverwaltung",
  roles: ["Administrator"],
  verifiedAt: new Date().toISOString(),
};

const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

const mitglieder = [
  {
    accountId: "00000000-0000-0000-0000-000000000002",
    email: "mitglied@liedertafel.test",
    displayName: "Testmitglied",
    roles: ["Member"],
    status: "active",
    invitationId: null,
    invitedAt: null,
    invitedByAccountId: null,
    acceptedAt: "2026-09-01T10:00:00.000Z",
    lastInvitationSentAt: "2026-09-01T09:00:00.000Z",
    invitationMailStatus: "sent",
  },
  {
    accountId: "00000000-0000-0000-0000-000000000003",
    email: "neu@liedertafel.test",
    displayName: "Neue Stimme",
    roles: ["Member"],
    status: "invited",
    invitationId: "00000000-0000-0000-0000-000000000003",
    invitedAt: "2026-09-14T10:00:00.000Z",
    invitedByAccountId: "00000000-0000-0000-0000-000000000001",
    acceptedAt: null,
    lastInvitationSentAt: "2026-09-14T10:00:00.000Z",
    invitationMailStatus: "sent",
  },
];

async function mockAdminSeite(page: Page) {
  await page.route("**/api/auth/me", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(adminMe),
    }),
  );
  await page.route("**/api/admin/members", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ members: mitglieder }),
    }),
  );
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ token: "test" }),
    }),
  );
}

test("Verwaltung validates the invitation email without a backend", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockAdminSeite(page);

  await page.goto("/verwaltung/");
  await expect(
    page.getByRole("heading", { name: "Mitgliederverwaltung" }),
  ).toBeVisible();

  await page.getByRole("button", { name: "Einladung senden" }).click();
  await expect(page.locator("#einladung-email-fehler")).toHaveText(
    "Bitte E-Mail-Adresse eingeben.",
  );

  await page.getByLabel("E-Mail-Adresse").fill("keine-mail");
  await page.getByRole("button", { name: "Einladung senden" }).click();
  await expect(page.locator("#einladung-email-fehler")).toHaveText(
    "Bitte gültige E-Mail-Adresse eingeben.",
  );

  expect(errors).toEqual([]);
});

test("Verwaltung gates members without administration rights", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.route("**/api/auth/me", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(memberMe),
    }),
  );

  await page.goto("/verwaltung/");
  await expect(
    page.getByText("Keine Berechtigung für die Mitgliederverwaltung."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Verwaltung invites and resends with a mocked API", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockAdminSeite(page);
  await page.route("**/api/admin/invitations", (route) =>
    route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        message:
          "Einladung erstellt. Die E-Mail wurde zum Versand angenommen; die Zustellung wird nicht bestätigt.",
      }),
    }),
  );
  await page.route("**/api/admin/invitations/resend", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        message:
          "Einladung erneut zum Versand übergeben. Die Zustellung wird nicht bestätigt.",
      }),
    }),
  );

  await page.goto("/verwaltung/");
  await expect(page.getByText("mitglied@liedertafel.test")).toBeVisible();
  await expect(page.getByText("Eingeladen").first()).toBeVisible();
  await expect(page.getByText("Aktiv").first()).toBeVisible();

  await page.getByLabel("E-Mail-Adresse").fill("chor@beispiel.at");
  await page.getByRole("button", { name: "Einladung senden" }).click();
  await expect(page.getByText("Einladung erstellt")).toBeVisible();

  await page
    .getByRole("row", { name: /neu@liedertafel\.test/ })
    .getByRole("button", { name: "Erneut senden" })
    .click();
  await expect(page.getByText("erneut zum Versand übergeben")).toBeVisible();

  expect(errors).toEqual([]);
});

test("Verwaltung prompts re-login when verification is stale", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockAdminSeite(page);
  await page.route("**/api/admin/invitations", (route) =>
    route.fulfill({
      status: 403,
      contentType: "application/problem+json",
      body: JSON.stringify({
        title:
          "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich.",
      }),
    }),
  );

  await page.goto("/verwaltung/");
  await page.getByLabel("E-Mail-Adresse").fill("chor@beispiel.at");
  await page.getByRole("button", { name: "Einladung senden" }).click();
  await expect(page.getByText(/erneute Anmeldung mit Code/)).toBeVisible();

  expect(errors).toEqual([]);
});

test("Vollständiger Einladungsfluss mit E-Mail-Code", async ({
  page,
  request,
  context,
}, testInfo) => {
  const build = await request.get("/api/build");
  const development = build.ok()
    ? (await build.json()).development === true
    : false;
  test.skip(
    !development || !mailUrl,
    "Nur mit Entwicklungs-Backend und Mailpit ausführbar.",
  );
  const suffix = `${testInfo.project.name}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
  const neueAdresse = `neu-${suffix}@liedertafel.test`.toLowerCase();
  await request.delete(`${mailUrl}/api/v1/messages`);
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));

  async function csrf(): Promise<{ cookie: string; token: string }> {
    const tokenResponse = await request.get("/api/antiforgery");
    const setCookie =
      (await tokenResponse.headersArray()).find(
        (header) => header.name.toLowerCase() === "set-cookie",
      )?.value ?? "";
    const { token } = await tokenResponse.json();
    return { cookie: setCookie.split(";")[0], token };
  }

  async function leseCode(empfaenger: string): Promise<string> {
    for (let versuch = 0; versuch < 20; versuch++) {
      const list = await request.get(`${mailUrl}/api/v1/messages?limit=50`);
      const { messages } = await list.json();
      for (const message of messages ?? []) {
        const detail = await (
          await request.get(`${mailUrl}/api/v1/message/${message.ID}`)
        ).json();
        if (!JSON.stringify(detail).toLowerCase().includes(empfaenger)) {
          continue;
        }
        const treffer = /Ihr Anmeldecode lautet:\s*(\d{6})/.exec(
          detail.Text ?? "",
        );
        if (treffer) return treffer[1];
      }
      await page.waitForTimeout(500);
    }
    throw new Error(`Kein Anmeldecode für ${empfaenger} gefunden.`);
  }

  // Testkonten sicherstellen und als Verwaltung anmelden.
  const seedCsrf = await csrf();
  const seed = await request.post("/api/dev/auth/seed", {
    headers: { Cookie: seedCsrf.cookie, "X-CSRF-TOKEN": seedCsrf.token },
    data: {},
  });
  expect(seed.ok()).toBe(true);

  const adminCsrf = await csrf();
  const adminAnfrage = await request.post("/api/auth/code/request", {
    headers: { Cookie: adminCsrf.cookie, "X-CSRF-TOKEN": adminCsrf.token },
    data: { email: "verwaltung@liedertafel.test" },
  });
  expect(adminAnfrage.status()).toBe(202);
  const adminCode = await leseCode("verwaltung@liedertafel.test");
  const verifyCsrf = await csrf();
  const adminVerify = await request.post("/api/auth/code/verify", {
    headers: { Cookie: verifyCsrf.cookie, "X-CSRF-TOKEN": verifyCsrf.token },
    data: { email: "verwaltung@liedertafel.test", code: adminCode },
  });
  expect(adminVerify.ok()).toBe(true);
  const sessionCookie =
    (await adminVerify.headersArray())
      .find(
        (header) =>
          header.name.toLowerCase() === "set-cookie" &&
          header.value.startsWith("archive.auth="),
      )
      ?.value.split(";")[0] ?? "";
  expect(sessionCookie).toContain("archive.auth=");
  const basis = new URL(page.url().split("/").slice(0, 3).join("/"));
  await context.addCookies([
    {
      name: "archive.auth",
      value: decodeURIComponent(sessionCookie.split("=")[1]),
      domain: basis.hostname,
      path: "/",
      httpOnly: true,
      sameSite: "Strict",
      secure: false,
    },
  ]);

  // Einladung über die deutsche Verwaltungsoberfläche erstellen.
  await page.goto("/verwaltung/");
  await expect(
    page.getByRole("heading", { name: "Mitgliederverwaltung" }),
  ).toBeVisible();
  await page.getByLabel("E-Mail-Adresse").fill(neueAdresse);
  await page.getByLabel("Name (optional)").fill("Neue Stimme");
  await page.getByLabel("Rolle").selectOption("Member");
  await page.getByRole("button", { name: "Einladung senden" }).click();
  await expect(page.getByText("Einladung erstellt")).toBeVisible();
  await expect(page.getByText(neueAdresse)).toBeVisible();

  // Einladungsmail im lokalen Postfach prüfen (Annahme, keine Zustellgarantie).
  let einladungGefunden = false;
  for (let versuch = 0; versuch < 20 && !einladungGefunden; versuch++) {
    const list = await request.get(`${mailUrl}/api/v1/messages?limit=50`);
    const { messages } = await list.json();
    for (const message of messages ?? []) {
      const detail = await (
        await request.get(`${mailUrl}/api/v1/message/${message.ID}`)
      ).json();
      const text = JSON.stringify(detail).toLowerCase();
      if (text.includes(neueAdresse) && text.includes("liedertafel-archiv")) {
        einladungGefunden = true;
        break;
      }
    }
    if (!einladungGefunden) await page.waitForTimeout(500);
  }
  expect(einladungGefunden).toBe(true);

  // Eingeladene nimmt an: Code anfordern, bestätigen, Mitgliederbereich öffnen.
  const codeCsrf = await csrf();
  const codeAnfrage = await request.post("/api/auth/code/request", {
    headers: { Cookie: codeCsrf.cookie, "X-CSRF-TOKEN": codeCsrf.token },
    data: { email: neueAdresse },
  });
  expect(codeAnfrage.status()).toBe(202);
  const code = await leseCode(neueAdresse);
  const bestaetigenCsrf = await csrf();
  const bestaetigt = await request.post("/api/auth/code/verify", {
    headers: {
      Cookie: bestaetigenCsrf.cookie,
      "X-CSRF-TOKEN": bestaetigenCsrf.token,
    },
    data: { email: neueAdresse, code },
  });
  expect(bestaetigt.ok()).toBe(true);
  const bestaetigtBody = await bestaetigt.json();
  expect(bestaetigtBody.accountId).toBeTruthy();

  expect(errors).toEqual([]);
});
