import { expect, type Page, test } from "@playwright/test";
import { mockLeereAufnahmen } from "./aufnahmen-mock";

// #160: Programm des Auftritts — ein Programm je Auftritt, das Speichern
// veröffentlicht es sofort. Lesesaal der Mitglieder, Werkbank der Redaktion
// und das Verzeichnis der kommenden Programme. Die
// Rezepturen folgen tests/auftritt-dokumente.spec.ts und
// tests/auftritte.spec.ts: feste Fixture-Ids, geroutete Antworten mit
// veränderbarem Stand (die zuletzt angemeldete Route gewinnt), Anspruch
// an die Vertragssprache, Handymaß im zweiten Projekt.

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

const eventId = "00000000-0000-0000-0000-00000000e020";
const programmId = "00000000-0000-0000-0000-00000000e021";

// Lied 1 steht zweimal im Programm (zwei eigene Einträge, zwei eigene
// Ids); Lied 2 liefert die Suche als zweites Ergebnis.
const lied1Id = "00000000-0000-0000-0000-0000000000a1";
const lied2Id = "00000000-0000-0000-0000-0000000000a2";

// Programmpunkte des veröffentlichten oder gezeichneten Standes; die
// Werkbank leitet ihre Zeilen daraus ab und behält die Ids stabil.
const punktA = "00000000-0000-0000-0000-00000000b0a1";
const punktB = "00000000-0000-0000-0000-00000000b0a2";
const punktC = "00000000-0000-0000-0000-00000000b0a3";

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
  await page.route("**/api/maintenance", (route) =>
    route.fulfill(json({ maintenance: false })),
  );
}

// Die Liedsuche der Werkbank: das Verzeichnis liefert beide Lieder.
async function mockKatalog(page: Page) {
  await page.route(/\/api\/lieder\?.*/, (route) =>
    route.fulfill(
      json({
        query: "",
        page: 1,
        pageSize: 20,
        total: 2,
        filters: { voiceConfiguration: null, occasion: null, dateiTypen: [] },
        options: { voiceConfigurations: [], occasions: [] },
        lieder: [
          {
            id: lied1Id,
            title: "Das Wandern ist des Müllers Lust",
            composer: null,
            arranger: null,
            lyricist: null,
            voiceConfiguration: "SATB",
            musicalKey: "G-Dur",
            language: null,
            occasion: null,
            dateiTypen: [],
          },
          {
            id: lied2Id,
            title: "Aurora",
            composer: null,
            arranger: null,
            lyricist: null,
            voiceConfiguration: "SATB",
            musicalKey: null,
            language: null,
            occasion: null,
            dateiTypen: [],
          },
        ],
      }),
    ),
  );
}

// Auftrittsdetails mit der Programmeinbettung nach `documents`; die
// Dokumente bleiben leer, der Abschnitt interessiert hier nicht.
function detail(programme: unknown) {
  return {
    event: {
      id: eventId,
      kind: "concert",
      title: "Frühlingskonzert",
      venue: "Stadtsaal Mining",
      dateYear: 2027,
      dateMonth: 5,
      dateDay: 12,
      dateApproximate: false,
      datePrecision: "day",
      dateDisplay: "12. Mai 2027",
      startTime: "19:30",
      published: true,
      notes: null,
      sourceNote: null,
      documents: [],
      createdAt: "2026-09-01T08:00:00.000Z",
      updatedAt: "2026-09-24T10:00:00.000Z",
      publishedAt: "2026-09-20T10:00:00.000Z",
      programme,
    },
  };
}

function punkt(
  id: string,
  position: number,
  liedId: string,
  liedTitle: string,
  musicalKey: string | null,
  note: string | null = null,
) {
  return {
    id,
    position,
    liedId,
    liedTitle,
    voiceConfiguration: "SATB",
    musicalKey,
    note,
  };
}

const items = [
  punkt(
    punktA,
    1,
    lied1Id,
    "Das Wandern ist des Müllers Lust",
    "G-Dur",
    "Erste Strophe im Stehchoral.",
  ),
  punkt(punktB, 2, lied2Id, "Aurora", null, null),
  punkt(punktC, 3, lied1Id, "Das Wandern ist des Müllers Lust", "G-Dur", null),
];

// Das eine Programm mit drei Einträgen (Lied 1 steht zweimal darin).
function programmMitDrei(rowVersion = 4) {
  return {
    id: programmId,
    rowVersion,
    updatedAt: "2026-09-23T12:00:00.000Z",
    items,
  };
}

// Der Server-Schritt des PUT: bekannte Ids bleiben, neue bekommen in
// Array-Reihenfolge frische; die Anzeigefelder stammen aus den Fixtures.
function antwortItems(
  anfrage: Array<{
    id?: string;
    liedId: string;
    note?: string;
  }>,
) {
  return anfrage.map((eintrag, index) => {
    const muster =
      items.find((testPunkt) => testPunkt.liedId === eintrag.liedId) ??
      items[0];
    return {
      ...muster,
      id: eintrag.id ?? [punktA, punktB, punktC][index],
      position: index + 1,
      liedId: eintrag.liedId,
      note: eintrag.note ? eintrag.note : null,
    };
  });
}

type PutAnfrage = {
  items: Array<{ id?: string; liedId: string; note?: string }>;
  rowVersion?: number;
};

// Der PUT-Server: stampft rowVersion hoch und spiegelt die gesendeten
// Einträge; ohne vorhandenes Programm entfällt die rowVersion.
async function mockPut(
  page: Page,
  stand: { aktuell: ReturnType<typeof programmMitDrei> | null },
  puts: PutAnfrage[],
) {
  await page.route(`**/api/events/${eventId}/programme/items`, (route) => {
    expect(route.request().method()).toBe("PUT");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as PutAnfrage;
    expect(anfrage.rowVersion).toBe(stand.aktuell?.rowVersion);
    puts.push(anfrage);
    stand.aktuell = {
      id: programmId,
      rowVersion: (stand.aktuell?.rowVersion ?? 0) + 1,
      updatedAt: "2026-09-25T10:00:00.000Z",
      items: antwortItems(anfrage.items),
    };
    return route.fulfill(json({ programme: stand.aktuell }));
  });
}

// Öffnet den Werkbank-Aufklapper (er ist zu, bis die Redaktion ihn braucht).
async function programmVerwaltungAufklappen(page: Page) {
  await page
    .locator("section.auftritt-programm details.programm-verwaltung > summary")
    .click();
  await expect(
    page.locator("section.auftritt-programm details.programm-verwaltung"),
  ).toHaveAttribute("open", "");
}

async function liedHinzufuegen(page: Page, suche: string, titel: string) {
  await page.getByLabel("Lied aus dem Katalog suchen").fill(suche);
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".programm-lied-treffer")
    .getByRole("button", { name: titel })
    .click();
}

test("Mitglied liest das Programm mit Besetzung, Tonart und Notizen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(programmMitDrei(5)))),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const punkte = page.locator(".programm-liste .programm-eintrag");
  await expect(punkte).toHaveCount(3);
  await expect(punkte.nth(0)).toContainText("1.");
  await expect(punkte.nth(1)).toContainText("2.");
  await expect(punkte.nth(2)).toContainText("3.");
  await expect(punkte.nth(0)).toContainText("Das Wandern ist des Müllers Lust");
  await expect(punkte.nth(1)).toContainText("Aurora");
  // Tiefer Link: das Lied fährt in der Adresse mit.
  await expect(punkte.nth(0).getByRole("link")).toHaveAttribute(
    "href",
    `/lied/?id=${lied1Id}`,
  );
  await expect(punkte.nth(0).locator(".noten-info")).toHaveText(
    "Besetzung: SATB · Tonart: G-Dur",
  );
  await expect(punkte.nth(0).locator(".programm-notiz")).toHaveText(
    "Erste Strophe im Stehchoral.",
  );
  await expect(punkte.nth(1).locator(".programm-notiz")).toHaveCount(0);
  // Kein Entwurf, keine Revision, keine Bestätigung: nur das Programm.
  await expect(page.getByText(/Veröffentlicht am|Revision/)).toHaveCount(0);
  await expect(
    page.locator("section.auftritt-programm details.programm-verwaltung"),
  ).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);

  expect(errors).toEqual([]);
});

test("Ohne Programm bleibt der Abschnitt für Mitglieder ehrlich leer", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(null))),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Das Programm wurde noch nicht erfasst."),
  ).toBeVisible();
  await expect(page.locator(".programm-liste")).toHaveCount(0);
  await expect(
    page.locator("section.auftritt-programm details.programm-verwaltung"),
  ).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Redaktion legt ein Programm an: erster PUT ohne rowVersion, der zweite mit stabilen Ids", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  const stand: { aktuell: ReturnType<typeof programmMitDrei> | null } = {
    aktuell: null,
  };
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(stand.aktuell))),
  );
  const puts: PutAnfrage[] = [];
  await mockPut(page, stand, puts);

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  // Es gibt nur "Programm speichern": keine Entwürfe, kein Veröffentlichen.
  await expect(
    page.getByRole("button", { name: "Veröffentlichen" }),
  ).toHaveCount(0);

  await liedHinzufuegen(page, "Wandern", "Das Wandern ist des Müllers Lust");
  await liedHinzufuegen(page, "Aurora", "Aurora");
  // Dritter Eintrag wiederholt Lied 1.
  await liedHinzufuegen(page, "Wandern", "Das Wandern ist des Müllers Lust");
  await expect(page.locator(".programm-zeile")).toHaveCount(3);
  await page
    .locator(".programm-zeile")
    .nth(0)
    .getByLabel("Notiz (optional)")
    .fill("Erste Strophe im Stehchoral.");

  await page.getByRole("button", { name: "Programm speichern" }).click();
  await expect(
    page.getByText("Programm gespeichert. Mitglieder sehen es ab sofort."),
  ).toBeVisible();
  expect(puts).toHaveLength(1);
  expect(puts[0].rowVersion).toBeUndefined();
  // Ganzer geordneter Ersatz: neue Einträge tragen keine Id.
  expect(puts[0].items).toEqual([
    { liedId: lied1Id, note: "Erste Strophe im Stehchoral." },
    { liedId: lied2Id, note: "" },
    { liedId: lied1Id, note: "" },
  ]);

  // Der Stand kommt mit stabilen Ids zurück und trägt sie ins nächste PUT.
  await expect(page.locator(".programm-unerledigt")).toHaveCount(0);
  await page.getByRole("button", { name: "Programm speichern" }).click();
  await expect(async () => expect(puts).toHaveLength(2)).toPass();
  expect(puts[1].items).toEqual([
    { id: punktA, liedId: lied1Id, note: "Erste Strophe im Stehchoral." },
    { id: punktB, liedId: lied2Id, note: "" },
    { id: punktC, liedId: lied1Id, note: "" },
  ]);
  expect(puts[1].rowVersion).toBe(1);

  expect(errors).toEqual([]);
});

test("Umordnen und Entfernen nachträglich wandern in der PUT-Reihenfolge mit stabilen Ids", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  const stand: { aktuell: ReturnType<typeof programmMitDrei> | null } = {
    aktuell: programmMitDrei(),
  };
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(stand.aktuell))),
  );
  const puts: PutAnfrage[] = [];
  await mockPut(page, stand, puts);

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  await expect(page.locator(".programm-zeile")).toHaveCount(3);

  await page
    .locator(".programm-zeile")
    .nth(2)
    .getByRole("button", { name: "Eintrag 3 nach oben schieben" })
    .click();
  await expect(page.locator(".programm-unerledigt")).toHaveText(
    "Nicht gespeicherte Änderungen.",
  );
  await page.getByRole("button", { name: "Programm speichern" }).click();
  await expect(page.getByText(/Programm gespeichert/)).toBeVisible();
  await expect(page.locator(".programm-unerledigt")).toHaveCount(0);
  expect(puts[0].items).toEqual([
    { id: punktA, liedId: lied1Id, note: "Erste Strophe im Stehchoral." },
    { id: punktC, liedId: lied1Id, note: "" },
    { id: punktB, liedId: lied2Id, note: "" },
  ]);
  expect(puts[0].rowVersion).toBe(4);

  await page
    .locator(".programm-zeile")
    .nth(0)
    .getByRole("button", { name: "Eintrag 1 entfernen" })
    .click();
  await page.getByRole("button", { name: "Programm speichern" }).click();
  await expect(async () => expect(puts).toHaveLength(2)).toPass();
  expect(puts[1].items).toEqual([
    { id: punktC, liedId: lied1Id, note: "" },
    { id: punktB, liedId: lied2Id, note: "" },
  ]);
  expect(puts[1].rowVersion).toBe(5);

  expect(errors).toEqual([]);
});

test("Veralteter Stand erklärt sich und lädt den frischen Stand", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  const frischerStand = {
    ...programmMitDrei(9),
    items: [items[0], { ...items[1], note: "Vom anderen Beitrag ergänzt." }],
  };
  let abfragen = 0;
  await page.route(`**/api/events/${eventId}`, (route) => {
    abfragen += 1;
    return route.fulfill(
      json(detail(abfragen === 1 ? programmMitDrei() : frischerStand)),
    );
  });
  const puts: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/programme/items`, (route) => {
    puts.push(route.request().postDataJSON() as Record<string, unknown>);
    return route.fulfill(
      problem("Das Programm wurde zwischenzeitlich geändert.", 409),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  await expect(page.locator(".programm-zeile")).toHaveCount(3);

  await page.getByRole("button", { name: "Programm speichern" }).click();
  await expect(
    page.getByText(
      "Das Programm wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.",
    ),
  ).toBeVisible();
  await expect(
    page.locator(".programm-zeile").nth(1).getByLabel("Notiz (optional)"),
  ).toHaveValue("Vom anderen Beitrag ergänzt.");
  expect(abfragen).toBe(2);
  expect(puts).toHaveLength(1);

  expect(errors).toEqual([]);
});

test("Hängen Zeitmarken an einem entfernten Eintrag, nennt die Antwort sie beim Namen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(programmMitDrei()))),
  );
  await page.route(`**/api/events/${eventId}/programme/items`, (route) =>
    route.fulfill(
      problem(
        "Zeitmarken vorhanden: „Aurora“ in „Mitschnitt“. Entferne zuerst diese Zeitmarken in den Aufnahmen.",
        409,
      ),
    ),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  await page
    .locator(".programm-zeile")
    .nth(1)
    .getByRole("button", { name: "Eintrag 2 entfernen" })
    .click();
  await page.getByRole("button", { name: "Programm speichern" }).click();
  await expect(page.getByText(/Zeitmarken vorhanden: „Aurora“/)).toBeVisible();
  // Die Arbeit bleibt stehen, nichts wurde stillschweigend verworfen.
  await expect(page.locator(".programm-zeile")).toHaveCount(2);

  expect(errors).toEqual([]);
});

test("Das Verzeichnis listet das kommende Programm mit ehrlichem Datum und Liederzahl", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route("**/api/programmes", (route) =>
    route.fulfill(
      json({
        programmes: [
          {
            eventId,
            eventTitle: "Frühlingskonzert",
            kind: "concert",
            dateDisplay: "12. Mai 2027",
            datePrecision: "day",
            dateApproximate: false,
            venue: "Stadtsaal Mining",
            startTime: "19:30",
            updatedAt: "2026-09-20T10:00:00.000Z",
            itemCount: 3,
          },
        ],
      }),
    ),
  );

  await page.goto("/programm/");
  const zeile = page.locator(".programme-eintrag");
  await expect(zeile).toHaveCount(1);
  await expect(zeile.getByText("Frühlingskonzert")).toBeVisible();
  await expect(zeile.getByText("12. Mai 2027")).toBeVisible();
  await expect(zeile.getByText("3 Lieder")).toBeVisible();
  await expect(zeile.getByRole("link")).toHaveAttribute(
    "href",
    `/auftritt/?id=${eventId}`,
  );
  await expect(zeile.locator(".datum-unsicher")).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);

  expect(errors).toEqual([]);
});

test("Unsichere Daten bleiben auch im Programmverzeichnis gekennzeichnet", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1804, height: 1304 });
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route("**/api/programmes", (route) =>
    route.fulfill(
      json({
        programmes: [
          {
            eventId,
            eventTitle: "Frühlingskonzert",
            kind: "concert",
            dateDisplay: "12. Mai 2027",
            datePrecision: "day",
            dateApproximate: false,
            venue: "Stadtsaal Mining",
            startTime: "19:30",
            updatedAt: "2026-09-20T10:00:00.000Z",
            itemCount: 3,
          },
          {
            eventId: "00000000-0000-0000-0000-00000000e030",
            eventTitle: "Sängerfest (um 1950)",
            kind: "singing",
            dateDisplay: "um 1950",
            datePrecision: "year",
            dateApproximate: true,
            venue: null,
            startTime: null,
            updatedAt: "2026-09-20T10:00:00.000Z",
            itemCount: 1,
          },
        ],
      }),
    ),
  );

  await page.goto("/programm/");
  const zeilen = page.locator(".programme-eintrag");
  await expect(zeilen).toHaveCount(2);
  // Das Tagesdatum liest sicher, die Jahresangabe trägt die Marke.
  await expect(zeilen.nth(0).locator(".datum-unsicher")).toHaveCount(0);
  await expect(zeilen.nth(1).locator(".datum-unsicher")).toHaveText(
    "Datum unsicher",
  );
  const zeile = zeilen.first();
  const datum = await zeile.locator(".auftritt-datum").boundingBox();
  const titel = await zeile.locator("h3").boundingBox();
  const anzahl = await zeile.locator(".programme-anzahl").boundingBox();
  const inhalt = await page.locator(".archiv-seiteninhalt").boundingBox();
  const eintrag = await zeile.boundingBox();
  if (!datum || !titel || !anzahl || !inhalt || !eintrag)
    throw new Error("Programmzeile ist nicht sichtbar");
  expect(eintrag.width).toBeGreaterThan(inhalt.width * 0.95);
  expect(datum.x + datum.width).toBeLessThan(titel.x);
  expect(titel.x + titel.width).toBeLessThan(anzahl.x);
  expect(Math.abs(datum.y - anzahl.y)).toBeLessThan(20);

  await page.setViewportSize({ width: 680, height: 844 });
  const mittelDatum = await zeile.locator(".auftritt-datum").boundingBox();
  const mittelTitel = await zeile.locator("h3").boundingBox();
  const mittelAnzahl = await zeile.locator(".programme-anzahl").boundingBox();
  if (!mittelDatum || !mittelTitel || !mittelAnzahl)
    throw new Error("Programmzeile bei 680px fehlt");
  expect(mittelTitel.x).toBeCloseTo(mittelDatum.x, 0);
  expect(mittelAnzahl.x).toBeCloseTo(mittelDatum.x, 0);
  expect(mittelTitel.y).toBeGreaterThan(mittelDatum.y);
  expect(mittelAnzahl.y).toBeGreaterThan(mittelTitel.y);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(
    680,
  );

  await page.setViewportSize({ width: 390, height: 844 });
  const mobilDatum = await zeile.locator(".auftritt-datum").boundingBox();
  const mobilTitel = await zeile.locator("h3").boundingBox();
  if (!mobilDatum || !mobilTitel) throw new Error("Mobile Programmzeile fehlt");
  expect(mobilTitel.y).toBeGreaterThan(mobilDatum.y);
  // Auch im Handymaß bleibt das Verzeichnis ohne Seitenüberlauf.
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);

  expect(errors).toEqual([]);
});
