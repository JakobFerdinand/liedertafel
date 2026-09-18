import { type CDPSession, expect, test } from "@playwright/test";

const mailUrl = process.env.ARCHIVE_MAIL_URL;

/**
 * ARC-011-1 full passkey ceremony against the real development backend.
 * Uses Chromium's virtual WebAuthn authenticator (CDP), so enrollment and
 * username-less assertion run end-to-end: email-code sign-in, passkey
 * enrollment in the Archiv area, logout, and login with "Mit Passkey
 * anmelden". Needs the local stack (AppHost) with Mailpit, like auth-flow.
 */
test("Passkey registrieren und wieder anmelden", async ({
  page,
  request,
}, testInfo) => {
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
  await request.delete(`${mailUrl}/api/v1/messages`);
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));

  // Virtual internal authenticator with resident keys and user verification.
  const cdp: CDPSession = await page.context().newCDPSession(page);
  await cdp.send("WebAuthn.enable", { enableUI: false });
  await cdp.send("WebAuthn.addVirtualAuthenticator", {
    options: {
      protocol: "ctap2",
      transport: "internal",
      hasResidentKey: true,
      hasUserVerification: true,
      isUserVerified: true,
      automaticPresenceSimulation: true,
    },
  });

  // Seed and sign in with the email code.
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
  // Dev hydration can reset the form between fill and submit; retry until
  // the code step actually appears.
  for (let attempt = 0; ; attempt++) {
    await page.getByLabel("E-Mail-Adresse").fill(memberEmail);
    await page.getByRole("button", { name: "Code anfordern" }).click();
    try {
      await page
        .getByLabel("Code aus der E-Mail")
        .waitFor({ state: "visible", timeout: 5000 });
      break;
    } catch {
      if (attempt >= 2)
        throw new Error("Anmeldeformular blieb leer (Hydration).");
    }
  }

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

  // Remove passkeys left over from earlier runs so the enrollment below
  // starts from a clean list (and the remove endpoint stays exercised).
  const cleanup = await page.evaluate(async () => {
    const csrf = await (
      await fetch("/api/antiforgery", {
        credentials: "same-origin",
        cache: "no-store",
      })
    ).json();
    const response = await fetch("/api/auth/passkeys", {
      credentials: "same-origin",
      cache: "no-store",
    });
    if (!response.ok) throw new Error("Passkey-Liste fehlgeschlagen.");
    const { passkeys } = (await response.json()) as {
      passkeys: { credentialId: string }[];
    };
    for (const passkey of passkeys ?? []) {
      const removal = await fetch("/api/auth/passkeys/remove", {
        method: "POST",
        credentials: "same-origin",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": csrf.token,
        },
        body: JSON.stringify({ credentialId: passkey.credentialId }),
      });
      if (!removal.ok) {
        throw new Error(
          `Passkey-Entfernung fehlgeschlagen: ${removal.status} ${await removal.text()}`,
        );
      }
    }
    return passkeys?.length ?? 0;
  });
  if (cleanup > 0) {
    console.log(`Vor der Registrierung ${cleanup} alte Passkey(s) entfernt.`);
  }
  // The passkey list loaded before the removals, so refresh to see the
  // cleaned-up state.
  await page.reload();
  await expect(page.getByText("Willkommen im Archiv.")).toBeVisible();
  await expect(page.getByText("Noch kein Passkey registriert.")).toBeVisible();

  // Enroll a passkey for later username-less login.
  await page.getByLabel("Name für den neuen Passkey").fill("Test-Autenticator");
  await page.getByRole("button", { name: "Passkey hinzufügen" }).click();
  await expect(page.getByText("Passkey registriert.")).toBeVisible();
  await expect(page.getByText("Test-Autenticator")).toBeVisible();

  await page
    .getByRole("main")
    .getByRole("button", { name: "Abmelden" })
    .click();
  await expect(page).toHaveURL(/\/anmelden\//);

  // Username-less login with the enrolled passkey.
  await page.getByRole("button", { name: "Mit Passkey anmelden" }).click();
  await expect(page).toHaveURL(/\/archiv\//);
  await expect(page.getByText("Willkommen im Archiv.")).toBeVisible();

  expect(errors).toEqual([]);
});
