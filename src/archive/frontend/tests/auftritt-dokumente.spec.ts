import { expect, type Page, test } from "@playwright/test";

const editorMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000001",
  email: "redaktion@liedertafel.test",
  displayName: "Redaktion",
  roles: ["Editor"],
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

const editorTwo = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000003",
  email: "zweitredaktion@liedertafel.test",
  displayName: "Zweitredaktion",
  roles: ["Editor"],
  verifiedAt: new Date().toISOString(),
};

const eventId = "00000000-0000-0000-0000-00000000e010";
const fotoId = "00000000-0000-0000-0000-00000000e011";
const dokumentAssetId = "00000000-0000-0000-0000-00000000e012";
const fotoAssetId = "00000000-0000-0000-0000-00000000e017";
const unausgeliefertId = "00000000-0000-0000-0000-00000000e013";
const konfliktId = "00000000-0000-0000-0000-00000000e014";

function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  };
}

function problem(title: string, status: number) {
  return {
    status,
    contentType: "application/problem+json",
    body: JSON.stringify({ title }),
  };
}

function revision(overwrite: Record<string, unknown> = {}) {
  return {
    revisionId: "00000000-0000-0000-0000-00000000f00a",
    revisionNumber: 1,
    contentType: "image/jpeg",
    sizeBytes: 1258291,
    createdAt: "2026-09-24T10:00:00.000Z",
    ...overwrite,
  };
}

function zugriff(assetId: string, overwrite: Record<string, unknown> = {}) {
  return {
    assetId,
    revisionId: "00000000-0000-0000-0000-00000000f00a",
    revisionNumber: 1,
    contentType: "image/jpeg",
    sizeBytes: 1258291,
    createdAt: "2026-09-24T10:00:00.000Z",
    viewUrl: `https://speicher.test/ansicht?sig=${assetId}`,
    downloadUrl: `https://speicher.test/laden?sig=${assetId}`,
    expiresAt: "2026-09-24T10:15:00.000Z",
    ...overwrite,
  };
}

function materialEintrag(
  assetId: string,
  assetType: string,
  description: string | null,
  revision: Record<string, unknown> | null,
) {
  return {
    id: assetId,
    assetType,
    description,
    createdAt: "2026-09-20T10:00:00.000Z",
    currentRevision: revision,
  };
}

function detail(dokumente: unknown[] = []) {
  return {
    event: {
      id: eventId,
      kind: "concert",
      title: "Frühlingskonzert",
      venue: "Stadtsaal Mining",
      dateYear: 1950,
      dateMonth: 5,
      dateDay: 12,
      dateApproximate: false,
      datePrecision: "day",
      dateDisplay: "12. Mai 1950",
      startTime: "19:30",
      published: true,
      notes: null,
      sourceNote: "Programmheft im Vereinsarchiv, Blatt 3.",
      documents: dokumente,
      createdAt: "2026-09-01T08:00:00.000Z",
      updatedAt: "2026-09-24T10:00:00.000Z",
      publishedAt: "2026-09-20T10:00:00.000Z",
    },
  };
}

async function mockSitzung(page: Page, me: unknown) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

// Öffnet den Werkbank-Aufklapper (er ist zu, bis die Redaktion ihn braucht).
async function dokumenteVerwaltungAufklappen(page: Page) {
  await page.locator("details.material-verwaltung > summary").click();
  await expect(page.locator("details.material-verwaltung")).toHaveAttribute(
    "open",
    "",
  );
}

function pfadTeil(url: string, index: number): string {
  return new URL(url).pathname.split("/")[index] ?? "";
}

function dateiPfad(name: string, mimeType: string, inhalt: string) {
  return { name, mimeType, buffer: Buffer.from(inhalt) };
}

test("Mitglied liest Fotografien und Dokumente mit Textbeschriftung", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json(
        detail([
          materialEintrag(
            fotoId,
            "photo",
            "Chor auf der Bühne des Stadtsaals, Mai 1950",
            revision({ contentType: "image/jpeg", sizeBytes: 1258291 }),
          ),
          materialEintrag(
            "00000000-0000-0000-0000-00000000e015",
            "photo",
            null,
            revision({
              revisionId: "00000000-0000-0000-0000-00000000f00b",
              contentType: "image/png",
              sizeBytes: 20480,
            }),
          ),
          materialEintrag(
            dokumentAssetId,
            "document",
            "Programmheft des Frühlingskonzerts",
            revision({
              revisionId: "00000000-0000-0000-0000-00000000f00c",
              contentType: "application/pdf",
              sizeBytes: 419430,
            }),
          ),
        ]),
      ),
    ),
  );
  await page.route("**/api/assets/*/access", (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    return route.fulfill(json(zugriff(assetId)));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  // Fotografien mit sichtbarer Beschriftung und echtem Vorlesetext.
  const bilder = page.locator("img");
  await expect(bilder).toHaveCount(2);
  await expect(bilder.first()).toHaveAttribute(
    "alt",
    "Chor auf der Bühne des Stadtsaals, Mai 1950",
  );
  await expect(bilder.first()).toHaveAttribute(
    "src",
    `https://speicher.test/ansicht?sig=${fotoId}`,
  );
  await expect(
    page.getByText("Chor auf der Bühne des Stadtsaals, Mai 1950"),
  ).toBeVisible();
  const ohneBeschreibung = page
    .locator("figure")
    .filter({ has: page.locator('img[alt="Fotografie zum Auftritt"]') });
  await expect(ohneBeschreibung.locator("img")).toHaveAttribute(
    "alt",
    "Fotografie zum Auftritt",
  );
  await expect(
    ohneBeschreibung.locator(".dokument-bildunterschrift"),
  ).toHaveText("Fotografie");

  // Dokumente: beschriftete Einträge mit den Ticket-Aktionen.
  await expect(
    page.getByText("Programmheft des Frühlingskonzerts"),
  ).toBeVisible();
  await expect(page.getByText("PDF · 0,4 MB")).toBeVisible();
  const oeffnen = page.getByRole("link", { name: "Öffnen" });
  await expect(oeffnen).toBeVisible();
  await expect(oeffnen).toHaveAttribute("target", "_blank");
  await expect(oeffnen).toHaveAttribute("rel", "noreferrer");
  await expect(oeffnen).toHaveAttribute(
    "href",
    `https://speicher.test/ansicht?sig=${dokumentAssetId}`,
  );
  const herunterladen = page.getByRole("link", { name: "Herunterladen" });
  await expect(herunterladen).toBeVisible();
  await expect(herunterladen).toHaveAttribute(
    "href",
    `https://speicher.test/laden?sig=${dokumentAssetId}`,
  );

  // Der unausgelieferte Eintrag bleibt der Redaktion vorbehalten; der
  // Leerzustand erscheint nicht neben vorhandenem Material.
  await expect(page.getByText("Noch nicht hochgeladen")).toHaveCount(0);
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toHaveCount(0);

  // Auch im Handymaß bleibt der Abschnitt ohne Seitenüberlauf, und die
  // Bilder halten sich an ihre Spalte.
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  const bildBox = await bilder.first().boundingBox();
  expect(bildBox).not.toBeNull();
  expect(bildBox?.width ?? 0).toBeLessThanOrEqual(
    (page.viewportSize()?.width ?? 0) + 1,
  );

  expect(errors).toEqual([]);
});

test("Fehlende Tickets erklären sich pro Material und bieten den erneuten Versuch an", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const gestoertId = "00000000-0000-0000-0000-00000000e016";
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json(
        detail([
          materialEintrag(
            fotoId,
            "photo",
            "Chor auf der Bühne des Stadtsaals",
            revision(),
          ),
          materialEintrag(
            gestoertId,
            "photo",
            "Gruppenbild auf dem Festplatz",
            revision({ revisionId: "00000000-0000-0000-0000-00000000f00b" }),
          ),
          materialEintrag(
            dokumentAssetId,
            "document",
            "Programmheft des Frühlingskonzerts",
            revision({
              revisionId: "00000000-0000-0000-0000-00000000f00c",
              contentType: "application/pdf",
              sizeBytes: 419430,
            }),
          ),
        ]),
      ),
    ),
  );
  await page.route("**/api/assets/*/access", (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    if (assetId === fotoId) {
      return route.fulfill(problem("Material nicht gefunden.", 404));
    }
    if (assetId === dokumentAssetId) {
      return route.fulfill(problem("Anmeldung erforderlich.", 401));
    }
    return route.fulfill(problem("Speicherdienst nicht erreichbar.", 502));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Das Material ist nicht mehr verfügbar."),
  ).toBeVisible();
  await expect(
    page.getByText("Die Anmeldung ist abgelaufen. Bitte lade die Seite neu."),
  ).toBeVisible();
  const gestoert = page
    .locator("figure")
    .filter({ hasText: "Gruppenbild auf dem Festplatz" });
  await expect(
    gestoert.getByText("Das Material konnte nicht geladen werden."),
  ).toBeVisible();
  await expect(
    gestoert.getByRole("button", { name: "Erneut versuchen" }),
  ).toBeVisible();

  // Der gestörte Abruf bleibt registriert; der spätere Erfolg tritt an
  // seine Stelle (die zuletzt angemeldete Route gewinnt).
  await page.route(`**/api/assets/${gestoertId}/access`, (route) =>
    route.fulfill(json(zugriff(gestoertId))),
  );
  await gestoert.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(gestoert.locator("img")).toHaveAttribute(
    "alt",
    "Gruppenbild auf dem Festplatz",
  );

  expect(errors).toEqual([]);
});

test("Ohne hinterlegtes Material bleibt der Abschnitt ehrlich leer", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  let zugriffe = 0;
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail())),
  );
  await page.route("**/api/assets/*/access", (route) => {
    zugriffe += 1;
    return route.fulfill(json({}));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toBeVisible();
  await expect(page.locator("img")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Öffnen" })).toHaveCount(0);
  await expect(page.getByText("Noch nicht hochgeladen")).toHaveCount(0);
  expect(zugriffe).toBe(0);

  expect(errors).toEqual([]);
});

test("Redaktion hängt Dokument und Fotografie an, überträgt sie und sieht das fertige Material", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const dokumente: Array<Record<string, unknown>> = [];
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(dokumente))),
  );
  await page.route("**/api/assets/*/access", (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    return route.fulfill(json(zugriff(assetId)));
  });

  const antraege: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/assets`, (route) => {
    antraege.push(route.request().postDataJSON() as Record<string, unknown>);
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as {
      assetType?: string;
      description?: string;
    };
    const assetId =
      anfrage.assetType === "photo" ? fotoAssetId : dokumentAssetId;
    dokumente.push(
      materialEintrag(
        assetId,
        anfrage.assetType ?? "document",
        anfrage.description ?? null,
        null,
      ),
    );
    return route.fulfill(
      json(
        {
          id: assetId,
          eventId,
          musicalVersionId: null,
          voiceLabel: null,
          assetType: anfrage.assetType ?? "document",
          description: anfrage.description ?? null,
          createdAt: "2026-09-24T12:00:00.000Z",
          currentRevision: null,
        },
        201,
      ),
    );
  });
  await page.route(/\/api\/assets\/[^/]+\/upload-session$/, (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    return route.fulfill(
      json(
        {
          uploadSessionId: `sitz-${assetId}`,
          blobName: null,
          uploadUrl: `https://speicher.test/ubertragung?sig=${assetId}`,
          expiresAt: "2026-09-24T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    );
  });
  const inhaltstypen: string[] = [];
  await page.route(/speicher\.test\/ubertragung/, (route) => {
    if (!route.request().url().includes("comp=blocklist")) {
      inhaltstypen.push(route.request().headers()["content-type"] ?? "");
    }
    return route.fulfill(json({}, 201));
  });
  const abschluesse: Record<string, unknown>[] = [];
  const gescheiterteVersuche = new Set<string>();
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    const sitzungsId = pfadTeil(route.request().url(), 3);
    const assetId = sitzungsId.replace("sitz-", "");
    abschluesse.push(route.request().postDataJSON() as Record<string, unknown>);
    // Die Fotografie scheitert beim ersten Abschluss, bleibt aber
    // wiederholbar (Sitzung weiterverwendbar).
    if (assetId === fotoAssetId && !gescheiterteVersuche.has(assetId)) {
      gescheiterteVersuche.add(assetId);
      return route.fulfill(problem("Die Datei ist kein gültiges Bild.", 422));
    }
    const istFoto = assetId === fotoAssetId;
    const contentType = istFoto ? "image/png" : "application/pdf";
    const stelle = dokumente.findIndex((eintrag) => eintrag.id === assetId);
    if (stelle >= 0) {
      dokumente[stelle] = materialEintrag(
        assetId,
        istFoto ? "photo" : "document",
        istFoto ? "Chor am Stadtsaal" : null,
        revision({ revisionId: `rev-${assetId}`, contentType }),
      );
    }
    return route.fulfill(
      json({
        assetId,
        revisionId: `rev-${assetId}`,
        revisionNumber: 1,
        contentType,
        sizeBytes: 419430,
        createdAt: "2026-09-24T12:00:00.000Z",
      }),
    );
  });
  const patchAntraege: Record<string, unknown>[] = [];
  await page.route(/\/api\/assets\/[^/]+$/, (route) => {
    expect(route.request().method()).toBe("PATCH");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const assetId = pfadTeil(route.request().url(), 3);
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    patchAntraege.push(anfrage);
    const stelle = dokumente.findIndex((eintrag) => eintrag.id === assetId);
    if (stelle >= 0) {
      dokumente[stelle] = {
        ...dokumente[stelle],
        ...anfrage,
      };
    }
    return route.fulfill(
      json({
        id: assetId,
        eventId,
        musicalVersionId: null,
        voiceLabel: null,
        ...(stelle >= 0 ? dokumente[stelle] : { currentRevision: null }),
      }),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await dokumenteVerwaltungAufklappen(page);
  await expect(
    page.getByRole("button", { name: "Dokument/Fotografie hinzufügen" }),
  ).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await page
    .getByRole("button", { name: "Dokument/Fotografie hinzufügen" })
    .click();
  const chooser = await wahl;
  await chooser.setFiles([
    dateiPfad("programm.pdf", "application/pdf", "%PDF-1.4 programm"),
    dateiPfad("chor.png", "image/png", "png-bild"),
  ]);
  await expect(page.locator(".material-datei")).toHaveCount(2);
  const zeileProgramm = page
    .locator(".material-datei")
    .filter({ hasText: "programm.pdf" });
  await expect(zeileProgramm.locator("select")).toHaveValue("document");
  const zeileChor = page
    .locator(".material-datei")
    .filter({ hasText: "chor.png" });
  await expect(zeileChor.locator("select")).toHaveValue("photo");
  await zeileChor
    .getByLabel("Beschreibung (wird als Text zum Bild vorgelesen)")
    .fill("Chor am Stadtsaal");

  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("1 von 2 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByText("Die Datei ist kein gültiges Bild."),
  ).toBeVisible();
  await expect(zeileChor.getByText("gescheitert")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toHaveCount(1);

  expect(antraege).toHaveLength(2);
  expect(antraege).toContainEqual({ assetType: "document" });
  expect(antraege).toContainEqual({
    assetType: "photo",
    description: "Chor am Stadtsaal",
  });
  expect([...inhaltstypen].sort()).toEqual(["application/pdf", "image/png"]);
  expect(abschluesse).toContainEqual({ fileName: "chor.png", sizeBytes: 8 });

  // Der erneute Versuch nutzt das bestehende Material: kein zweiter
  // Anlage-Aufruf, nur eine neue Übertragungssitzung.
  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(page.getByText("2 von 2 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toHaveCount(0);
  expect(antraege).toHaveLength(2);

  // Nach der Aktualisierung liest sich das fertige Material wie beim
  // Mitglied; der unausgelieferte Eintrag ist verschwunden.
  await expect(page.locator("img")).toHaveCount(1);
  await expect(page.getByText("Chor am Stadtsaal")).toBeVisible();
  await expect(page.getByRole("link", { name: "Öffnen" })).toBeVisible();
  await expect(page.getByText("Noch nicht hochgeladen")).toHaveCount(0);
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toHaveCount(0);

  expect(patchAntraege.length).toBeGreaterThanOrEqual(1);

  expect(errors).toEqual([]);
});

test("Redaktion bessert Beschreibung und Materialart nach; abgelehnte Änderungen erklären sich", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json(
        detail([
          materialEintrag(fotoId, "photo", "Chor am Stadtsaal", revision()),
          materialEintrag(unausgeliefertId, "document", null, null),
          materialEintrag(
            konfliktId,
            "photo",
            "Gruppenbild auf dem Festplatz",
            null,
          ),
        ]),
      ),
    ),
  );
  const patchAntraege: Record<string, unknown>[] = [];
  await page.route(/\/api\/assets\/[^/]+$/, (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    patchAntraege.push({ assetId, ...anfrage });
    if (assetId === konfliktId) {
      // Ein anderer Beitrag hat das Material zwischenzeitlich geändert:
      // der Server lehnt den veralteten Stand mit 409 ab.
      return route.fulfill(
        problem("Der Eintrag wurde zwischenzeitlich geändert.", 409),
      );
    }
    const antwort: Record<string, unknown> = {
      id: assetId,
      eventId,
      musicalVersionId: null,
      assetType: (anfrage.assetType as string) ?? "document",
      voiceLabel: null,
      description: (anfrage.description as string | null) ?? null,
      createdAt: "2026-09-20T10:05:00.000Z",
      currentRevision: null,
    };
    if (assetId === fotoId) {
      antwort.currentRevision = revision();
      antwort.assetType = "photo";
    }
    return route.fulfill(json(antwort));
  });
  await page.route("**/api/assets/*/access", (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    return route.fulfill(json(zugriff(assetId)));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await dokumenteVerwaltungAufklappen(page);
  await expect(page.getByText("Noch nicht hochgeladen")).toBeVisible();

  // Unausgelieferter Eintrag: Beschreibung und Materialart gehen als
  // Änderungsvorschlag in denselben PATCH.
  const zeileProgramm = page
    .locator(".material-fortsetzung")
    .filter({ hasText: "Dokument" });
  await zeileProgramm.getByRole("button", { name: "Bearbeiten" }).click();
  await zeileProgramm
    .getByLabel("Beschreibung (wird als Text zum Bild vorgelesen)")
    .fill("Programmheft des Frühlingskonzerts");
  await zeileProgramm.locator("select").selectOption({ label: "Fotografie" });
  await zeileProgramm
    .getByRole("button", { name: "Änderungen speichern" })
    .click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();
  expect(patchAntraege[0]).toEqual({
    assetId: unausgeliefertId,
    assetType: "photo",
    description: "Programmheft des Frühlingskonzerts",
  });

  // Fertige Fotografie: Beschreibung korrigierbar, Typ bleibt gesperrt.
  // Die Redaktionswerkzeuge laufen neben der Figur, daher zählt die Zeile.
  const fotoEintrag = page
    .locator(".dokumente-fotos li")
    .filter({ hasText: "Chor am Stadtsaal" });
  await fotoEintrag
    .getByRole("button", { name: "Beschreibung bearbeiten" })
    .click();
  await expect(fotoEintrag.getByText("Materialtyp: Fotografie")).toBeVisible();
  await fotoEintrag
    .getByLabel("Beschreibung (wird als Text zum Bild vorgelesen)")
    .fill("Chor auf der Bühne des Stadtsaals, Mai 1950");
  await fotoEintrag
    .getByRole("button", { name: "Änderungen speichern" })
    .click();
  await expect(fotoEintrag.locator("select")).toHaveCount(0);
  expect(patchAntraege[1]).toEqual({
    assetId: fotoId,
    description: "Chor auf der Bühne des Stadtsaals, Mai 1950",
  });

  // Die abgelehnte Änderung erklärt sich mit der Vertragssprache.
  const zeileGesamt = page
    .locator(".material-fortsetzung")
    .filter({ hasText: "Gruppenbild auf dem Festplatz" });
  await zeileGesamt.getByRole("button", { name: "Bearbeiten" }).click();
  await zeileGesamt.locator("select").selectOption({ label: "Dokument (PDF)" });
  await zeileGesamt
    .getByRole("button", { name: "Änderungen speichern" })
    .click();
  await expect(
    page.getByText("Der Eintrag wurde zwischenzeitlich geändert."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Zweitredaktion scheitert am fremden Material und liest die Ablehnung in der Zeile", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  // Der unausgelieferte Eintrag wurde von der ersten Redaktion angelegt
  // (accountId …0001); die angemeldete Zweitredaktion (…0003) darf das
  // fremde Material weder ändern noch hochladen — der Server lehnt den
  // PATCH mit 403 ab (Vertrag: AssetOwnerMessage).
  await mockSitzung(page, editorTwo);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json(detail([materialEintrag(unausgeliefertId, "document", null, null)])),
    ),
  );
  const antraege: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/assets`, (route) => {
    antraege.push(route.request().postDataJSON() as Record<string, unknown>);
    return route.fulfill(json({}, 500));
  });
  await page.route(/\/api\/assets\/[^/]+$/, (route) => {
    expect(route.request().method()).toBe("PATCH");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    return route.fulfill(
      problem("Nur die anlegendende Person kann das Material bearbeiten.", 403),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await dokumenteVerwaltungAufklappen(page);
  await expect(page.getByText("Noch nicht hochgeladen")).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Hochladen" }).click();
  await (await wahl).setFiles([
    dateiPfad("programm.pdf", "application/pdf", "%PDF-1.4 programm"),
  ]);

  // Die Ablehnung erklärt sich in der gescheiterten Zeile; aus dem
  // Fehlschlag entsteht kein zweiter wartender Eintrag und keine
  // Ersatzanfrage an die Materialanlage.
  const zeileFremd = page
    .locator(".material-datei")
    .filter({ hasText: "programm.pdf" });
  await expect(zeileFremd).toHaveCount(1);
  await expect(zeileFremd).toHaveAttribute("data-status", "gescheitert");
  await expect(zeileFremd.locator("output.feld-fehler")).toHaveText(
    "Nur die anlegendende Person kann das Material bearbeiten.",
  );
  expect(antraege).toHaveLength(0);
  await expect(page.locator(".material-fortsetzung")).toHaveCount(0);

  expect(errors).toEqual([]);
});
