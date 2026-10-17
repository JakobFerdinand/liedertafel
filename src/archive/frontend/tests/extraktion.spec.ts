import { expect, type Page, test } from "@playwright/test";
import {
  datei,
  editorMe,
  json,
  liedDetail,
  memberMe,
  mockSitzung,
  mondId,
  problem,
} from "./lieder-mock";

// #169: Auswertungsstand der PDF-Textauswertung und des KI-Lesens an den
// Noten-Dateien der Lied-Seite: ehrliche Zustände, erneuter Versuch für
// gescheiterte Läufe, Nachfragen nur solange die Auswertung unterwegs ist,
// „Rest lesen“ für teilweise gelesene Scans und nichts für Mitglieder.

const dateiId = "00000000-0000-0000-0000-00000000b001";

const partitur = datei(dateiId, {
  isPartitur: true,
  stimme: null,
  fileName: "partitur.pdf",
});

/** Auswertungszeile des Stapelabrufs (Vertrag StatusPayload). */
function auswertung(overwrite: Record<string, unknown> = {}) {
  return {
    dateiId,
    status: "completed",
    textSource: "embedded",
    text: "Erste Strophe: Der Mond ist aufgegangen.",
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

function lesen(overwrite: Record<string, unknown> = {}) {
  return {
    status: "completed",
    pagesTotal: 6,
    pagesRead: 3,
    partial: true,
    fieldsApplied: 2,
    failureReason: null,
    attemptCount: 1,
    completedAt: "2026-09-24T11:00:00.000Z",
    updatedAt: "2026-09-24T11:00:00.000Z",
    rowVersion: 2,
    ...overwrite,
  };
}

async function liedSeite(page: Page, me: unknown) {
  await mockSitzung(page, me);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([partitur]))),
  );
}

test("Gescheiterte Auswertung zeigt den Grund und läuft nach dem erneuten Versuch weiter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await liedSeite(page, editorMe);
  let stand = auswertung({
    status: "failed",
    text: null,
    failureReason:
      "Das Dokument ist verschlüsselt und kann nicht gelesen werden.",
    completedAt: null,
    attemptCount: 2,
  });
  let anfragen = 0;
  await page.route("**/api/dateien/extraction*", (route) => {
    anfragen += 1;
    expect(new URL(route.request().url()).searchParams.get("ids")).toBe(
      dateiId,
    );
    return route.fulfill(json({ results: [stand] }));
  });
  const wiederholAntraege: unknown[] = [];
  await page.route(`**/api/dateien/${dateiId}/extraction/retry`, (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    wiederholAntraege.push(route.request().postDataJSON());
    stand = auswertung({
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

  await page.goto(`/lied/?id=${mondId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toContainText(
    "Textauswertung gescheitert",
  );
  await expect(zeile.locator(".extraktion-grund")).toHaveText(
    " — Das Dokument ist verschlüsselt und kann nicht gelesen werden.",
  );

  await zeile
    .getByRole("button", { name: /Textauswertung für .* erneut starten/ })
    .click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung wartet …",
  );
  expect(wiederholAntraege).toEqual([{}]);

  // Nach dem Anstoßen fragt die Zeile weiter nach, bis sie fertig ist.
  stand = auswertung();
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
  await liedSeite(page, editorMe);
  await page.route("**/api/dateien/extraction*", (route) =>
    route.fulfill(
      json({
        results: [
          auswertung({
            status: "failed",
            text: null,
            failureReason: "Der Auswertungsdienst hat den Lauf abgebrochen.",
            completedAt: null,
          }),
        ],
      }),
    ),
  );
  await page.route(`**/api/dateien/${dateiId}/extraction/retry`, (route) =>
    route.fulfill(problem("Die Auswertung läuft bereits.", 409)),
  );

  await page.goto(`/lied/?id=${mondId}`);
  const zeile = page.locator(".extraktion-status");
  await zeile
    .getByRole("button", { name: /Textauswertung für .* erneut starten/ })
    .click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung läuft …",
  );
});

test("Eingescannte Noten ohne KI-Lesen bleiben ehrlich ohne Text und ohne Knopf", async ({
  page,
}) => {
  await liedSeite(page, editorMe);
  await page.route("**/api/dateien/extraction*", (route) =>
    route.fulfill(
      json({ results: [auswertung({ status: "noText", text: null })] }),
    ),
  );

  await page.goto(`/lied/?id=${mondId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Kein Text gefunden — vermutlich eingescannt.",
  );
  await expect(zeile.getByRole("button")).toHaveCount(0);
});

test("Teilweise gelesener Scan: „Rest lesen“ stößt die nächsten Seiten an", async ({
  page,
}) => {
  await liedSeite(page, editorMe);
  await page.route("**/api/dateien/extraction*", (route) =>
    route.fulfill(
      json({
        results: [
          auswertung({
            textSource: "reading",
            readingEnabled: true,
            reading: lesen(),
          }),
        ],
      }),
    ),
  );
  const anfragen: string[] = [];
  await page.route(`**/api/dateien/${dateiId}/reading/continue`, (route) => {
    anfragen.push(route.request().method());
    return route.fulfill(
      json({ reading: lesen({ status: "queued", pagesRead: 3 }) }),
    );
  });

  await page.goto(`/lied/?id=${mondId}`);
  await expect(page.locator(".extraktion-status")).toContainText(
    "Teilweise gelesen (3 von 6 Seiten)",
  );
  await page
    .getByRole("button", { name: /Rest lesen: restliche Seiten von/ })
    .click();
  await expect(page.locator(".extraktion-status")).toContainText(
    "KI-Lesen wartet …",
  );
  expect(anfragen).toEqual(["POST"]);
});

test("Mitglieder sehen keinen Auswertungsstand und es gibt keine Nachfrage", async ({
  page,
}) => {
  await liedSeite(page, memberMe);
  let anfragen = 0;
  await page.route("**/api/dateien/extraction*", (route) => {
    anfragen += 1;
    return route.fulfill(json({ results: [] }));
  });

  await page.goto(`/lied/?id=${mondId}`);
  await expect(
    page.getByRole("button", { name: "Noten anzeigen" }),
  ).toBeVisible();
  await expect(page.locator(".extraktion-status")).toHaveCount(0);
  expect(anfragen).toBe(0);
});
