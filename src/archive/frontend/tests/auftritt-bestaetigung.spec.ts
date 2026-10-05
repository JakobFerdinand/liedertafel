import { expect, type Page, test } from "@playwright/test";

// ARC-029: tatsächlich gesungenes Programm — der Lesesaal der Mitglieder
// (Plan und Aufführung getrennt, abweichende Fassung, Zugabe, ausgefallene
// Programmpunkte, ehrliches „noch nicht bestätigt“) und die Werkbank der
// Redaktion (Ein-Klick-Bestätigung, ausdrückliche Entscheidung je
// Programmpunkt, Zugabe mit Client-Schlüssel, veralteter Stand). Die
// Rezepturen folgen tests/auftritt-belege.spec.ts: feste Fixture-Ids,
// geroutete Antworten, Anspruch an die Vertragssprache.

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

const eventId = "00000000-0000-0000-0000-00000000e090";
const programmId = "00000000-0000-0000-0000-00000000e091";
const revisionId = "00000000-0000-0000-0000-00000000e093";
const lied1Id = "00000000-0000-0000-0000-0000000000f1";
const lied2Id = "00000000-0000-0000-0000-0000000000f2";
const arrangement1Id = "00000000-0000-0000-0000-00000000c0f1";
const arrangement2Id = "00000000-0000-0000-0000-00000000c0f2";
const standardId = "00000000-0000-0000-0000-00000000d0f1";
const transponiertId = "00000000-0000-0000-0000-00000000d0f3";
const lied2FassungId = "00000000-0000-0000-0000-00000000d0f2";
const punktA = "00000000-0000-0000-0000-00000000b0f1";
const punktB = "00000000-0000-0000-0000-00000000b0f2";
const nachweisA = "00000000-0000-0000-0000-00000000a0f1";
const nachweisZugabe = "00000000-0000-0000-0000-00000000a0f9";

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

function song(
  id: string,
  title: string,
  arrangementId: string,
  versionen: Array<{ id: string; label: string; key: string }>,
) {
  return {
    song: {
      id,
      title,
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
          id: arrangementId,
          label: "Satz für gemischten Chor",
          arranger: null,
          voiceConfiguration: "SATB",
          accompaniment: null,
          musicalVersions: versionen.map((fassung) => ({
            id: fassung.id,
            label: fassung.label,
            creator: null,
            musicalKey: fassung.key,
            assets: [],
          })),
        },
      ],
    },
  };
}

async function mockKatalog(page: Page) {
  await page.route(/\/api\/songs\?.*/, (route) =>
    route.fulfill(
      json({
        query: "",
        page: 1,
        pageSize: 20,
        total: 1,
        songs: [
          {
            id: lied2Id,
            title: "Zugabe-Lied",
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
      json(
        song(lied1Id, "Das Wandern", arrangement1Id, [
          { id: standardId, label: "Standardfassung", key: "G-Dur" },
          { id: transponiertId, label: "Tiefe Tonart", key: "F-Dur" },
        ]),
      ),
    ),
  );
  await page.route(`**/api/songs/${lied2Id}`, (route) =>
    route.fulfill(
      json(
        song(lied2Id, "Zugabe-Lied", arrangement2Id, [
          { id: lied2FassungId, label: "Standardfassung", key: "C-Dur" },
        ]),
      ),
    ),
  );
}

const fassung1 = {
  songId: lied1Id,
  songTitle: "Das Wandern",
  arrangementId: arrangement1Id,
  musicalVersionId: standardId,
  arrangementLabel: "Satz für gemischten Chor",
  voiceConfiguration: "SATB",
  musicalVersionLabel: "Standardfassung",
  musicalKey: "G-Dur",
};

const fassung2 = {
  songId: lied2Id,
  songTitle: "Zugabe-Lied",
  arrangementId: arrangement2Id,
  musicalVersionId: lied2FassungId,
  arrangementLabel: "Satz für gemischten Chor",
  voiceConfiguration: "SATB",
  musicalVersionLabel: "Standardfassung",
  musicalKey: "C-Dur",
};

function planPunkt(
  id: string,
  position: number,
  fassung: typeof fassung1,
  extra: Record<string, unknown> = {},
) {
  return { id, position, note: null, ...fassung, ...extra };
}

const veroeffentlicht = {
  id: revisionId,
  number: 2,
  publishedAt: "2026-09-20T10:00:00.000Z",
  items: [planPunkt(punktA, 1, fassung1), planPunkt(punktB, 2, fassung2)],
};

function detail(confirmation: unknown) {
  return {
    event: {
      id: eventId,
      kind: "concert",
      title: "Jahreskonzert",
      venue: "Stadtsaal Mining",
      dateYear: 2026,
      dateMonth: 5,
      dateDay: 12,
      dateApproximate: false,
      datePrecision: "day",
      dateDisplay: "12. Mai 2026",
      startTime: "19:30",
      published: true,
      notes: null,
      sourceNote: null,
      documents: [],
      createdAt: "2026-09-01T08:00:00.000Z",
      updatedAt: "2026-09-24T10:00:00.000Z",
      publishedAt: "2026-09-20T10:00:00.000Z",
      programme: {
        id: programmId,
        rowVersion: 4,
        working: null,
        published: veroeffentlicht,
        history: [],
        confirmation,
      },
      performances: [],
    },
  };
}

// Bestätigung: Punkt 1 in anderer Fassung gesungen, Punkt 2 ausgefallen,
// dazu eine Zugabe.
const bestaetigung = {
  revisionId,
  revisionNumber: 2,
  confirmedAt: "2026-09-25T20:00:00.000Z",
  updatedAt: "2026-09-25T20:00:00.000Z",
  upToDate: true,
  actual: [
    {
      ...fassung1,
      performanceId: nachweisA,
      programmeItemId: punktA,
      added: false,
      differsFromPlan: true,
      plannedMusicalVersionLabel: "Standardfassung",
      musicalVersionId: transponiertId,
      musicalVersionLabel: "Tiefe Tonart",
      musicalKey: "F-Dur",
      evidenceStatus: "confirmed",
    },
    {
      ...fassung2,
      songTitle: "Die Zugabe",
      performanceId: nachweisZugabe,
      programmeItemId: null,
      added: true,
      differsFromPlan: false,
      plannedMusicalVersionLabel: null,
      evidenceStatus: "confirmed",
    },
  ],
  skipped: [
    {
      ...fassung2,
      programmeItemId: punktB,
      position: 2,
    },
  ],
};

function pruefung(
  rowVersion: number,
  items: unknown[],
  confirmation: unknown = null,
  additions: unknown[] = [],
) {
  return {
    review: {
      revision: {
        id: revisionId,
        number: 2,
        publishedAt: "2026-09-20T10:00:00.000Z",
      },
      currentRevisionId: revisionId,
      upToDate: true,
      confirmation,
      rowVersion,
      items,
      additions,
    },
  };
}

function offenerPunkt(id: string, position: number, fassung: typeof fassung1) {
  return {
    programmeItemId: id,
    position,
    note: null,
    ...fassung,
    outcome: "open",
    performance: null,
    suggestedPerformanceId: null,
  };
}

const offeneItems = [
  offenerPunkt(punktA, 1, fassung1),
  offenerPunkt(punktB, 2, fassung2),
];

function nachweis(
  id: string,
  fassung: typeof fassung1,
  programmeItemId: string | null,
) {
  return {
    ...fassung,
    id,
    evidenceStatus: "confirmed",
    programmeItemId,
    rowVersion: 1,
  };
}

test("Mitglied liest ohne Bestätigung ehrlich: Plan ist nicht Aufführung", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(null))),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const abschnitt = page.locator("section.auftritt-bestaetigung");
  await expect(
    abschnitt.getByRole("heading", { name: "Tatsächlich gesungen" }),
  ).toBeVisible();
  await expect(abschnitt).toContainText("Noch nicht bestätigt.");
  await expect(abschnitt).toContainText("Das Programm oben ist der Plan");
  // Die Werkbank bleibt dem Mitglied verborgen.
  await expect(page.locator("details.bestaetigung-verwaltung")).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("Mitglied unterscheidet Plan und Aufführung: andere Fassung, Zugabe, ausgefallen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(bestaetigung))),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const abschnitt = page.locator("section.auftritt-bestaetigung");
  await expect(abschnitt).toContainText("Bestätigt am");
  await expect(abschnitt).toContainText("Grundlage: Revision 2 des Programms");
  const eintraege = abschnitt.locator(".bestaetigung-ist .programm-eintrag");
  await expect(eintraege).toHaveCount(2);
  await expect(eintraege.nth(0)).toContainText("Das Wandern");
  await expect(eintraege.nth(0)).toContainText(
    "Andere Fassung als geplant (geplant: Standardfassung)",
  );
  await expect(eintraege.nth(0)).toContainText("Tiefe Tonart");
  // Tiefer Leseport auf die tatsächlich gesungene Fassung.
  await expect(eintraege.nth(0).getByRole("link")).toHaveAttribute(
    "href",
    `/lied/?id=${lied1Id}&fassung=${arrangement1Id}&version=${transponiertId}`,
  );
  await expect(eintraege.nth(1)).toContainText("Die Zugabe");
  await expect(eintraege.nth(1)).toContainText("Zusätzlich gesungen");
  // Ausgefallene Programmpunkte stehen getrennt.
  await expect(abschnitt.locator(".bestaetigung-ausgefallen")).toContainText(
    "Geplant, aber nicht gesungen",
  );
  await expect(abschnitt.locator(".bestaetigung-ausgefallen")).toContainText(
    "Zugabe-Lied",
  );
  // Der Plan oben bleibt unverändert zweizeilig.
  await expect(
    page.locator("section.auftritt-programm .programm-eintrag"),
  ).toHaveCount(2);
  await expect(page.locator("details.bestaetigung-verwaltung")).toHaveCount(0);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  expect(errors).toEqual([]);
});

test("Redaktion bestätigt das Programm mit einem Klick unverändert", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(null))),
  );
  await page.route(
    `**/api/events/${eventId}/programme/confirmation`,
    (route) => {
      if (route.request().method() === "GET")
        return route.fulfill(json(pruefung(0, offeneItems)));
      expect(route.request().method()).toBe("PUT");
      expect(route.request().headers()["x-csrf-token"]).toBe("test");
      puts.push(route.request().postDataJSON());
      return route.fulfill(
        json(
          pruefung(
            1,
            [
              {
                ...offeneItems[0],
                outcome: "sung",
                performance: nachweis(nachweisA, fassung1, punktA),
              },
              {
                ...offeneItems[1],
                outcome: "sung",
                performance: nachweis("a2", fassung2, punktB),
              },
            ],
            {
              id: "c1",
              revisionId,
              revisionNumber: 2,
              confirmedAt: "2026-09-25T20:00:00.000Z",
              updatedAt: "2026-09-25T20:00:00.000Z",
            },
          ),
        ),
      );
    },
  );
  const puts: unknown[] = [];

  await page.goto(`/auftritt/?id=${eventId}`);
  await page.locator("details.bestaetigung-verwaltung > summary").click();
  await expect(page.getByText("Noch nicht bestätigt.").first()).toBeVisible();
  await page
    .getByRole("button", { name: "Programm unverändert bestätigen" })
    .click();
  await expect(page.getByText("Bestätigung gespeichert.")).toBeVisible();
  expect(puts).toEqual([
    {
      revisionId,
      rowVersion: 0,
      items: [
        { programmeItemId: punktA, outcome: "sung" },
        { programmeItemId: punktB, outcome: "sung" },
      ],
      additions: [],
    },
  ]);
  // Nach der Bestätigung gibt es die Ein-Klick-Aktion nicht mehr.
  await expect(
    page.getByRole("button", { name: "Programm unverändert bestätigen" }),
  ).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("Redaktion entscheidet ausdrücklich: ausgefallen, andere Fassung und Zugabe", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(null))),
  );
  const puts: Array<Record<string, unknown>> = [];
  await page.route(
    `**/api/events/${eventId}/programme/confirmation`,
    (route) => {
      if (route.request().method() === "GET")
        return route.fulfill(json(pruefung(0, offeneItems)));
      puts.push(route.request().postDataJSON());
      return route.fulfill(json(pruefung(1, offeneItems)));
    },
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  await page.locator("details.bestaetigung-verwaltung > summary").click();
  const zeilen = page.locator(".bestaetigung-zeile");
  await expect(zeilen).toHaveCount(2);

  // Nichts wird still angenommen: ohne Entscheidung kein Absenden.
  await page.getByRole("button", { name: "Bestätigung speichern" }).click();
  await expect(
    page.getByText(
      "Bitte gib für jeden Programmpunkt an, ob er gesungen wurde.",
    ),
  ).toBeVisible();
  expect(puts).toHaveLength(0);

  // Punkt 1 gesungen — in der tiefen Tonart; Punkt 2 nicht gesungen.
  await zeilen.nth(0).getByLabel("Gesungen", { exact: true }).check();
  await zeilen
    .nth(0)
    .getByRole("button", { name: "Andere Fassung gesungen" })
    .click();
  await zeilen
    .nth(0)
    .getByRole("button", { name: /Tiefe Tonart/ })
    .click();
  await expect(zeilen.nth(0)).toContainText("Gesungen: Tiefe Tonart");
  await zeilen.nth(1).getByLabel("Nicht gesungen").check();

  // Zugabe aus dem Katalog.
  await page.getByLabel("Zusätzlich gesungenes Lied suchen").fill("Zugabe");
  await page.getByRole("button", { name: "Suchen" }).last().click();
  await page
    .locator(".programm-lied-treffer")
    .getByRole("button", { name: "Zugabe-Lied" })
    .click();
  await expect(page.locator(".bestaetigung-zusatz")).toHaveCount(1);

  await page.getByRole("button", { name: "Bestätigung speichern" }).click();
  await expect(page.getByText("Bestätigung gespeichert.")).toBeVisible();
  expect(puts).toHaveLength(1);
  const gesendet = puts[0] as {
    items: unknown[];
    additions: Array<{ clientKey?: string; songId: string }>;
  };
  expect(gesendet).toMatchObject({
    revisionId,
    rowVersion: 0,
    items: [
      {
        programmeItemId: punktA,
        outcome: "sung",
        musicalVersionId: transponiertId,
      },
      { programmeItemId: punktB, outcome: "skipped" },
    ],
  });
  expect(gesendet.additions).toHaveLength(1);
  expect(gesendet.additions[0].songId).toBe(lied2Id);
  expect(gesendet.additions[0].clientKey).toMatch(/^[0-9a-f-]{36}$/);
  expect(errors).toEqual([]);
});

test("Veralteter Stand erklärt sich und lädt die Prüfansicht neu", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(null))),
  );
  let gets = 0;
  await page.route(
    `**/api/events/${eventId}/programme/confirmation`,
    (route) => {
      if (route.request().method() === "GET") {
        gets += 1;
        return route.fulfill(json(pruefung(0, offeneItems)));
      }
      return route.fulfill(
        problem(
          "Das Programm wurde zwischenzeitlich neu veröffentlicht. Bitte lade die Bestätigung neu.",
          409,
        ),
      );
    },
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  await page.locator("details.bestaetigung-verwaltung > summary").click();
  await page
    .getByRole("button", { name: "Programm unverändert bestätigen" })
    .click();
  await expect(
    page.getByText(
      "Das Programm wurde zwischenzeitlich neu veröffentlicht. Bitte lade die Bestätigung neu. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.",
    ),
  ).toBeVisible();
  // Der frische Stand wurde nachgeladen (zweites GET).
  await expect.poll(() => gets).toBeGreaterThanOrEqual(2);
  expect(errors).toEqual([]);
});
