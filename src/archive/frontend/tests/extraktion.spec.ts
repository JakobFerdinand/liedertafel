import { expect, type Page, test } from "@playwright/test";
import { mockLeereAufnahmen } from "./aufnahmen-mock";

// ARC-034: Auswertungsstand der PDF-Textauswertung in den Material-Einträgen
// (Noten und Auftrittsdokumente): ehrliche Zustände, Textvorschau für
// gelesenen Text, erneuter Versuch für gescheiterte Läufe, Nachfragen nur
// solange die Auswertung wirklich unterwegs ist, und nichts für Mitglieder.

const editorMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000001",
  email: "redaktion@liedertafel.test",
  displayName: "Redaktion",
  roles: ["Editor"],
  verifiedAt: new Date().toISOString(),
};

// Auftritts-Oberfläche (Auftritt mit Dokument-PDF).
const eventId = "00000000-0000-0000-0000-00000000e010";
const dokumentAssetId = "00000000-0000-0000-0000-00000000e012";
const dokumentRevisionId = "00000000-0000-0000-0000-00000000f00c";

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

async function mockSitzung(page: Page, me: unknown) {
  await mockLeereAufnahmen(page);
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

function pfadTeil(url: string, index: number): string {
  return new URL(url).pathname.split("/")[index] ?? "";
}

function revision(revisionId: string) {
  return {
    revisionId,
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-18T10:00:00.000Z",
  };
}

function dokumentEintrag() {
  return {
    id: dokumentAssetId,
    assetType: "document",
    description: "Programmheft des Frühlingskonzerts",
    createdAt: "2026-09-20T10:00:00.000Z",
    currentRevision: revision(dokumentRevisionId),
  };
}

function auftrittDetail(dokumente: unknown[]) {
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

function dokumentZugriff(assetId: string) {
  return {
    assetId,
    revisionId: dokumentRevisionId,
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-24T10:00:00.000Z",
    viewUrl: `https://speicher.test/ansicht?sig=${assetId}`,
    downloadUrl: `https://speicher.test/laden?sig=${assetId}`,
    expiresAt: "2026-09-24T10:15:00.000Z",
  };
}

/** Auswertungszeile des Stapelabrufs (Vertrag StatusPayload). */
function auswertung(
  revisionId: string,
  assetId: string,
  overwrite: Record<string, unknown> = {},
) {
  return {
    revisionId,
    assetId,
    revisionNumber: 1,
    status: "completed",
    textSource: "embedded",
    text: "Erste Strophe: Das Wandern ist des Müllers Lust.",
    cleanText: null,
    facts: null,
    readingEnabled: false,
    reading: null,
    failureReason: null,
    attemptCount: 1,
    completedAt: "2026-09-24T11:00:00.000Z",
    lastAttemptAt: "2026-09-24T10:59:00.000Z",
    lastEnqueuedAt: "2026-09-24T10:58:00.000Z",
    updatedAt: "2026-09-24T11:00:00.000Z",
    rowVersion: 3,
    ...overwrite,
  };
}

test("Gescheiterte Auswertung zeigt den Grund und läuft nach dem erneuten Versuch weiter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(auftrittDetail([dokumentEintrag()]))),
  );
  await page.route("**/api/assets/*/access", (route) =>
    route.fulfill(json(dokumentZugriff(pfadTeil(route.request().url(), 3)))),
  );
  let stand = auswertung(dokumentRevisionId, dokumentAssetId, {
    status: "failed",
    text: null,
    failureReason:
      "Das Dokument ist verschlüsselt und kann nicht gelesen werden.",
    completedAt: null,
    attemptCount: 2,
  });
  let anfragen = 0;
  await page.route("**/api/revisions/extraction*", (route) => {
    anfragen += 1;
    return route.fulfill(json({ results: [stand] }));
  });
  const wiederholAntraege: unknown[] = [];
  await page.route("**/api/revisions/*/extraction/retry", (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    expect(route.request().postDataJSON()).toEqual({});
    wiederholAntraege.push(route.request().postDataJSON());
    stand = auswertung(dokumentRevisionId, dokumentAssetId, {
      status: "queued",
      text: null,
      failureReason: null,
      attemptCount: 0,
      completedAt: null,
      lastAttemptAt: null,
      rowVersion: 4,
    });
    return route.fulfill(json(stand));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toContainText(
    "Textauswertung gescheitert",
  );
  await expect(zeile.locator(".extraktion-grund")).toHaveText(
    " — Das Dokument ist verschlüsselt und kann nicht gelesen werden.",
  );

  await zeile
    .getByRole("button", {
      name: "Textauswertung für diese Datei erneut starten",
    })
    .click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung wartet …",
  );
  expect(wiederholAntraege).toHaveLength(1);

  // Nach dem Anstoßen fragt die Zeile weiter nach, bis sie fertig ist.
  stand = auswertung(dokumentRevisionId, dokumentAssetId);
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Text ausgelesen",
    { timeout: 10_000 },
  );
  expect(anfragen).toBeGreaterThanOrEqual(2);

  expect(errors).toEqual([]);
});

test("Laufende Auswertung erklärt den abgelehnten erneuten Versuch ehrlich", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(auftrittDetail([dokumentEintrag()]))),
  );
  await page.route("**/api/assets/*/access", (route) =>
    route.fulfill(json(dokumentZugriff(pfadTeil(route.request().url(), 3)))),
  );
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(
      json({
        results: [
          auswertung(dokumentRevisionId, dokumentAssetId, {
            status: "failed",
            text: null,
            failureReason: "Der Auswertungsdienst hat den Lauf abgebrochen.",
            completedAt: null,
          }),
        ],
      }),
    ),
  );
  const wiederholAntraege: unknown[] = [];
  await page.route("**/api/revisions/*/extraction/retry", (route) => {
    wiederholAntraege.push(route.request().postDataJSON());
    return route.fulfill(problem("Die Auswertung läuft bereits.", 409));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toContainText(
    "Textauswertung gescheitert",
  );
  await zeile
    .getByRole("button", {
      name: "Textauswertung für diese Datei erneut starten",
    })
    .click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung läuft …",
  );
  expect(wiederholAntraege).toHaveLength(1);

  expect(errors).toEqual([]);
});

test("Eingescanntes Dokument bleibt ehrlich ohne Text und ohne Knopf", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(auftrittDetail([dokumentEintrag()]))),
  );
  await page.route("**/api/assets/*/access", (route) =>
    route.fulfill(json(dokumentZugriff(pfadTeil(route.request().url(), 3)))),
  );
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(
      json({
        results: [
          auswertung(dokumentRevisionId, dokumentAssetId, {
            status: "noText",
            text: null,
          }),
        ],
      }),
    ),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Kein Text gefunden — vermutlich eingescannt.",
  );
  await expect(zeile.getByRole("button")).toHaveCount(0);
  await expect(zeile.locator(".extraktion-vorschau")).toHaveCount(0);

  expect(errors).toEqual([]);
});

// ARC-034-1: KI-Lesen der Seiten. Der Stand kommt mit der Auswertungszeile
// ("reading"), in der Form des Vertrags ReadingEndpoints.ReadingPayload.
