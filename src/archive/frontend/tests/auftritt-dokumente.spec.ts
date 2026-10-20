import { expect, type Page, test } from "@playwright/test";
import { mockLeereAufnahmen } from "./aufnahmen-mock";

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

const eventId = "00000000-0000-0000-0000-00000000e010";
const fotoId = "00000000-0000-0000-0000-00000000e011";
const dokumentAssetId = "00000000-0000-0000-0000-00000000e012";
const fotoAssetId = "00000000-0000-0000-0000-00000000e017";

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
    dateiId: "00000000-0000-0000-0000-00000000f00a",
    contentType: "image/jpeg",
    sizeBytes: 1258291,
    createdAt: "2026-09-24T10:00:00.000Z",
    ...overwrite,
  };
}

function zugriff(dateiId: string, overwrite: Record<string, unknown> = {}) {
  return {
    dateiId,
    kind: "sonstiges",
    contentType: "image/jpeg",
    sizeBytes: 1258291,
    fileName: "datei.jpg",
    createdAt: "2026-09-24T10:00:00.000Z",
    viewUrl: `https://speicher.test/ansicht?sig=${dateiId}`,
    downloadUrl: `https://speicher.test/laden?sig=${dateiId}`,
    expiresAt: "2026-09-24T10:15:00.000Z",
    ...overwrite,
  };
}

/** Dokument oder Foto des Auftritts, wie es die Auftrittsdetails liefern. */
function materialEintrag(
  dateiId: string,
  art: string,
  description: string | null,
  datei: Record<string, unknown>,
) {
  return {
    id: dateiId,
    art,
    description,
    fileName: art === "photo" ? "bild.jpg" : "dokument.pdf",
    contentType: datei.contentType,
    sizeBytes: datei.sizeBytes,
    createdAt: "2026-09-20T10:00:00.000Z",
    rowVersion: 1,
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
      notes: null,
      sourceNote: "Programmheft im Vereinsarchiv, Blatt 3.",
      documents: dokumente,
      createdAt: "2026-09-01T08:00:00.000Z",
      updatedAt: "2026-09-24T10:00:00.000Z",
    },
  };
}

async function mockSitzung(page: Page, me: unknown) {
  await mockLeereAufnahmen(page);
  // #169: PDF-Dokumente fragen ihren Auswertungsstand ab (hier: nichts bekannt).
  await page.route("**/api/dateien/extraction*", (route) =>
    route.fulfill(json({ results: [] })),
  );
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
              dateiId: "00000000-0000-0000-0000-00000000f00b",
              contentType: "image/png",
              sizeBytes: 20480,
            }),
          ),
          materialEintrag(
            dokumentAssetId,
            "document",
            "Programmheft des Frühlingskonzerts",
            revision({
              dateiId: "00000000-0000-0000-0000-00000000f00c",
              contentType: "application/pdf",
              sizeBytes: 419430,
            }),
          ),
        ]),
      ),
    ),
  );
  await page.route("**/api/dateien/*/access", (route) => {
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

  // Der Leerzustand erscheint nicht neben vorhandenem Material.
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
            revision({ dateiId: "00000000-0000-0000-0000-00000000f00b" }),
          ),
          materialEintrag(
            dokumentAssetId,
            "document",
            "Programmheft des Frühlingskonzerts",
            revision({
              dateiId: "00000000-0000-0000-0000-00000000f00c",
              contentType: "application/pdf",
              sizeBytes: 419430,
            }),
          ),
        ]),
      ),
    ),
  );
  await page.route("**/api/dateien/*/access", (route) => {
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
  await page.route(`**/api/dateien/${gestoertId}/access`, (route) =>
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
  await page.route("**/api/dateien/*/access", (route) => {
    zugriffe += 1;
    return route.fulfill(json({}));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toBeVisible();
  await expect(page.locator("img")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Öffnen" })).toHaveCount(0);
  expect(zugriffe).toBe(0);

  expect(errors).toEqual([]);
});

test("Redaktion überträgt Dokument und Fotografie; eine abgelehnte Datei wird erneut versucht", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const dokumente: Array<Record<string, unknown>> = [];
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(dokumente))),
  );
  await page.route("**/api/dateien/*/access", (route) => {
    const dateiId = pfadTeil(route.request().url(), 3);
    return route.fulfill(json(zugriff(dateiId)));
  });

  const sitzungen: Record<string, unknown>[] = [];
  await page.route(
    `**/api/events/${eventId}/dateien/upload-session`,
    (route) => {
      expect(route.request().method()).toBe("POST");
      expect(route.request().headers()["x-csrf-token"]).toBe("test");
      const anfrage = route.request().postDataJSON() as Record<string, unknown>;
      sitzungen.push(anfrage);
      const kennung = String(anfrage.fileName).replace(/\W/g, "");
      return route.fulfill(
        json(
          {
            uploadSessionId: `sitz-${kennung}-${sitzungen.length}`,
            blobName: null,
            uploadUrl: `https://speicher.test/ubertragung?sig=${kennung}`,
            expiresAt: "2026-09-24T12:15:00.000Z",
            maxBytes: 5242880,
            blockBytes: 5242880,
          },
          201,
        ),
      );
    },
  );
  const inhaltstypen: string[] = [];
  await page.route(/speicher\.test\/ubertragung/, (route) => {
    if (!route.request().url().includes("comp=blocklist")) {
      inhaltstypen.push(route.request().headers()["content-type"] ?? "");
    }
    return route.fulfill(json({}, 201));
  });
  const abschluesse: Record<string, unknown>[] = [];
  let fotoVersuche = 0;
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    const sitzungsId = pfadTeil(route.request().url(), 3);
    const istFoto = sitzungsId.startsWith("sitz-chorpng");
    abschluesse.push(route.request().postDataJSON() as Record<string, unknown>);
    // Die Fotografie scheitert beim ersten Abschluss; der Versuch wiederholt
    // mit einer frischen Sitzung.
    if (istFoto && fotoVersuche === 0) {
      fotoVersuche += 1;
      return route.fulfill(
        problem("Die Datei passt nicht zu ihrem Dateityp.", 422),
      );
    }
    const dateiId = istFoto ? fotoAssetId : dokumentAssetId;
    const contentType = istFoto ? "image/png" : "application/pdf";
    dokumente.push(
      materialEintrag(
        dateiId,
        istFoto ? "photo" : "document",
        istFoto ? "Chor am Stadtsaal" : null,
        revision({ contentType }),
      ),
    );
    return route.fulfill(
      json({
        datei: {
          id: dateiId,
          kind: "sonstiges",
          contentType,
          sizeBytes: 419430,
          fileName: istFoto ? "chor.png" : "programm.pdf",
          createdAt: "2026-09-24T12:00:00.000Z",
        },
      }),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await dokumenteVerwaltungAufklappen(page);
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
  const zeileChor = page
    .locator(".material-datei")
    .filter({ hasText: "chor.png" });
  await zeileChor
    .getByLabel("Beschreibung (wird als Text zum Bild vorgelesen)")
    .fill("Chor am Stadtsaal");

  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("1 von 2 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByText("Die Datei passt nicht zu ihrem Dateityp."),
  ).toBeVisible();
  await expect(zeileChor.getByText("gescheitert")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toHaveCount(1);

  expect(sitzungen).toHaveLength(2);
  expect(sitzungen).toContainEqual({
    kind: "sonstiges",
    contentType: "application/pdf",
    description: null,
    fileName: "programm.pdf",
    sizeBytes: 17,
  });
  expect(sitzungen).toContainEqual({
    kind: "sonstiges",
    contentType: "image/png",
    description: "Chor am Stadtsaal",
    fileName: "chor.png",
    sizeBytes: 8,
  });
  expect([...inhaltstypen].sort()).toEqual(["application/pdf", "image/png"]);
  expect(abschluesse).toContainEqual({ fileName: "chor.png", sizeBytes: 8 });

  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(page.getByText("2 von 2 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toHaveCount(0);
  expect(sitzungen).toHaveLength(3);

  // Nach der Aktualisierung liest sich das fertige Material wie beim Mitglied.
  await expect(page.locator("img")).toHaveCount(1);
  await expect(page.getByText("Chor am Stadtsaal")).toBeVisible();
  await expect(page.getByRole("link", { name: "Öffnen" })).toBeVisible();
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Redaktion bessert die Beschreibung nach, ersetzt und löscht; Mitglieder sehen keine Werkzeuge", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  const dokumente: Array<Record<string, unknown>> = [
    materialEintrag(
      dokumentAssetId,
      "document",
      "Programmheft",
      revision({ contentType: "application/pdf", sizeBytes: 419430 }),
    ),
  ];
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(dokumente))),
  );
  await page.route("**/api/dateien/*/access", (route) =>
    route.fulfill(json(zugriff(pfadTeil(route.request().url(), 3)))),
  );
  const aenderungen: Record<string, unknown>[] = [];
  await page.route(`**/api/dateien/${dokumentAssetId}`, (route) => {
    expect(route.request().method()).toBe("PATCH");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    aenderungen.push(anfrage);
    if (anfrage.description === "ablehnen") {
      return route.fulfill(problem("Die Beschreibung ist zu lang.", 400));
    }
    dokumente[0] = { ...dokumente[0], description: anfrage.description };
    return route.fulfill(json({ datei: dokumente[0] }));
  });
  const ersetzSitzungen: Record<string, unknown>[] = [];
  await page.route(
    `**/api/events/${eventId}/dateien/upload-session`,
    (route) => {
      ersetzSitzungen.push(
        route.request().postDataJSON() as Record<string, unknown>,
      );
      return route.fulfill(
        json(
          {
            uploadSessionId: "sitz-ersatz",
            blobName: null,
            uploadUrl: "https://speicher.test/ubertragung?sig=ersatz",
            expiresAt: "2026-09-24T12:15:00.000Z",
            maxBytes: 5242880,
            blockBytes: 5242880,
          },
          201,
        ),
      );
    },
  );
  await page.route(/speicher\.test\/ubertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route("**/api/upload-sessions/sitz-ersatz/finalize", (route) => {
    dokumente[0] = {
      ...materialEintrag(
        "00000000-0000-0000-0000-00000000e0aa",
        "document",
        "Programmheft",
        revision({ contentType: "application/pdf", sizeBytes: 100 }),
      ),
    };
    return route.fulfill(
      json({
        datei: {
          id: "00000000-0000-0000-0000-00000000e0aa",
          kind: "sonstiges",
          contentType: "application/pdf",
          sizeBytes: 100,
          fileName: "neu.pdf",
          createdAt: "2026-09-24T12:00:00.000Z",
        },
      }),
    );
  });
  const loeschungen: Record<string, unknown>[] = [];
  await page.route("**/api/dateien/*/delete", (route) => {
    loeschungen.push({
      id: pfadTeil(route.request().url(), 3),
      ...(route.request().postDataJSON() as Record<string, unknown>),
    });
    dokumente.length = 0;
    return route.fulfill({ status: 204 });
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const eintrag = page.getByRole("article", { name: "Programmheft" });
  await eintrag
    .getByRole("button", { name: "Beschreibung bearbeiten" })
    .click();
  const feld = eintrag.getByLabel(
    "Beschreibung (wird als Text zum Bild vorgelesen)",
  );
  await feld.fill("ablehnen");
  await eintrag.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Die Beschreibung ist zu lang.")).toBeVisible();
  await feld.fill("Programmheft 1950");
  await eintrag.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();
  expect(aenderungen).toEqual([
    { description: "ablehnen" },
    { description: "Programmheft 1950" },
  ]);

  // Ersetzen: eine neue Sitzung nennt die alte Datei, die Liste lädt neu.
  const neuer = page.getByRole("article", { name: "Programmheft 1950" });
  await neuer
    .getByLabel("Neue Datei für Programmheft 1950")
    .setInputFiles(dateiPfad("neu.pdf", "application/pdf", "%PDF-1.4 neu"));
  await expect(
    page.getByRole("article", { name: "Programmheft" }),
  ).toBeVisible();
  expect(ersetzSitzungen).toEqual([
    {
      kind: "sonstiges",
      contentType: "application/pdf",
      description: null,
      replacesDateiId: dokumentAssetId,
      fileName: "neu.pdf",
      sizeBytes: 12,
    },
  ]);

  // Löschen verlangt eine Bestätigung und legt die Datei in den Papierkorb.
  const ersetzt = page.getByRole("article", { name: "Programmheft" });
  await ersetzt.getByRole("button", { name: "Löschen · Programmheft" }).click();
  await ersetzt.getByRole("button", { name: "Ja, löschen" }).click();
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Dokumente hinterlegt."),
  ).toBeVisible();
  expect(loeschungen).toEqual([
    { id: "00000000-0000-0000-0000-00000000e0aa", rowVersion: 1 },
  ]);

  expect(errors).toEqual([]);
});

test("Mitglieder sehen weder Werkbank noch Redaktionswerkzeuge", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json(
        detail([
          materialEintrag(
            dokumentAssetId,
            "document",
            "Programmheft",
            revision({ contentType: "application/pdf", sizeBytes: 419430 }),
          ),
        ]),
      ),
    ),
  );
  await page.route("**/api/dateien/*/access", (route) =>
    route.fulfill(json(zugriff(pfadTeil(route.request().url(), 3)))),
  );
  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(page.getByRole("link", { name: "Öffnen" })).toBeVisible();
  await expect(page.locator("details.material-verwaltung")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Beschreibung bearbeiten" }),
  ).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Löschen ·/ })).toHaveCount(0);
});
