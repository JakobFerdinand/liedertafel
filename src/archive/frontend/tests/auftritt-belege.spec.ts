import { expect, type Page, test } from "@playwright/test";

// ARC-028: Aufführungsnachweise des Auftritts — der Lesesaal der
// Mitglieder (Titel, Bestätigungsstand, Fassungskette, ohne
// Quellenangabe) und die Werkbank der Redaktion (Erfassen mit
// Wiederholungsschlüssel, Bearbeiten mit rowVersion, Löschen mit
// einmaliger Bestätigung in der Zeile). Die Rezepturen folgen
// tests/programm.spec.ts: feste Fixture-Ids, geroutete Antworten mit
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

const eventId = "00000000-0000-0000-0000-00000000e040";
const lied1Id = "00000000-0000-0000-0000-0000000000b1";
const lied2Id = "00000000-0000-0000-0000-0000000000b2";
const lied2FassungId = "00000000-0000-0000-0000-00000000d0b2";
const lied2ArrangementId = "00000000-0000-0000-0000-00000000c0b2";
const arrangement1Id = "00000000-0000-0000-0000-00000000c0b1";
const standardId = "00000000-0000-0000-0000-00000000d0b1";
const nachweisAId = "00000000-0000-0000-0000-00000000f0a1";
const nachweisBId = "00000000-0000-0000-0000-00000000f0a2";

// Nachweis A: bestätigte Aufführung mit bekannter Kette (Standardfassung
// des gemischten Satzes) und ohne Quellenangabe — der ehrliche Leerstand.
// Nachweis B: unverifizierte Programmangabe ohne Kette („Fassung
// unbekannt") mit Pflichtquellenangabe.
function nachweisA(aenderungen: Record<string, unknown> = {}) {
  return {
    id: nachweisAId,
    eventId,
    songId: lied1Id,
    arrangementId: arrangement1Id,
    musicalVersionId: standardId,
    position: 1,
    evidenceStatus: "confirmed",
    sourceNote: null,
    capturedAt: "2026-09-21T12:00:00.000Z",
    createdAt: "2026-09-20T12:00:00.000Z",
    updatedAt: "2026-09-21T12:00:00.000Z",
    rowVersion: 5,
    ...aenderungen,
  };
}

function nachweisB(aenderungen: Record<string, unknown> = {}) {
  return {
    id: nachweisBId,
    eventId,
    songId: lied2Id,
    arrangementId: null,
    musicalVersionId: null,
    position: 2,
    evidenceStatus: "mention",
    sourceNote: "Programmblatt im Stadtarchiv.",
    capturedAt: "2026-09-22T09:30:00.000Z",
    createdAt: "2026-09-22T09:30:00.000Z",
    updatedAt: "2026-09-22T09:30:00.000Z",
    rowVersion: 2,
    ...aenderungen,
  };
}

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

// Lieddetails für Titel- und Kettenauflösung; dieselben Lieder dient
// die Katalogsuche der Erfassung.
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

// Auftrittsdetails mit stiller Programmeinbettung (der Abschnitt bleibt
// hier unbeantwortet) und den Nachweisen nach `programme`.
function detail(performances: unknown) {
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
      programme: null,
      performances,
    },
  };
}

test("Mitglied liest die Nachweise mit Standmarke, ohne Quellenangabe", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await mockKatalog(page);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail([nachweisA(), nachweisB()]))),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const abschnitt = page.locator("section.auftritt-belege");
  await expect(
    abschnitt.getByRole("heading", { name: "Aufführungsnachweise" }),
  ).toBeVisible();

  // Ehrliche Einleitung: das Satzpaar erklärt die Unterscheidung, nur
  // wenn Nachweise stehen (der Leerstand spricht für sich).
  await expect(abschnitt.locator(".belege-einleitung")).toHaveText(
    "Bestätigt heißt: der Auftritt ist überliefert. Programmangabe heißt: das Lied steht in einer Programmquelle, ohne dass die Aufführung gesichert ist.",
  );

  // Geordnete Reihe in Positionsreihenfolge; die Titel lösen sich aus
  // den Lieddetails auf.
  const punkte = abschnitt.locator(".nachweise-liste .nachweis-eintrag");
  await expect(punkte).toHaveCount(2);
  await expect(punkte.nth(0)).toContainText("1.");
  await expect(punkte.nth(0)).toContainText("Das Wandern ist des Müllers Lust");
  await expect(punkte.nth(1)).toContainText("2.");
  await expect(punkte.nth(1)).toContainText("Aurora");
  // Ehrliche Fassungsangaben: bekannte Kette gegen „Fassung unbekannt“.
  await expect(punkte.nth(0)).toContainText(
    "Bestätigt · SATB · Satz für gemischten Chor · Standardfassung · Tonart: G-Dur",
  );
  await expect(punkte.nth(1)).toContainText(
    "Programmangabe · Fassung unbekannt",
  );
  // Die Marken tragen ihren Stand (Bestätigung in Markenfarbe).
  await expect(punkte.nth(0).locator(".nachweis-marke")).toHaveAttribute(
    "data-art",
    "confirmed",
  );
  await expect(punkte.nth(1).locator(".nachweis-marke")).toHaveAttribute(
    "data-art",
    "mention",
  );
  // Tiefe Leseporte wie im Programm: die bekannte Kette fährt Fassung
  // und Version in der Adresse, der Nachweis ohne Kette weist nur aufs
  // Lied — nichts wird erfunden.
  await expect(punkte.nth(0).getByRole("link")).toHaveAttribute(
    "href",
    `/lied/?id=${lied1Id}&fassung=${arrangement1Id}&version=${standardId}`,
  );
  await expect(punkte.nth(1).getByRole("link")).toHaveAttribute(
    "href",
    `/lied/?id=${lied2Id}`,
  );
  // Die Quellenangabe bleibt der Redaktion vorbehalten (Vertragssprache).
  await expect(punkte.getByText(/Quellenangabe/)).toHaveCount(0);
  await expect(punkte.getByText(/Erfasst:/)).toHaveCount(0);
  // Der Werkbank-Aufklapper bleibt dem Mitglied verborgen.
  await expect(page.locator("details.belege-erfassung")).toHaveCount(0);
  // Auch im Handymaß bleibt der Abschnitt ohne Seitenüberlauf.
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);

  expect(errors).toEqual([]);
});

test("Ohne Nachweise bleibt der Abschnitt für beide rollen ehrlich leer", async ({
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
    page.getByText(
      "Zu diesem Auftritt sind noch keine Aufführungsnachweise erfasst.",
    ),
  ).toBeVisible();
  // Ohne Zeilen liest keine Einleitung: der Leerstand braucht die
  // Unterscheidung nicht.
  await expect(page.locator(".belege-einleitung")).toHaveCount(0);
  await expect(page.locator(".nachweise-liste")).toHaveCount(0);
  await expect(page.locator(".belege-zeilen")).toHaveCount(0);
  await expect(page.locator("details.belege-erfassung")).toHaveCount(0);

  // Ältere Antworten ohne Nachweisfeld lesen sich als derselbe Leerstand.
  await page.unroute(`**/api/events/${eventId}`);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail(undefined))),
  );
  await page.reload();
  await expect(
    page.getByText(
      "Zu diesem Auftritt sind noch keine Aufführungsnachweise erfasst.",
    ),
  ).toBeVisible();
  await expect(page.locator(".belege-einleitung")).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Redaktion erfasst zweimal; der Wiederholungsschlüssel wandert weiter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  // Der Bestand wandert mit: die zuletzt gelesene Einbettung trägt die
  // erfassten Nachweise hinter den beiden Anfangszeilen.
  const erfasst: Array<Record<string, unknown>> = [];
  const neoFassung = (anfrage: Record<string, unknown>, stelle: number) => {
    const songId = anfrage.songId as string;
    const fassungId =
      songId === lied2Id
        ? lied2FassungId
        : "musicalVersionId" in anfrage
          ? (anfrage.musicalVersionId as string)
          : null;
    return {
      id: `00000000-0000-0000-0000-00000000f1a${stelle}`,
      eventId,
      songId,
      arrangementId: fassungId
        ? songId === lied2Id
          ? lied2ArrangementId
          : arrangement1Id
        : null,
      musicalVersionId: fassungId,
      position: stelle,
      evidenceStatus: anfrage.evidenceStatus,
      sourceNote: (anfrage.sourceNote as string | undefined) ?? null,
      capturedAt: "2026-09-26T10:00:00.000Z",
      createdAt: "2026-09-26T10:00:00.000Z",
      updatedAt: "2026-09-26T10:00:00.000Z",
      rowVersion: 1,
    };
  };
  await page.route(`**/api/events/${eventId}`, (route) => {
    const start = [nachweisA(), nachweisB()];
    const neu = erfasst.map((eintrag, index) => ({
      ...eintrag,
      position: index + 3,
    }));
    return route.fulfill(json(detail([...start, ...neu])));
  });
  const erfassungen: Record<string, unknown>[] = [];
  await page.route(`**/api/events/${eventId}/performances`, (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    erfassungen.push(anfrage);
    const neu = neoFassung(anfrage, erfasst.length + 3);
    erfasst.push(neu);
    return route.fulfill(json({ performance: neu }, 201));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(page.locator(".belege-zeile")).toHaveCount(2);
  await page.locator("details.belege-erfassung > summary").click();
  await expect(page.locator("details.belege-erfassung")).toHaveAttribute(
    "open",
    "",
  );

  // Erstes Lied über die Katalogsuche; die Fassung kommt aus dem
  // Lieddetail (Standardfassung in G-Dur).
  await page.getByLabel("Lied für den Nachweis suchen").fill("Wandern");
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".belege-lied-treffer")
    .getByRole("button", { name: "Das Wandern ist des Müllers Lust" })
    .click();
  await page
    .locator(".belege-fassung")
    .getByRole("button", { name: "Fassung unbekannt" })
    .click();
  // Die konkrete Fassung kommt aus dem Lieddetail (Standardfassung in
  // G-Dur) und reist als musikalische Version in der Erfassung.
  await page
    .locator(".belege-erfassung")
    .getByRole("button", { name: /Standardfassung/ })
    .click();
  await page.getByRole("radio", { name: "Bestätigte Aufführung" }).check();
  await page.getByRole("button", { name: "Nachweis erfassen" }).click();
  await expect(page.getByText("Nachweis erfasst.")).toBeVisible();
  expect(erfassungen).toHaveLength(1);
  // Der erste Erfassungswunsch trägt den Wiederholungsschlüssel; leere
  // Quellenangabe bleibt abwesend, die gewählte Kette reist mit.
  expect(erfassungen[0]).toEqual({
    songId: lied1Id,
    evidenceStatus: "confirmed",
    musicalVersionId: standardId,
    idempotencyKey: expect.stringMatching(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    ),
  });

  // Der frische Bestand liest die dritte Zeile; das Formular startet
  // leer für die nächste bewusste Erfassung.
  await expect(page.locator(".belege-zeile")).toHaveCount(3);
  await expect(
    page
      .locator(".belege-zeile")
      .nth(2)
      .getByText("Das Wandern ist des Müllers Lust"),
  ).toBeVisible();
  await expect(page.getByLabel("Lied für den Nachweis suchen")).toBeVisible();

  // Zweite Erfassung: Programmangabe mit Pflichtquellenangabe, Fassung
  // unbekannt (kein musicalVersionId im Körper).
  await page.getByLabel("Lied für den Nachweis suchen").fill("Aurora");
  await page.getByRole("button", { name: "Suchen" }).click();
  await page
    .locator(".belege-lied-treffer")
    .getByRole("button", { name: "Aurora" })
    .click();
  await page
    .locator(".belege-fassung")
    .getByRole("button", { name: "Fassung unbekannt" })
    .click();
  await page.getByRole("radio", { name: /Programmangabe/ }).check();

  // Ohne Quellenangabe bleibt die Erfassung clientseitig stehen (Vertrag).
  await page.getByRole("button", { name: "Nachweis erfassen" }).click();
  await expect(
    page.getByText(
      "Ein unverifizierter Programmhinweis braucht eine Quellenangabe.",
    ),
  ).toBeVisible();
  expect(erfassungen).toHaveLength(1);

  // Mit Quellenangabe kennt die Erfassung die Pflichtquellregel.
  await page
    .getByLabel("Quellenangabe", { exact: true })
    .fill("Programmheft im Stadtarchiv.");
  await page.getByRole("button", { name: "Nachweis erfassen" }).click();
  await expect(page.getByText("Nachweis erfasst.")).toBeVisible();
  expect(erfassungen).toHaveLength(2);
  expect(erfassungen[1]).toEqual({
    songId: lied2Id,
    evidenceStatus: "mention",
    sourceNote: "Programmheft im Stadtarchiv.",
    idempotencyKey: expect.stringMatching(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    ),
  });
  // Der Schlüssel wandert: ein bewusster zweiter Eintrag wiederholt die
  // erste Erfassung nicht (kein Replay desselben Schlüssels).
  expect(erfassungen[1].idempotencyKey).not.toBe(erfassungen[0].idempotencyKey);
  await expect(page.locator(".belege-zeile")).toHaveCount(4);

  expect(errors).toEqual([]);
});

test("Bearbeiten sendet nur die geänderten Felder und verlangt Nacharbeit bei 409", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  // Zwischenstand: die Angabe liest sich nach der 409-Ablehnung fremd
  // („Vom anderen Beitrag geändert."), rowVersion 7; die zweite
  // Bearbeitung trägt die frische Ankerung.
  let stand = { quelle: "Programmblatt im Stadtarchiv.", rowVersion: 2 };
  let abfragen = 0;
  await page.route(`**/api/events/${eventId}`, (route) => {
    abfragen += 1;
    return route.fulfill(
      json(
        detail([
          nachweisA(),
          nachweisB({ sourceNote: stand.quelle, rowVersion: stand.rowVersion }),
        ]),
      ),
    );
  });
  const patches: Record<string, unknown>[] = [];
  await page.route(`**/api/performances/${nachweisBId}`, (route) => {
    expect(route.request().method()).toBe("PATCH");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    patches.push(anfrage);
    if (patches.length === 1) {
      // Veralteter Stand: der frische kommt über den Eltern-Nachlauf —
      // ein anderer Beitrag hat die Angabe zwischenzeitlich geändert.
      stand = { quelle: "Vom anderen Beitrag geändert.", rowVersion: 7 };
      return route.fulfill(
        problem("Der Nachweis wurde zwischenzeitlich geändert.", 409),
      );
    }
    stand = {
      quelle: anfrage.sourceNote as string,
      rowVersion: stand.rowVersion + 1,
    };
    return route.fulfill(
      json({
        performance: nachweisB({
          sourceNote: stand.quelle,
          rowVersion: stand.rowVersion,
        }),
      }),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const zweite = page.locator(".belege-zeile").nth(1);
  await expect(zweite).toContainText("Aurora");
  await expect(zweite).toContainText(
    "Quellenangabe: Programmblatt im Stadtarchiv.",
  );
  await expect(zweite).toContainText(/Erfasst: 22\. September 2026/);

  await zweite.getByRole("button", { name: "Bearbeiten" }).click();
  await expect(
    zweite.getByRole("radio", { name: /Programmangabe/ }),
  ).toBeChecked();
  await zweite
    .getByLabel("Quellenangabe", { exact: true })
    .fill("Programmheft 1953, Stadtarchiv Mining.");
  await zweite.getByRole("button", { name: "Änderungen speichern" }).click();

  // Der erste Versuch liest als veraltet: der frische Stand kommt über
  // den Nachlauf, der Hinweis erklärt die Ersetzung.
  await expect(
    page.getByText(
      "Der Nachweis wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.",
    ),
  ).toBeVisible();
  // Der frische Stand liest sich in der Zeile (fremde Überarbeitung);
  // erst danach liest sich der Zähler ohne Wettkampf um die Nachladung.
  await expect(zweite).toContainText(
    "Quellenangabe: Vom anderen Beitrag geändert.",
  );
  expect(patches[0]).toEqual({
    rowVersion: 2,
    sourceNote: "Programmheft 1953, Stadtarchiv Mining.",
  });
  expect(abfragen).toBe(2);

  // Die zweite Bearbeitung trägt die frische rowVersion (7) und denselben
  // sparsamen Körper (nur das geänderte Feld reist mit).
  await zweite
    .getByLabel("Quellenangabe", { exact: true })
    .fill("Programmheft 1953, Stadtarchiv Mining.");
  await zweite.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();
  expect(patches[1]).toEqual({
    rowVersion: 7,
    sourceNote: "Programmheft 1953, Stadtarchiv Mining.",
  });
  expect(abfragen).toBe(3);
  await expect(
    zweite.getByRole("button", { name: "Bearbeiten schließen" }),
  ).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Löschen bestätigt einmal in der Zeile und entfernt den Nachweis", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await mockKatalog(page);
  let geloescht = false;
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json(detail(geloescht ? [nachweisB()] : [nachweisA(), nachweisB()])),
    ),
  );
  const loeschungen: Record<string, unknown>[] = [];
  await page.route(`**/api/performances/${nachweisAId}/delete`, (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    expect(anfrage).toEqual({});
    loeschungen.push(anfrage);
    geloescht = true;
    return route.fulfill({ status: 204 });
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(page.locator(".belege-zeile")).toHaveCount(2);

  const erste = page.locator(".belege-zeile").nth(0);
  // Einmalige Bestätigung in der Zeile: der erste Klick bewaffnet, der
  // zweite trägt die Entfernung (kein window.confirm).
  await erste.getByRole("button", { name: "Löschen" }).click();
  await expect(
    erste.getByRole("button", { name: "Wirklich löschen?" }),
  ).toBeVisible();
  expect(loeschungen).toHaveLength(0);
  await erste.getByRole("button", { name: "Wirklich löschen?" }).click();
  await expect(page.getByText("Nachweis gelöscht.")).toBeVisible();
  expect(loeschungen).toHaveLength(1);
  // Der frische Bestand liest ohne den Nachweis A.
  await expect(page.locator(".belege-zeile")).toHaveCount(1);
  await expect(page.locator(".belege-zeile").nth(0)).toContainText("Aurora");

  expect(errors).toEqual([]);
});
