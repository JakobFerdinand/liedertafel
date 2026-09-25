import { expect, type Page, test } from "@playwright/test";

// ARC-026: Programm des Auftritts — Lesesaal der Mitglieder, Werkbank
// der Redaktion und das Verzeichnis der kommenden Programme. Die
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
const revisionEntwurfId = "00000000-0000-0000-0000-00000000e022";
const revisionVeroeffentlichtId = "00000000-0000-0000-0000-00000000e023";

// Lied 1 kennt die Standard- und die transponierte Fassung; Lied 2
// liefern die Suche als zweites Ergebnis; der dritte Programmpunkt
// wiederholt Lied 1 (zwei eigene Einträge, zwei eigene Ids).
const lied1Id = "00000000-0000-0000-0000-0000000000a1";
const lied2Id = "00000000-0000-0000-0000-0000000000a2";
const lied2FassungId = "00000000-0000-0000-0000-00000000d010";
const arrangement1Id = "00000000-0000-0000-0000-00000000c0a1";
const arrangement2Id = "00000000-0000-0000-0000-00000000c0a2";
const standardId = "00000000-0000-0000-0000-00000000d00a";
const transponiertId = "00000000-0000-0000-0000-00000000d00b";
const lied2ArrangementId = "00000000-0000-0000-0000-00000000c0b1";

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
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
  await page.route("**/api/maintenance", (route) =>
    route.fulfill(json({ maintenance: false })),
  );
}

// Lieddetails für die FassungsWahl: Lied 1 trägt zwei Fassungen
// (Standard + Transposition nach Es-Dur), Lied 2 eine eigene Fassung.
async function mockKatalog(page: Page) {
  await page.route(/\/api\/songs\?.*/, (route) =>
    route.fulfill(
      json({
        query: "",
        page: 1,
        pageSize: 20,
        total: 2,
        songs: [
          {
            id: lied1Id,
            title: "Das Wandern ist des Müllers Lust",
            composer: null,
            lyricist: null,
            published: true,
            publishedAt: null,
            alternateTitles: [],
            arrangements: [],
            matchedIn: [],
            matchedArrangements: [],
            language: null,
            occasion: null,
            tags: [],
          },
          {
            id: lied2Id,
            title: "Aurora",
            composer: null,
            lyricist: null,
            published: true,
            publishedAt: null,
            alternateTitles: [],
            arrangements: [],
            matchedIn: [],
            matchedArrangements: [],
            language: null,
            occasion: null,
            tags: [],
          },
        ],
      }),
    ),
  );
  await page.route(`**/api/songs/${lied1Id}`, (route) =>
    route.fulfill(
      json({
        song: {
          id: lied1Id,
          title: "Das Wandern ist des Müllers Lust",
          composer: "Carl Friedrich Zöllner",
          lyricist: "Wilhelm Müller",
          published: true,
          publishedAt: null,
          alternateTitles: [],
          matchedIn: [],
          matchedArrangements: [],
          createdAt: "2026-08-20T08:00:00.000Z",
          updatedAt: "2026-09-01T10:00:00.000Z",
          lyrics: null,
          language: null,
          occasion: null,
          tags: [],
          arrangements: [
            {
              id: arrangement1Id,
              label: "Satz für gemischten Chor",
              arranger: "Josef Gabriel",
              voiceConfiguration: "SATB",
              accompaniment: null,
              musicalVersions: [
                {
                  id: standardId,
                  label: "Standardfassung",
                  creator: "Josef Gabriel",
                  musicalKey: "G-Dur",
                  assets: [],
                },
                {
                  id: transponiertId,
                  label: "Transposition",
                  creator: "Josef Gabriel",
                  musicalKey: "Es-Dur",
                  assets: [],
                },
              ],
            },
            {
              id: arrangement2Id,
              label: "Satz für Männerchor",
              arranger: "Hans Schmid",
              voiceConfiguration: "TTBB",
              accompaniment: null,
              musicalVersions: [
                {
                  id: "00000000-0000-0000-0000-00000000d00c",
                  label: "Männerchor",
                  creator: "Hans Schmid",
                  musicalKey: null,
                  assets: [],
                },
              ],
            },
          ],
        },
      }),
    ),
  );
  await page.route(`**/api/songs/${lied2Id}`, (route) =>
    route.fulfill(
      json({
        song: {
          id: lied2Id,
          title: "Aurora",
          composer: null,
          lyricist: null,
          published: true,
          publishedAt: null,
          alternateTitles: [],
          matchedIn: [],
          matchedArrangements: [],
          createdAt: "2026-08-20T08:00:00.000Z",
          updatedAt: "2026-09-01T10:00:00.000Z",
          lyrics: null,
          language: null,
          occasion: null,
          tags: [],
          arrangements: [
            {
              id: lied2ArrangementId,
              label: "Satz für gemischten Chor",
              arranger: null,
              voiceConfiguration: "SATB",
              accompaniment: null,
              musicalVersions: [
                {
                  id: lied2FassungId,
                  label: "Standardfassung",
                  creator: null,
                  musicalKey: "G-Dur",
                  assets: [],
                },
              ],
            },
          ],
        },
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
  songId: string,
  songTitle: string,
  arrangementId: string,
  arrangementLabel: string,
  versionId: string,
  musicalVersionLabel: string,
  musicalKey: string | null,
  note: string | null = null,
) {
  return {
    id,
    position,
    songId,
    arrangementId,
    musicalVersionId: versionId,
    songTitle,
    arrangementLabel,
    voiceConfiguration: "SATB",
    musicalVersionLabel,
    musicalKey,
    note,
  };
}

const veroeffentlichteItems = [
  punkt(
    punktA,
    1,
    lied1Id,
    "Das Wandern ist des Müllers Lust",
    arrangement1Id,
    "Satz für gemischten Chor",
    standardId,
    "Standardfassung",
    "G-Dur",
    "Erste Strophe im Stehchoral.",
  ),
  punkt(
    punktB,
    2,
    lied2Id,
    "Aurora",
    lied2ArrangementId,
    "Satz für gemischten Chor",
    lied2FassungId,
    "Standardfassung",
    null,
    null,
  ),
  punkt(
    punktC,
    3,
    lied1Id,
    "Das Wandern ist des Müllers Lust",
    arrangement1Id,
    "Satz für gemischten Chor",
    transponiertId,
    "Transposition",
    "Es-Dur",
    null,
  ),
];

const veroeffentlicht = {
  id: programmId,
  rowVersion: 5,
  working: null,
  published: {
    id: revisionVeroeffentlichtId,
    number: 1,
    publishedAt: "2026-09-20T10:00:00.000Z",
    items: veroeffentlichteItems,
  },
};

// Leerer Entwurf (rowVersion 3) als Ausgangslage der Aufbau-Prüfung.
const entwurfLeer = {
  id: programmId,
  rowVersion: 3,
  working: {
    id: revisionEntwurfId,
    number: 1,
    updatedAt: "2026-09-23T10:00:00.000Z",
    items: [],
  },
  published: null,
};

// Entwurf mit den drei Programmpunkten und stabilen Ids; die rowVersion
// zählt je Speicherung hoch (der Server stampft sie mit).
function entwurfStandMitDrei(rowVersion = 4) {
  return {
    id: programmId,
    rowVersion,
    working: {
      id: revisionEntwurfId,
      number: 1,
      updatedAt: "2026-09-23T12:00:00.000Z",
      items: veroeffentlichteItems,
    },
    published: null,
  };
}

// Der Server-Schritt des PUT: bekannte Ids bleiben, neue bekommen in
// Array-Reihenfolge frische; die Anzeigefelder stammen aus der
// Fassungskette (hier: die Fixtures als Muster).
function antwortItems(
  anfrage: Array<{
    id?: string;
    songId: string;
    musicalVersionId: string;
    note?: string;
  }>,
) {
  return anfrage.map((eintrag, index) => {
    const muster =
      veroeffentlichteItems.find(
        (testPunkt) => testPunkt.musicalVersionId === eintrag.musicalVersionId,
      ) ?? veroeffentlichteItems[0];
    return {
      ...muster,
      id: eintrag.id ?? [punktA, punktB, punktC][index],
      position: index + 1,
      songId: eintrag.songId,
      musicalVersionId: eintrag.musicalVersionId,
      note: eintrag.note ?? null,
    };
  });
}

// Entwurf mit einem einzigen Programmpunkt (frisch angelegtes Programm).
function entwurfStandMitEinem() {
  return {
    id: programmId,
    rowVersion: 1,
    working: {
      id: revisionEntwurfId,
      number: 1,
      updatedAt: "2026-09-23T12:00:00.000Z",
      items: [veroeffentlichteItems[1]],
    },
    published: null,
  };
}

// Öffnet den Werkbank-Aufklapper (er ist zu, bis die Redaktion ihn braucht).
async function programmVerwaltungAufklappen(page: Page) {
  await page.locator("details.programm-verwaltung > summary").click();
  await expect(page.locator("details.programm-verwaltung")).toHaveAttribute(
    "open",
    "",
  );
}

test("Mitglied liest das veröffentlichte Programm mit Fassungsangaben und Notizen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(veroeffentlicht))),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const punkte = page.locator(".programm-liste .programm-eintrag");
  await expect(punkte).toHaveCount(3);
  // Geordnete Reihe mit Positionsnummern: die Titel erscheinen in
  // Positionsreihenfolge.
  await expect(punkte.nth(0)).toContainText("1.");
  await expect(punkte.nth(1)).toContainText("2.");
  await expect(punkte.nth(2)).toContainText("3.");
  await expect(punkte.nth(0)).toContainText("Das Wandern ist des Müllers Lust");
  await expect(punkte.nth(1)).toContainText("Aurora");
  await expect(punkte.nth(2)).toContainText("Das Wandern ist des Müllers Lust");
  // Deep-Link: Lied, Fassung und musikalische Version fahren in der
  // Adresse mit.
  await expect(punkte.nth(0).getByRole("link")).toHaveAttribute(
    "href",
    `/lied/?id=${lied1Id}&fassung=${arrangement1Id}&version=${standardId}`,
  );
  await expect(punkte.nth(2).getByRole("link")).toHaveAttribute(
    "href",
    `/lied/?id=${lied1Id}&fassung=${arrangement1Id}&version=${transponiertId}`,
  );
  // Fassungszeile mit Tonart, Notiz je Eintrag.
  await expect(punkte.nth(0).locator(".noten-info")).toHaveText(
    "Satz für gemischten Chor · Standardfassung · Tonart: G-Dur · Stimmkonfiguration: SATB",
  );
  await expect(punkte.nth(0).locator(".programm-notiz")).toHaveText(
    "Erste Strophe im Stehchoral.",
  );
  await expect(punkte.nth(1).locator(".programm-notiz")).toHaveCount(0);
  await expect(
    page.getByText(/Veröffentlicht am 20\. September 2026/),
  ).toBeVisible();
  // Der Werkbank-Aufklapper bleibt dem Mitglied verborgen.
  await expect(page.locator("details.programm-verwaltung")).toHaveCount(0);
  // Auch im Handymaß bleibt der Abschnitt ohne Seitenüberlauf.
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
  await expect(page.locator("details.programm-verwaltung")).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Aurora" })).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Redaktion baut ein dreiliediges Programm auf und speichert zweimal mit stabilen Ids", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  // Der Stand wandert mit: leerer Entwurf (rowVersion 3), nach dem
  // ersten Speichern der Stand mit Server-Ids (rowVersion 4).
  let stand: unknown = entwurfLeer;
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(stand))),
  );

  const puts: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/programme/items`, (route) => {
    expect(route.request().method()).toBe("PUT");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as {
      items: Array<{
        id?: string;
        songId: string;
        musicalVersionId: string;
        note?: string;
      }>;
      rowVersion?: number;
    };
    // Der Concurrency-Anker zählt mit: der erste PUT trägt die rowVersion
    // des leeren Entwurfs, der zweite die frische nach dem Speichern.
    const bisher = stand as { rowVersion: number };
    expect(anfrage.rowVersion).toBe(bisher.rowVersion);
    puts.push(anfrage);
    // Der Server behält die gespeicherten Notizen: die Antwort spiegelt
    // die gesendeten Einträge mit stabilen und frischen Ids.
    stand = {
      id: programmId,
      rowVersion: bisher.rowVersion + 1,
      working: {
        id: revisionEntwurfId,
        number: 1,
        updatedAt: "2026-09-23T12:00:00.000Z",
        items: antwortItems(anfrage.items),
      },
      published: null,
    };
    return route.fulfill(json({ programme: stand }));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Das Programm wurde noch nicht erfasst."),
  ).toBeVisible();
  await programmVerwaltungAufklappen(page);

  // Erstes Lied über die Katalogsuche; die Fassung kommt aus dem
  // Lieddetail (Standardfassung in G-Dur).
  await page.getByLabel("Lied aus dem Katalog suchen").fill("Wandern");
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".programm-lied-treffer")
    .getByRole("button", { name: "Das Wandern ist des Müllers Lust" })
    .click();
  await expect(page.locator(".programm-zeile")).toHaveCount(1);
  await page
    .locator(".programm-zeile")
    .nth(0)
    .getByRole("button", { name: /Standardfassung/ })
    .click();

  // Zweites Lied mit eigener Standardfassung.
  await page.getByLabel("Lied aus dem Katalog suchen").fill("Aurora");
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".programm-lied-treffer")
    .getByRole("button", { name: "Aurora" })
    .click();
  await expect(page.locator(".programm-zeile")).toHaveCount(2);
  await page
    .locator(".programm-zeile")
    .nth(1)
    .getByRole("button", { name: /Standardfassung/ })
    .click();

  // Dritter Eintrag wiederholt Lied 1, diesmal transponiert nach Es-Dur.
  await page.getByLabel("Lied aus dem Katalog suchen").fill("Wandern");
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".programm-lied-treffer")
    .getByRole("button", { name: "Das Wandern ist des Müllers Lust" })
    .click();
  await expect(page.locator(".programm-zeile")).toHaveCount(3);
  await page
    .locator(".programm-zeile")
    .nth(2)
    .getByRole("button", { name: /Transposition/ })
    .click();

  // Notizen in den ersten beiden Einträgen.
  await page
    .locator(".programm-zeile")
    .nth(0)
    .getByLabel("Notiz (optional)")
    .fill("Erste Strophe im Stehchoral.");
  await page
    .locator(".programm-zeile")
    .nth(1)
    .getByLabel("Notiz (optional)")
    .fill("Zweite Strophe sitzend.");

  await page.getByRole("button", { name: "Entwurf speichern" }).click();
  await expect(page.getByText("Programmentwurf gespeichert.")).toBeVisible();
  expect(puts).toHaveLength(1);
  // Ganzer geordneter Ersatz: die Reihenfolge der Zeilen setzt die
  // Positionen; neue Einträge tragen keine Id.
  expect(puts[0].items).toEqual([
    {
      songId: lied1Id,
      musicalVersionId: standardId,
      note: "Erste Strophe im Stehchoral.",
    },
    {
      songId: lied2Id,
      musicalVersionId: lied2FassungId,
      note: "Zweite Strophe sitzend.",
    },
    { songId: lied1Id, musicalVersionId: transponiertId, note: "" },
  ]);

  // Der frische Stand kommt mit drei stabilen Ids zurück; die unver-
  // änderten Einträge tragen sie beim nächsten Speichern unverändert in
  // die Anfrage (zweiter PUT-Rundlauf).
  await expect(page.locator(".programm-zeile")).toHaveCount(3);
  await page.getByRole("button", { name: "Entwurf speichern" }).click();
  await expect(puts).toHaveLength(2);
  expect(puts[1].items).toEqual([
    {
      id: punktA,
      songId: lied1Id,
      musicalVersionId: standardId,
      note: "Erste Strophe im Stehchoral.",
    },
    {
      id: punktB,
      songId: lied2Id,
      musicalVersionId: lied2FassungId,
      note: "Zweite Strophe sitzend.",
    },
    {
      id: punktC,
      songId: lied1Id,
      musicalVersionId: transponiertId,
      note: "",
    },
  ]);
  expect(puts[1].rowVersion).toBe(4);

  expect(errors).toEqual([]);
});

test("Redaktion legt auf einem auftrittslosen Programm mit an: erster PUT ohne rowVersion", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  let stand: unknown = null;
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(stand))),
  );

  const puts: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/programme/items`, (route) => {
    puts.push(route.request().postDataJSON() as Record<string, unknown>);
    // Der erste PUT legt das Programm still an; die Antwort trägt die
    // frische Einbettung mit Server-Ids.
    stand = entwurfStandMitEinem();
    return route.fulfill(json({ programme: stand }));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  await page.getByLabel("Lied aus dem Katalog suchen").fill("Aurora");
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".programm-lied-treffer")
    .getByRole("button", { name: "Aurora" })
    .click();
  await page
    .locator(".programm-zeile")
    .nth(0)
    .getByRole("button", { name: /Standardfassung/ })
    .click();

  await page.getByRole("button", { name: "Entwurf speichern" }).click();
  await expect(page.getByText("Programmentwurf gespeichert.")).toBeVisible();
  expect(puts).toHaveLength(1);
  // Ohne Programm entfällt die rowVersion; der Server legt still an.
  expect(puts[0].rowVersion).toBeUndefined();
  expect(puts[0].items).toEqual([
    { songId: lied2Id, musicalVersionId: lied2FassungId, note: "" },
  ]);

  // Der frische Stand (Programm + Entwurf) kommt über die erneute
  // Auftrittsabfrage; die Werkbank bleibt aufgeklappt und zeigt ihn.
  await expect(page.locator(".programm-zeile")).toHaveCount(1);
  await expect(
    page.locator(".programm-zeile").getByText("Aurora"),
  ).toBeVisible();
  // Veröffentlicht ist noch nichts: der Lesesaal bleibt ehrlich leer.
  await expect(page.locator(".programm-liste")).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Veralteter Stand erklärt sich mit der Vertragssprache und lädt den frischen Stand", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  // Kommt der Stand nach dem Speichern hoch, trägt er die fremde Notiz:
  // ein anderer Beitrag hat zwischenzeitlich geändert.
  const frischerStand = {
    id: programmId,
    rowVersion: 9,
    working: {
      id: revisionEntwurfId,
      number: 1,
      updatedAt: "2026-09-24T10:00:00.000Z",
      items: [
        veroeffentlichteItems[0],
        {
          ...veroeffentlichteItems[1],
          note: "Vom anderen Beitrag ergänzt.",
        },
      ],
    },
    published: null,
  };
  let abfragen = 0;
  await page.route(`**/api/events/${eventId}`, (route) => {
    abfragen += 1;
    return route.fulfill(
      json(detail(abfragen === 1 ? entwurfStandMitDrei() : frischerStand)),
    );
  });
  const puts: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/programme/items`, (route) => {
    puts.push(route.request().postDataJSON() as Record<string, unknown>);
    return route.fulfill(
      problem("Der Programmentwurf wurde zwischenzeitlich geändert.", 409),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  await expect(page.locator(".programm-zeile")).toHaveCount(3);

  await page.getByRole("button", { name: "Entwurf speichern" }).click();
  await expect(
    page.getByText(
      "Der Programmentwurf wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.",
    ),
  ).toBeVisible();
  // Der frische Stand kommt nach; die Werkzeilen folgen dem Server-Stand
  // (hier: ein Eintrag mit der fremden Notiz).
  await expect(
    page.locator(".programm-zeile").nth(1).getByLabel("Notiz (optional)"),
  ).toHaveValue("Vom anderen Beitrag ergänzt.");
  expect(abfragen).toBe(2);
  expect(puts).toHaveLength(1);

  expect(errors).toEqual([]);
});

test("Veröffentlichen sendet die rowVersion und meldet den Erfolg; das Verzeichnis listet das Programm", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  let abfragen = 0;
  await page.route(`**/api/events/${eventId}`, (route) => {
    abfragen += 1;
    return route.fulfill(
      json(detail(abfragen === 1 ? entwurfStandMitDrei() : veroeffentlicht)),
    );
  });
  const veroeffentlichungen: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/programme/publish`, (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    veroeffentlichungen.push(
      route.request().postDataJSON() as Record<string, unknown>,
    );
    return route.fulfill(json({ programme: veroeffentlicht }));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await programmVerwaltungAufklappen(page);
  await page.getByRole("button", { name: "Veröffentlichen" }).click();
  await expect(
    page.getByText("Programm veröffentlicht. Mitglieder sehen es ab sofort."),
  ).toBeVisible();
  expect(veroeffentlichungen).toEqual([{ rowVersion: 4 }]);
  // Nach der Veröffentlichung liest sich die veröffentlichte Revision.
  await expect(page.locator(".programm-liste .programm-eintrag")).toHaveCount(
    3,
  );

  // Das Verzeichnis listet das kommende Programm mit ehrlichem Datum,
  // Liederzahl und dem Weg in die Auftrittsansicht.
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
            publishedAt: "2026-09-20T10:00:00.000Z",
            itemCount: 3,
          },
        ],
      }),
    ),
  );

  if (page.viewportSize()?.width === 412) {
    await page.getByRole("button", { name: "Menü", exact: true }).click();
  }
  await page.getByRole("link", { name: "Programme" }).click();
  await expect(page).toHaveURL(/\/programm\/$/);
  const zeile = page.locator(".programme-eintrag");
  await expect(zeile).toHaveCount(1);
  await expect(zeile.getByText("Frühlingskonzert")).toBeVisible();
  await expect(zeile.getByText("Konzert", { exact: true })).toBeVisible();
  await expect(zeile.getByText("12. Mai 2027")).toBeVisible();
  await expect(zeile.getByText("Stadtsaal Mining")).toBeVisible();
  await expect(zeile.getByText("3 Lieder")).toBeVisible();
  await expect(zeile.getByRole("link")).toHaveAttribute(
    "href",
    `/auftritt/?id=${eventId}`,
  );
  await expect(zeile.locator(".datum-unsicher")).toHaveCount(0);
  // Auch im Handymaß bleibt das Verzeichnis ohne Seitenüberlauf.
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  // Jede Navigation schließt das Handymenü; der Nav-Link liest sich erst
  // nach dem erneuten Öffnen.
  if (page.viewportSize()?.width === 412) {
    await page.getByRole("button", { name: "Menü", exact: true }).click();
  }
  await expect(
    page
      .getByRole("navigation", { name: "Hauptnavigation" })
      .getByRole("link", { name: "Programme" }),
  ).toHaveAttribute("href", "/programm/");

  expect(errors).toEqual([]);
});
