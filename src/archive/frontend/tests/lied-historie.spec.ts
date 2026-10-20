import { expect, type Page, test } from "@playwright/test";
import { mockLeereAufnahmen } from "./aufnahmen-mock";
import {
  editorMe,
  json,
  liedDetail,
  memberMe,
  mockSitzung as mockAnmeldung,
  mondId,
} from "./lieder-mock";

// #160: „Wann gesungen“ auf der Liedseite — je Auftritt eine Zeile, abgeleitet
// aus den Programmen und den Aufnahmen des Liedes, die einen Auftritt nennen
// (mit Vorbehalt „nur erfasste Überlieferung“); unsichere Daten sichtbar.
// Vertragsform von GET /api/lieder/{id}/historie (tests/archive/backend/
// LiedHistorieTests.cs).

const liedId = mondId;
const titel = "Der Mond ist aufgegangen";
const auftritt1 = "00000000-0000-0000-0000-00000000e001";
const auftritt2 = "00000000-0000-0000-0000-00000000e002";
const auftritt3 = "00000000-0000-0000-0000-00000000e003";

async function mockSitzung(page: Page, me: unknown) {
  await mockLeereAufnahmen(page);
  await mockAnmeldung(page, me);
  await page.route("**/api/maintenance", (route) =>
    route.fulfill(json({ maintenance: false })),
  );
  await page.route(`**/api/lieder/${liedId}`, (route) =>
    route.fulfill(json(liedDetail([]))),
  );
}

type Zeile = Record<string, unknown>;

function zeile(eventId: string, titel: string, extra: Zeile = {}): Zeile {
  return {
    eventId,
    eventTitle: titel,
    eventKind: "concert",
    dateYear: 1975,
    dateMonth: 6,
    dateDay: 1,
    dateApproximate: false,
    dateDisplay: "1. Juni 1975",
    datePrecision: "day",
    dateUncertain: false,
    inProgramme: true,
    entries: 1,
    recordings: [],
    liedAufnahmen: [],
    ...extra,
  };
}

const zaehlung = {
  events: 4,
  inProgramme: 3,
  uncertainDates: 2,
};

function historie(zeilen: Zeile[], extra: Zeile = {}) {
  return {
    lied: { id: liedId, title: titel },
    page: 1,
    pageSize: 20,
    total: zeilen.length,
    counts: zaehlung,
    auftritte: zeilen,
    ...extra,
  };
}

const gemischteZeilen = [
  zeile(auftritt1, "Frühjahrskonzert", { entries: 2 }),
  zeile(auftritt2, "Sommerfest", {
    eventKind: "festival",
    dateYear: 1960,
    dateMonth: null,
    dateDay: null,
    dateApproximate: true,
    dateDisplay: "um 1960",
    datePrecision: "year",
    dateUncertain: true,
  }),
  zeile(auftritt3, "Gottesdienst ohne Datum", {
    eventKind: "service",
    dateYear: null,
    dateMonth: null,
    dateDay: null,
    dateDisplay: "Datum unbekannt",
    datePrecision: "unknown",
    dateUncertain: true,
    inProgramme: false,
    entries: 0,
    liedAufnahmen: [
      {
        id: "00000000-0000-0000-0000-00000000d001",
        fileName: "gottesdienst.mp3",
        contentType: "audio/mpeg",
        description: null,
      },
    ],
  }),
];

async function mockHistorie(
  page: Page,
  antwort: (url: URL) => { status?: number; body: unknown },
) {
  const anfragen: string[] = [];
  await page.route(
    new RegExp(`/api/lieder/${liedId}/historie(\\?.*)?$`),
    (route) => {
      const url = new URL(route.request().url());
      anfragen.push(url.search);
      const stand = antwort(url);
      return route.fulfill(json(stand.body, stand.status ?? 200));
    },
  );
  return anfragen;
}

test("Mitglieder sehen ehrlich begrenzte Zahlen und je Auftritt eine Zeile", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await mockHistorie(page, () => ({ body: historie(gemischteZeilen) }));
  await page.goto(`/lied/?id=${liedId}`);

  const abschnitt = page.getByRole("region", { name: "Wann gesungen" });
  await expect(abschnitt).toContainText("keine vollständige Chronik");
  await expect(abschnitt).toContainText("4 Auftritte");
  await expect(abschnitt).toContainText(
    "Bei 1 Auftritt steht das Lied nicht im Programm, es gibt aber eine Aufnahme.",
  );
  await expect(abschnitt).toContainText(
    "Bei 2 ist das Datum unsicher oder unbekannt.",
  );
  // Nichts vom entfernten Nachweismodell.
  await expect(abschnitt).not.toContainText("Bestätigt");
  await expect(abschnitt).not.toContainText("Programmangabe");

  const zeilen = abschnitt.locator(".historie-eintrag");
  await expect(zeilen).toHaveCount(3);
  await expect(zeilen.nth(0).getByRole("link")).toHaveAttribute(
    "href",
    `/auftritt/?id=${auftritt1}`,
  );
  await expect(zeilen.nth(0)).toContainText("1. Juni 1975");
  await expect(zeilen.nth(0)).toContainText("2-mal im Programm");
  await expect(zeilen.nth(0).locator(".datum-unsicher")).toHaveCount(0);
  // Ein Jahr allein bleibt gekennzeichnet.
  await expect(zeilen.nth(1)).toContainText("um 1960");
  await expect(zeilen.nth(1).locator(".datum-unsicher")).toHaveText(
    "Datum unsicher",
  );
  await expect(zeilen.nth(1)).toContainText("Im Programm");
  // Nur eine Lied-Aufnahme nennt den Auftritt.
  await expect(zeilen.nth(2)).toContainText("Datum unbekannt");
  await expect(zeilen.nth(2)).toContainText(
    "Nicht im Programm, aber aufgenommen",
  );
  await expect(
    zeilen.nth(2).getByRole("list", { name: "Aufnahmen des Liedes" }),
  ).toContainText("gottesdienst.mp3");
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});

test("Ein Lied ohne Auftritt sagt ehrlich, dass nichts erfasst ist", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await mockHistorie(page, () => ({
    body: historie([], {
      counts: {
        events: 0,
        inProgramme: 0,
        uncertainDates: 0,
      },
    }),
  }));
  await page.goto(`/lied/?id=${liedId}`);

  const abschnitt = page.getByRole("region", { name: "Wann gesungen" });
  await expect(abschnitt).toContainText(
    "Für dieses Lied ist noch kein Auftritt erfasst.",
  );
  await expect(abschnitt).toContainText(
    "Das heißt nicht, dass es nie gesungen wurde.",
  );
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(0);
});

test("Eine Seite hinter dem Ende übernimmt die vom Server gemeldete Seite", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  const anfragen = await mockHistorie(page, (url) => {
    const seite = url.searchParams.get("page");
    if (seite === "2") {
      return {
        body: historie([zeile(auftritt3, "Zweite Seite")], {
          page: 2,
          total: 21,
        }),
      };
    }
    return { body: historie(gemischteZeilen, { total: 21 }) };
  });
  await page.goto(`/lied/?id=${liedId}`);
  const abschnitt = page.getByRole("region", { name: "Wann gesungen" });
  await expect(abschnitt).toContainText("Seite 1 von 2");
  await abschnitt.getByRole("button", { name: "Weiter" }).click();
  await expect(abschnitt).toContainText("Seite 2 von 2");
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(1);
  expect(anfragen.some((suche) => suche.includes("page=2"))).toBe(true);
});

test("Ein Ladefehler bleibt im Abschnitt und lässt sich wiederholen", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  let kaputt = true;
  await mockHistorie(page, () => {
    if (kaputt) {
      return { status: 500, body: { title: "Fehler" } };
    }
    return { body: historie(gemischteZeilen) };
  });
  await page.goto(`/lied/?id=${liedId}`);
  // Das Lied selbst bleibt benutzbar.
  await expect(page.getByRole("heading", { name: titel })).toBeVisible();
  const abschnitt = page.getByRole("region", { name: "Wann gesungen" });
  await expect(abschnitt.getByRole("alert")).toContainText(
    "konnte nicht geladen werden",
  );
  kaputt = false;
  await abschnitt.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(3);
});

test("Auftritte mit markierter Stelle verweisen in die Aufnahme, andere bleiben ohne Aussage", async ({
  page,
}) => {
  const aufnahmeId = "00000000-0000-0000-0000-00000000c321";
  const zweiteId = "00000000-0000-0000-0000-00000000c322";
  const stelleId = "00000000-0000-0000-0000-00000000e101";
  const zweiteStelle = "00000000-0000-0000-0000-00000000e102";
  const zeilen = [
    zeile(auftritt1, "Frühjahrskonzert", {
      recordings: [
        {
          passageId: stelleId,
          recordingId: aufnahmeId,
          recordingLabel: "Video Kamera 1",
          kind: "video",
          isPublished: true,
          startSeconds: 750,
          endSeconds: 940.5,
          timestampState: "current",
        },
        {
          passageId: zweiteStelle,
          recordingId: zweiteId,
          recordingLabel: "Tonmitschnitt",
          kind: "audio",
          isPublished: true,
          startSeconds: null,
          endSeconds: null,
          timestampState: "needsReview",
        },
      ],
    }),
    zeile(auftritt2, "Sommerfest"),
  ];
  await mockSitzung(page, memberMe);
  await mockHistorie(page, () => ({ body: historie(zeilen) }));
  await page.goto(`/lied/?id=${liedId}`);

  const erste = page.locator(`[data-event-id="${auftritt1}"]`);
  const liste = erste.getByRole("list", { name: "Aufnahmen mit Zeitmarke" });
  await expect(liste.getByRole("listitem")).toHaveCount(2);
  const sprung = liste.getByRole("link", {
    name: "Zu „Der Mond ist aufgegangen“ in „Video Kamera 1“ springen",
  });
  await expect(sprung).toHaveAttribute(
    "href",
    `/auftritt/?id=${auftritt1}&aufnahme=${aufnahmeId}&stelle=${stelleId}`,
  );
  await expect(liste).toContainText("Video „Video Kamera 1“ · 12:30 – 15:40,5");
  // Zu prüfende Marke: nur die ganze Aufnahme, ohne Zeit und ohne Sprung.
  await expect(liste).toContainText(
    "Tonaufnahme „Tonmitschnitt“ · Zeitmarke wird überprüft",
  );
  await expect(
    liste.getByRole("link", { name: "Aufnahme „Tonmitschnitt“ öffnen" }),
  ).toHaveAttribute("href", `/auftritt/?id=${auftritt1}&aufnahme=${zweiteId}`);
  await expect(
    liste.getByRole("link", { name: /„Tonmitschnitt“ springen/ }),
  ).toHaveCount(0);

  // Ohne Markierung: kein Platz, keine „keine Aufnahmen“-Aussage.
  const zweite = page.locator(`[data-event-id="${auftritt2}"]`);
  await expect(zweite.getByRole("list", { name: /Aufnahmen/ })).toHaveCount(0);
  await expect(page.getByText(/keine Aufnahme/i)).toHaveCount(0);
});

test("Redaktion sieht Entwurfsaufnahmen und darf eine ungeprüfte Marke anspringen", async ({
  page,
}) => {
  const aufnahmeId = "00000000-0000-0000-0000-00000000c321";
  const stelleId = "00000000-0000-0000-0000-00000000e101";
  await mockSitzung(page, editorMe);
  await mockHistorie(page, () => ({
    body: historie([
      zeile(auftritt1, "Frühjahrskonzert", {
        recordings: [
          {
            passageId: stelleId,
            recordingId: aufnahmeId,
            recordingLabel: "Rohschnitt",
            kind: "video",
            isPublished: false,
            startSeconds: 60,
            endSeconds: 200,
            timestampState: "needsReview",
          },
        ],
      }),
    ]),
  }));
  await page.goto(`/lied/?id=${liedId}`);
  const liste = page
    .locator(`[data-event-id="${auftritt1}"]`)
    .getByRole("list", { name: "Aufnahmen mit Zeitmarke" });
  await expect(liste).toContainText("Entwurf");
  await expect(liste).toContainText("Zeitmarke zu prüfen");
  await expect(liste).toContainText("1:00 – 3:20");
  await expect(
    liste.getByRole("link", {
      name: "Zu „Der Mond ist aufgegangen“ in „Rohschnitt“ springen",
    }),
  ).toBeVisible();
});
