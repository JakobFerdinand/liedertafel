import { expect, type Page, test } from "@playwright/test";

// ARC-031: Aufführungsgeschichte auf der Liedseite — bestätigte
// Aufführungen und Programmangaben getrennt gezählt (mit Vorbehalt „nur
// erfasste Überlieferung“), unsichere Daten, unbekannte Fassung,
// Wiederholungen und mögliche Doppelerfassungen sichtbar, Filter und
// Blättern gegen die echte Vertragsform (tests/archive/backend/
// SongHistoryApiTests.cs). Rezeptur wie tests/auftritt-bestaetigung.spec.ts.

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

const liedId = "00000000-0000-0000-0000-0000000000f1";
const arrangementId = "00000000-0000-0000-0000-00000000c0f1";
const arrangement2Id = "00000000-0000-0000-0000-00000000c0f2";
const standardId = "00000000-0000-0000-0000-00000000d0f1";
const auftritt1 = "00000000-0000-0000-0000-00000000e001";
const auftritt2 = "00000000-0000-0000-0000-00000000e002";
const auftritt3 = "00000000-0000-0000-0000-00000000e003";
const auftritt4 = "00000000-0000-0000-0000-00000000e004";

function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
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
  await page.route(`**/api/songs/${liedId}`, (route) =>
    route.fulfill(
      json({
        song: {
          id: liedId,
          title: "Das Wandern",
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
              musicalVersions: [
                {
                  id: standardId,
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

type Zeile = Record<string, unknown>;

function zeile(
  id: string,
  eventId: string,
  titel: string,
  extra: Zeile = {},
): Zeile {
  return {
    id,
    eventId,
    eventTitle: titel,
    eventKind: "concert",
    eventPublished: true,
    dateYear: 1975,
    dateMonth: 6,
    dateDay: 1,
    dateApproximate: false,
    dateDisplay: "1. Juni 1975",
    datePrecision: "day",
    dateUncertain: false,
    evidenceStatus: "confirmed",
    origin: "record",
    arrangement: {
      id: arrangementId,
      label: "Satz für gemischten Chor",
    },
    musicalVersion: { id: standardId, label: "Standardfassung" },
    occurrence: { index: 1, of: 1 },
    possiblyDuplicate: false,
    alsoConfirmedAtEvent: false,
    ...extra,
  };
}

const zaehlung = {
  confirmed: {
    occurrences: 3,
    events: 2,
    uncertainDates: 1,
    possiblyDuplicate: 1,
  },
  unconfirmed: {
    occurrences: 2,
    events: 2,
    onlyEvents: 1,
    uncertainDates: 1,
  },
  draftEventOccurrences: null,
};

function historie(zeilen: Zeile[], extra: Zeile = {}) {
  return {
    song: { id: liedId, title: "Das Wandern", published: true },
    page: 1,
    pageSize: 20,
    total: zeilen.length,
    counts: zaehlung,
    arrangements: [
      {
        id: arrangementId,
        label: "Satz für gemischten Chor",
        confirmed: 2,
        unconfirmed: 1,
      },
      {
        id: arrangement2Id,
        label: "Satz für Männerchor",
        confirmed: 0,
        unconfirmed: 0,
      },
    ],
    unknownArrangement: { confirmed: 1, unconfirmed: 1 },
    performances: zeilen,
    ...extra,
  };
}

const gemischteZeilen = [
  zeile("p1", auftritt1, "Frühjahrskonzert", {
    evidenceStatus: "mention",
    alsoConfirmedAtEvent: true,
    occurrence: null,
  }),
  zeile("p2", auftritt1, "Frühjahrskonzert", {
    origin: "programme",
    occurrence: { index: 1, of: 2 },
  }),
  zeile("p3", auftritt1, "Frühjahrskonzert", {
    occurrence: { index: 2, of: 2 },
    possiblyDuplicate: true,
  }),
  zeile("p4", auftritt2, "Sommerfest", {
    eventKind: "festival",
    dateYear: 1960,
    dateMonth: null,
    dateDay: null,
    dateApproximate: true,
    dateDisplay: "um 1960",
    datePrecision: "year",
    dateUncertain: true,
    evidenceStatus: "mention",
    arrangement: null,
    musicalVersion: null,
    occurrence: null,
  }),
  zeile("p5", auftritt3, "Gottesdienst ohne Datum", {
    eventKind: "service",
    dateYear: null,
    dateMonth: null,
    dateDay: null,
    dateDisplay: "Datum unbekannt",
    datePrecision: "unknown",
    dateUncertain: true,
    arrangement: null,
    musicalVersion: null,
  }),
];

async function mockHistorie(
  page: Page,
  antwort: (url: URL) => { status?: number; body: unknown },
) {
  const anfragen: string[] = [];
  await page.route(
    new RegExp(`/api/songs/${liedId}/performances(\\?.*)?$`),
    (route) => {
      const url = new URL(route.request().url());
      anfragen.push(url.search);
      const stand = antwort(url);
      return route.fulfill(json(stand.body, stand.status ?? 200));
    },
  );
  return anfragen;
}

test("Mitglieder sehen getrennte, ehrlich begrenzte Zahlen und die Zeilen", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await mockHistorie(page, () => ({ body: historie(gemischteZeilen) }));
  await page.goto(`/lied/?id=${liedId}`);

  const abschnitt = page.getByRole("region", {
    name: "Aufführungsgeschichte",
  });
  await expect(abschnitt).toBeVisible();
  await expect(abschnitt).toContainText(
    "Sie sind keine vollständige Chronik der Aufführungen.",
  );
  // Zwei getrennte Zahlen, nie summiert.
  const summen = abschnitt.locator(".historie-summen");
  await expect(summen).toContainText("Bestätigt");
  await expect(summen).toContainText("3 Aufführungen bei 2 Auftritten");
  await expect(summen).toContainText("Unbestätigt");
  await expect(summen).toContainText("2 Programmangaben bei 2 Auftritten");
  await expect(summen).toContainText("Davon 1 möglicherweise doppelt erfasst");
  await expect(summen).toContainText(
    "An 1 Auftritt liegt nur eine Programmangabe vor",
  );
  await expect(summen).not.toContainText(/5 (Aufführungen|Nachweise|Einträge)/);

  const eintraege = abschnitt.locator(".historie-eintrag");
  await expect(eintraege).toHaveCount(5);
  // Auftrittslink je Zeile.
  await expect(
    eintraege.nth(0).getByRole("link", { name: "Frühjahrskonzert" }),
  ).toHaveAttribute("href", `/auftritt/?id=${auftritt1}`);
  // Programmangabe mit bestätigtem Auftritt.
  await expect(eintraege.nth(0)).toContainText("Programmangabe");
  await expect(eintraege.nth(0)).toContainText(
    "gehört vermutlich zur selben Aufführung",
  );
  // Wiederholung und Programmherkunft.
  await expect(eintraege.nth(1)).toContainText("Im Programm bestätigt");
  await expect(eintraege.nth(1)).toContainText("Aufführung 1 von 2");
  await expect(eintraege.nth(2)).toContainText("Aufführung 2 von 2");
  await expect(eintraege.nth(2)).toContainText("doppelt erfasst");
  // Unsicheres Datum und unbekannte Fassung.
  await expect(eintraege.nth(3)).toContainText("um 1960");
  await expect(eintraege.nth(3)).toContainText("Datum unsicher");
  await expect(eintraege.nth(3)).toContainText("Fassung unbekannt");
  await expect(eintraege.nth(4)).toContainText("Datum unbekannt");
  // Bekannte Fassung als Kette.
  await expect(eintraege.nth(1)).toContainText(
    "Satz für gemischten Chor · Standardfassung",
  );
  // Mitglieder sehen weder Quellenangaben noch Entwurfshinweise noch einen
  // erfundenen Aufnahmen-Platz.
  await expect(abschnitt).not.toContainText("Quellenangabe");
  await expect(abschnitt).not.toContainText("nicht veröffentlicht");
  await expect(abschnitt.locator(".historie-erweiterung")).toHaveCount(0);
  await expect(eintraege.nth(1)).toHaveAttribute("data-performance-id", "p2");

  // Kein horizontaler Überlauf (Handy-Projekt inklusive).
  const ueberlauf = await page.evaluate(
    () =>
      document.documentElement.scrollWidth >
      document.documentElement.clientWidth,
  );
  expect(ueberlauf).toBe(false);
});

test("Filter fragen den Server und blättern zurück auf Seite 1", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  const anfragen = await mockHistorie(page, (url) => {
    const evidenz = url.searchParams.get("evidence");
    const fassung = url.searchParams.get("arrangementId");
    const seite = Number(url.searchParams.get("page") ?? "1");
    if (evidenz === "mention" && fassung === "unknown") {
      return { body: historie([gemischteZeilen[3]]) };
    }
    if (evidenz === "mention") {
      return {
        body: historie([gemischteZeilen[0], gemischteZeilen[3]]),
      };
    }
    // Zwei Seiten, wenn ungefiltert.
    return {
      body: historie(gemischteZeilen.slice(0, seite === 2 ? 1 : 2), {
        page: seite,
        pageSize: 2,
        total: 3,
      }),
    };
  });
  await page.goto(`/lied/?id=${liedId}`);
  const abschnitt = page.getByRole("region", {
    name: "Aufführungsgeschichte",
  });
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(2);
  await expect(abschnitt).toContainText("Seite 1 von 2");

  await abschnitt.getByRole("button", { name: "Weiter" }).click();
  await expect(abschnitt).toContainText("Seite 2 von 2");
  expect(anfragen.at(-1)).toBe("?page=2");

  await abschnitt.getByLabel("Nachweis").selectOption("mention");
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(2);
  expect(anfragen.at(-1)).toBe("?evidence=mention");
  await expect(abschnitt).not.toContainText("Seite 2 von");

  await abschnitt.getByLabel("Fassung").selectOption("unknown");
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(1);
  expect(anfragen.at(-1)).toBe("?evidence=mention&arrangementId=unknown");
  await expect(abschnitt.locator(".historie-eintrag")).toContainText(
    "Fassung unbekannt",
  );
});

test("Ein Lied ohne Nachweise sagt ehrlich, dass nichts erfasst ist", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await mockHistorie(page, () => ({
    body: historie([], {
      counts: {
        confirmed: {
          occurrences: 0,
          events: 0,
          uncertainDates: 0,
          possiblyDuplicate: 0,
        },
        unconfirmed: {
          occurrences: 0,
          events: 0,
          onlyEvents: 0,
          uncertainDates: 0,
        },
        draftEventOccurrences: null,
      },
      arrangements: [],
      unknownArrangement: { confirmed: 0, unconfirmed: 0 },
    }),
  }));
  await page.goto(`/lied/?id=${liedId}`);
  const abschnitt = page.getByRole("region", {
    name: "Aufführungsgeschichte",
  });
  await expect(abschnitt).toContainText(
    "Für dieses Lied sind noch keine Aufführungen oder Programmangaben erfasst.",
  );
  await expect(abschnitt).toContainText(
    "Das heißt nicht, dass es nie gesungen wurde.",
  );
  await expect(abschnitt.locator(".historie-summen")).toHaveCount(0);
  await expect(abschnitt.getByLabel("Nachweis")).toHaveCount(0);
});

test("Die Redaktion sieht Quellenangaben und Entwurfszeilen ohne Zählung", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await mockHistorie(page, () => ({
    body: historie(
      [
        zeile("p1", auftritt4, "Entwurfsauftritt", {
          eventPublished: false,
          evidenceStatus: "mention",
          occurrence: null,
          sourceNote: "Programmzettel 1975",
        }),
        zeile("p2", auftritt1, "Frühjahrskonzert", {
          sourceNote: null,
        }),
      ],
      {
        counts: { ...zaehlung, draftEventOccurrences: 1 },
      },
    ),
  }));
  await page.goto(`/lied/?id=${liedId}`);
  const abschnitt = page.getByRole("region", {
    name: "Aufführungsgeschichte",
  });
  await expect(abschnitt).toContainText(
    "1 Nachweis an unveröffentlichten Auftritten sind in keiner Zahl enthalten.",
  );
  const eintraege = abschnitt.locator(".historie-eintrag");
  await expect(eintraege.nth(0)).toContainText(
    "Auftritt noch nicht veröffentlicht, in keiner Zahl enthalten.",
  );
  await expect(eintraege.nth(0)).toContainText(
    "Quellenangabe: Programmzettel 1975",
  );
  await expect(eintraege.nth(1)).not.toContainText("Quellenangabe");
});

test("Ein Ladefehler bleibt im Abschnitt und lässt sich wiederholen", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  let versuche = 0;
  await mockHistorie(page, () => {
    versuche += 1;
    if (versuche === 1) {
      return { status: 500, body: { title: "Fehler" } };
    }
    return { body: historie(gemischteZeilen) };
  });
  await page.goto(`/lied/?id=${liedId}`);
  // Das Lied selbst bleibt benutzbar.
  await expect(
    page.getByRole("heading", { name: "Das Wandern" }),
  ).toBeVisible();
  const abschnitt = page.getByRole("region", {
    name: "Aufführungsgeschichte",
  });
  await expect(abschnitt.getByRole("alert")).toContainText(
    "konnte nicht geladen werden",
  );
  await abschnitt.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(abschnitt.locator(".historie-eintrag")).toHaveCount(5);
});
