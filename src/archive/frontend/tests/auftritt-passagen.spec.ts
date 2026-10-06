import { expect, type Page, test } from "@playwright/test";
import { webmBase64 } from "./aufnahmen-mock";

// ARC-032: markierte Stellen in ganzen Aufnahmen. Die API ist im Browser
// nachgestellt; die Antworten folgen Feld für Feld den Formen von
// GET /api/recordings/{id}/passages, POST …/passages, PATCH/POST
// …/passages/{id}[/delete] und POST …/passages/review.

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

const eventId = "00000000-0000-0000-0000-00000000c320";
const videoId = "00000000-0000-0000-0000-00000000c321";
const revisionId = "00000000-0000-0000-0000-00000000f320";
const alteRevisionId = "00000000-0000-0000-0000-00000000f319";
const performanceA = "00000000-0000-0000-0000-00000000d001";
const performanceB = "00000000-0000-0000-0000-00000000d002";
const performanceC = "00000000-0000-0000-0000-00000000d003";
const passageA = "00000000-0000-0000-0000-00000000e001";
const passageB = "00000000-0000-0000-0000-00000000e002";

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

type Stelle = {
  id: string;
  performanceId: string;
  songTitle: string;
  position: number;
  start: number;
  ende: number;
  zustand: "current" | "needsReview";
  version: number;
};

const lieder: Record<string, { titel: string; position: number }> = {
  [performanceA]: { titel: "Lied A", position: 1 },
  [performanceB]: { titel: "Lied B", position: 2 },
  [performanceC]: { titel: "Lied C", position: 3 },
};

function eintrag(stelle: Stelle, redaktion: boolean) {
  const verborgen = !redaktion && stelle.zustand === "needsReview";
  return {
    id: stelle.id,
    recordingId: videoId,
    performanceId: stelle.performanceId,
    songId: `song-${stelle.performanceId}`,
    songTitle: stelle.songTitle,
    arrangement: null,
    musicalVersion: null,
    evidenceStatus: "confirmed",
    position: stelle.position,
    startSeconds: verborgen ? null : stelle.start,
    endSeconds: verborgen ? null : stelle.ende,
    timestampState: stelle.zustand,
    editor: redaktion
      ? {
          version: stelle.version,
          playbackRevisionId:
            stelle.zustand === "current" ? revisionId : alteRevisionId,
          updatedAt: "2026-10-05T10:00:00.000Z",
        }
      : null,
  };
}

function stellenStand(stellen: Stelle[], redaktion: boolean, opts = {}) {
  const basis = {
    recordingId: videoId,
    eventId,
    passages: [...stellen]
      .sort((a, b) => a.position - b.position)
      .map((stelle) => eintrag(stelle, redaktion)),
  };
  if (!redaktion) return basis;
  return {
    ...basis,
    playbackRevisionId: revisionId,
    durationSeconds: 6,
    occurrences: Object.entries(lieder).map(([performanceId, lied]) => ({
      performanceId,
      position: lied.position,
      songId: `song-${performanceId}`,
      songTitle: lied.titel,
      arrangement: null,
      musicalVersion: null,
      evidenceStatus: "confirmed",
      passageId:
        stellen.find((s) => s.performanceId === performanceId)?.id ?? null,
    })),
    hasPublishedProgramme: true,
    ...opts,
  };
}

function aufnahme(redaktion: boolean) {
  return {
    id: videoId,
    eventId,
    label: "Gesamtmitschnitt Video",
    kind: "video",
    isPublished: true,
    downloadEnabled: false,
    durationSeconds: 6,
    playback: {
      state: "ready",
      source: "original",
      revisionId,
      contentType: "video/webm",
      sizeBytes: 1048576,
    },
    createdAt: "2026-10-01T10:00:00.000Z",
    editor: redaktion
      ? {
          version: 3,
          canChangeFiles: true,
          publishedAt: "2026-10-01T11:00:00.000Z",
          original: {
            assetId: "00000000-0000-0000-0000-00000000b321",
            file: {
              revisionId,
              revisionNumber: 1,
              contentType: "video/webm",
              sizeBytes: 1048576,
              fileName: "advent.webm",
              createdAt: "2026-10-01T10:00:00.000Z",
              playable: true,
            },
          },
          playbackCopy: null,
        }
      : null,
  };
}

let ticketNummer = 0;

type Anfragen = {
  zugriffe: number;
  gesendet: { methode: string; pfad: string; body: unknown }[];
};

async function mockSeite(
  page: Page,
  me: unknown,
  stellen: Stelle[],
  opts: { redaktion?: boolean; stand?: () => unknown } = {},
): Promise<Anfragen> {
  const redaktion = opts.redaktion ?? false;
  const anfragen: Anfragen = { zugriffe: 0, gesendet: [] };
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(
      json({
        event: {
          id: eventId,
          kind: "concert",
          title: "Adventkonzert",
          venue: null,
          dateYear: 2019,
          dateMonth: 12,
          dateDay: 8,
          dateApproximate: false,
          datePrecision: "day",
          dateDisplay: "8. Dezember 2019",
          startTime: null,
          published: true,
          notes: null,
          sourceNote: null,
          documents: [],
          createdAt: "2026-09-01T08:00:00.000Z",
          updatedAt: "2026-09-24T10:00:00.000Z",
          publishedAt: "2026-09-20T10:00:00.000Z",
        },
      }),
    ),
  );
  await page.route(`**/api/events/${eventId}/recordings`, (route) =>
    route.request().method() === "GET"
      ? route.fulfill(json({ eventId, recordings: [aufnahme(redaktion)] }))
      : route.fallback(),
  );
  await page.route(`**/api/recordings/${videoId}/passages`, (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    return route.fulfill(
      json(opts.stand ? opts.stand() : stellenStand(stellen, redaktion)),
    );
  });
  await page.route(`**/api/recordings/${videoId}/access`, (route) => {
    anfragen.zugriffe += 1;
    ticketNummer += 1;
    return route.fulfill(
      json({
        recordingId: videoId,
        kind: "video",
        playbackState: "ready",
        source: "original",
        revisionId,
        contentType: "video/webm",
        sizeBytes: 1048576,
        durationSeconds: 6,
        viewUrl: `https://speicher.test/passage-${ticketNummer}?ticket=ansicht`,
        downloadEnabled: false,
        downloadUrl: null,
        expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
      }),
    );
  });
  // Jede Ticket-Adresse liefert dasselbe Video und beantwortet
  // Bereichsanfragen wie der echte Blob-Dienst (206): darauf stützt sich
  // der Browser beim Suchen.
  const video = Buffer.from(webmBase64, "base64");
  await page.route("**/speicher.test/**", (route) => {
    const bereich = /^bytes=(\d+)-(\d*)$/.exec(
      route.request().headers().range ?? "",
    );
    if (!bereich) {
      return route.fulfill({
        status: 200,
        contentType: "video/webm",
        headers: { "Accept-Ranges": "bytes" },
        body: video,
      });
    }
    const von = Number(bereich[1]);
    const bis = bereich[2] ? Number(bereich[2]) : video.length - 1;
    return route.fulfill({
      status: 206,
      contentType: "video/webm",
      headers: {
        "Accept-Ranges": "bytes",
        "Content-Range": `bytes ${von}-${bis}/${video.length}`,
      },
      body: video.subarray(von, bis + 1),
    });
  });
  return anfragen;
}

const stelleA: Stelle = {
  id: passageA,
  performanceId: performanceA,
  songTitle: "Lied A",
  position: 1,
  start: 1,
  ende: 3,
  zustand: "current",
  version: 1,
};

const stelleB: Stelle = {
  id: passageB,
  performanceId: performanceB,
  songTitle: "Lied B",
  position: 2,
  start: 4,
  ende: 5.5,
  zustand: "current",
  version: 1,
};

function position(page: Page) {
  return page.evaluate(
    () =>
      (document.querySelector("video") as HTMLMediaElement | null)
        ?.currentTime ?? 0,
  );
}

function pausiert(page: Page) {
  return page.evaluate(
    () =>
      (document.querySelector("video") as HTMLMediaElement | null)?.paused ??
      true,
  );
}

test("Mitglied springt zu einem Lied; der Abschnitt endet mit Hinweis und die Aufnahme läuft weiter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const anfragen = await mockSeite(page, memberMe, [stelleA, stelleB]);
  await page.goto(`/auftritt/?id=${eventId}`);

  const liste = page.getByRole("region", { name: "Lieder in dieser Aufnahme" });
  await expect(liste.getByText("Lied A")).toBeVisible();
  await expect(liste.getByText("0:01 – 0:03")).toBeVisible();
  await expect(liste.getByText("0:04 – 0:05,5")).toBeVisible();
  // Die ganze Aufnahme ist auch ohne Sprung abspielbar; kein Ticket vorab.
  expect(anfragen.zugriffe).toBe(0);
  await expect(
    page.getByRole("button", { name: "Gesamtmitschnitt Video ansehen" }),
  ).toBeVisible();

  // Der Sprung öffnet den Spieler (frisches Ticket) und stellt den Anfang ein.
  await liste.getByRole("button", { name: "Zu „Lied A“ springen" }).click();
  const spieler = page.getByRole("region", {
    name: "Video-Spieler · Gesamtmitschnitt Video",
  });
  await expect(spieler).toBeVisible();
  expect(anfragen.zugriffe).toBe(1);
  await expect
    .poll(() => position(page), { timeout: 10_000 })
    .toBeGreaterThanOrEqual(0.95);
  expect(await position(page)).toBeLessThan(1.5);
  await expect(
    spieler.getByText("Abschnitt: „Lied A“ · 0:01 – 0:03"),
  ).toBeVisible();

  // Abspielen: am Ende des Abschnitts hält der Spieler an und sagt es.
  await spieler
    .getByRole("button", { name: "Gesamtmitschnitt Video abspielen" })
    .click();
  await expect(
    spieler.getByText("Ende des Abschnitts „Lied A“ erreicht."),
  ).toBeVisible({ timeout: 10_000 });
  expect(await pausiert(page)).toBe(true);
  const ende = await position(page);
  expect(ende).toBeGreaterThanOrEqual(2.9);
  expect(ende).toBeLessThan(3.8);

  // Ganze Aufnahme weiterspielen: kein Halt mehr am Abschnittsende.
  await spieler
    .getByRole("button", { name: "In der ganzen Aufnahme weiterspielen" })
    .click();
  await expect
    .poll(() => position(page), { timeout: 10_000 })
    .toBeGreaterThan(ende + 0.5);
  expect(await pausiert(page)).toBe(false);

  // Zum nächsten Lied: der Spieler bleibt, die Position wechselt.
  await liste.getByRole("button", { name: "Zu „Lied B“ springen" }).click();
  await expect.poll(() => position(page)).toBeGreaterThanOrEqual(3.9);
  await expect(
    spieler.getByText("Abschnitt: „Lied B“ · 0:04 – 0:05,5"),
  ).toBeVisible();
  expect(anfragen.zugriffe).toBe(1);
  expect(errors).toEqual([]);
});

test("Mitglied: eine Zeitmarke zu einer früheren Datei bietet keinen Sprung an", async ({
  page,
}) => {
  await mockSeite(page, memberMe, [
    stelleA,
    { ...stelleB, zustand: "needsReview" },
  ]);
  await page.goto(`/auftritt/?id=${eventId}`);
  const liste = page.getByRole("region", { name: "Lieder in dieser Aufnahme" });
  await expect(
    liste.getByRole("button", { name: "Zu „Lied A“ springen" }),
  ).toBeVisible();
  // Lied B ist Teil der Aufnahme, aber ohne Position: nichts springt falsch.
  await expect(liste.getByText("Lied B")).toBeVisible();
  await expect(liste.getByText("Zeitmarke wird überprüft")).toBeVisible();
  await expect(
    liste.getByRole("button", { name: "Zu „Lied B“ springen" }),
  ).toHaveCount(0);
  await expect(liste.getByText("0:04")).toHaveCount(0);
  // Die ganze Aufnahme bleibt abspielbar.
  await page
    .getByRole("button", { name: "Gesamtmitschnitt Video ansehen" })
    .click();
  await expect(
    page.getByRole("region", {
      name: "Video-Spieler · Gesamtmitschnitt Video",
    }),
  ).toBeVisible();
});

test("Verweis mit Stelle öffnet die Aufnahme am Anfang des Abschnitts", async ({
  page,
}) => {
  const anfragen = await mockSeite(page, memberMe, [stelleA, stelleB]);
  await page.goto(
    `/auftritt/?id=${eventId}&aufnahme=${videoId}&stelle=${passageB}`,
  );
  const spieler = page.getByRole("region", {
    name: "Video-Spieler · Gesamtmitschnitt Video",
  });
  await expect(spieler).toBeVisible();
  await expect(
    spieler.getByText("Abschnitt: „Lied B“ · 0:04 – 0:05,5"),
  ).toBeVisible();
  await expect
    .poll(() => position(page), { timeout: 10_000 })
    .toBeGreaterThanOrEqual(3.9);
  expect(anfragen.zugriffe).toBe(1);
});

test("Verweis auf eine unbekannte oder zu prüfende Stelle öffnet am Anfang und sagt warum", async ({
  page,
}) => {
  await mockSeite(page, memberMe, [
    stelleA,
    { ...stelleB, zustand: "needsReview" },
  ]);
  await page.goto(
    `/auftritt/?id=${eventId}&aufnahme=${videoId}&stelle=${passageB}&t=4`,
  );
  await expect(
    page.getByRole("region", {
      name: "Video-Spieler · Gesamtmitschnitt Video",
    }),
  ).toBeVisible();
  await expect(
    page.getByText(
      "Die Zeitmarke dieser Stelle wird gerade überprüft. Die Aufnahme beginnt am Anfang.",
    ),
  ).toBeVisible();
  // Ein Wert t= wird neben einer Stelle nicht erraten.
  await page.waitForTimeout(500);
  expect(await position(page)).toBeLessThan(0.5);

  await page.goto(
    `/auftritt/?id=${eventId}&aufnahme=${videoId}&stelle=00000000-0000-0000-0000-00000000ffff`,
  );
  await expect(
    page.getByText(
      "Diese Stelle gibt es nicht mehr. Die Aufnahme beginnt am Anfang.",
    ),
  ).toBeVisible();
});

test("Mitglied ohne markierte Lieder sieht keine Liste und keine erfundene Aussage", async ({
  page,
}) => {
  await mockSeite(page, memberMe, []);
  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByRole("button", { name: "Gesamtmitschnitt Video ansehen" }),
  ).toBeVisible();
  await expect(
    page.getByRole("region", { name: "Lieder in dieser Aufnahme" }),
  ).toHaveCount(0);
  await expect(page.getByText(/keine Lieder/i)).toHaveCount(0);
});

test("Ladefehler der Zeitmarken lässt die Aufnahme spielbar und bietet einen neuen Versuch", async ({
  page,
}) => {
  let scheitern = true;
  await mockSeite(page, memberMe, [stelleA], {
    stand: () => (scheitern ? null : stellenStand([stelleA], false)),
  });
  await page.route(`**/api/recordings/${videoId}/passages`, (route) =>
    scheitern ? route.fulfill(problem("kaputt", 500)) : route.fallback(),
  );
  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Die Zeitmarken konnten nicht geladen werden."),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Gesamtmitschnitt Video ansehen" }),
  ).toBeVisible();
  scheitern = false;
  await page.getByRole("button", { name: "Zeitmarken erneut laden" }).click();
  await expect(
    page.getByRole("button", { name: "Zu „Lied A“ springen" }),
  ).toBeVisible();
});

test("Redaktion geht die Lieder der Reihe nach durch und setzt Anfang und Ende mit dem Spieler", async ({
  page,
}) => {
  const stellen: Stelle[] = [];
  const anfragen = await mockSeite(page, editorMe, stellen, {
    redaktion: true,
  });
  await page.route(`**/api/recordings/${videoId}/passages`, async (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    const body = route.request().postDataJSON() as {
      performanceId: string;
      startSeconds: number;
      endSeconds: number;
    };
    anfragen.gesendet.push({ methode: "POST", pfad: "passages", body });
    const neu: Stelle = {
      id: passageA,
      performanceId: body.performanceId,
      songTitle: lieder[body.performanceId].titel,
      position: lieder[body.performanceId].position,
      start: body.startSeconds,
      ende: body.endSeconds,
      zustand: "current",
      version: 1,
    };
    stellen.push(neu);
    return route.fulfill(json({ passage: eintrag(neu, true) }, 201));
  });
  await page.goto(`/auftritt/?id=${eventId}`);

  const zeitmarken = page.getByRole("group", {
    name: "Zeitmarken · Gesamtmitschnitt Video",
  });
  const zeilen = zeitmarken.getByRole("listitem");
  await expect(zeilen).toHaveCount(3);
  await expect(zeilen.nth(0)).toContainText("Lied A");
  await expect(zeilen.nth(1)).toContainText("Lied B");
  await expect(zeilen.nth(0)).toContainText("Noch nicht markiert");

  // Ohne geöffneten Spieler gibt es keine Position zu übernehmen.
  await expect(
    zeitmarken.getByRole("button", {
      name: "Aktuelle Position als Anfang für „Lied A“ übernehmen",
    }),
  ).toBeDisabled();
  await page
    .getByRole("button", { name: "Gesamtmitschnitt Video ansehen" })
    .click();
  const spieler = page.getByRole("region", {
    name: "Video-Spieler · Gesamtmitschnitt Video",
  });
  await expect(spieler.getByText(/^0:00 \/ 0:0[56]$/)).toBeVisible();

  // Anfang und Ende aus dem Spieler übernehmen.
  await spieler.getByLabel("Position (Gesamtmitschnitt Video)").fill("1.5");
  await expect.poll(() => position(page)).toBeGreaterThanOrEqual(1.4);
  await zeitmarken
    .getByRole("button", {
      name: "Aktuelle Position als Anfang für „Lied A“ übernehmen",
    })
    .click();
  await expect(zeitmarken.getByLabel("Anfang „Lied A“")).toHaveValue(
    /^0:01,[45]\d{0,2}$/,
  );
  await spieler.getByLabel("Position (Gesamtmitschnitt Video)").fill("3");
  await expect.poll(() => position(page)).toBeGreaterThanOrEqual(2.9);
  await zeitmarken
    .getByRole("button", {
      name: "Aktuelle Position als Ende für „Lied A“ übernehmen",
    })
    .click();
  await expect(zeitmarken.getByLabel("Ende „Lied A“")).toHaveValue(
    /^0:0(2,9|3)/,
  );
  // Von Hand korrigieren ist möglich und genau.
  await zeitmarken.getByLabel("Anfang „Lied A“").fill("0:01,5");
  await zeitmarken.getByLabel("Ende „Lied A“").fill("0:03");

  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect.poll(() => anfragen.gesendet.length).toBe(1);
  expect(anfragen.gesendet[0].body).toEqual({
    performanceId: performanceA,
    startSeconds: 1.5,
    endSeconds: 3,
  });
  await expect(
    page.getByText("Zeitmarke für „Lied A“ gespeichert."),
  ).toBeVisible();
  await expect(zeilen.nth(0)).toContainText("0:01,5 – 0:03");
  // Der Fokus geht zum nächsten Lied ohne Marke.
  await expect(zeitmarken.getByLabel("Anfang „Lied B“")).toBeFocused();
});

test("Redaktion: ungültige Zeiten werden erklärt, ohne Anfrage und ohne die Eingabe zu verlieren", async ({
  page,
}) => {
  const anfragen = await mockSeite(page, editorMe, [], { redaktion: true });
  await page.goto(`/auftritt/?id=${eventId}`);
  const zeitmarken = page.getByRole("group", {
    name: "Zeitmarken · Gesamtmitschnitt Video",
  });
  const alarm = zeitmarken.getByRole("alert");

  await zeitmarken.getByLabel("Anfang „Lied A“").fill("abc");
  await zeitmarken.getByLabel("Ende „Lied A“").fill("0:30");
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(alarm).toContainText(
    "Den Anfang bitte als Minuten:Sekunden angeben, zum Beispiel 12:30.",
  );
  await expect(alarm.locator("p")).toBeFocused();

  await zeitmarken.getByLabel("Anfang „Lied A“").fill("1:00");
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(alarm).toContainText("Das Ende muss nach dem Anfang liegen.");

  await zeitmarken.getByLabel("Ende „Lied A“").fill("");
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(alarm).toContainText("Das Ende fehlt.");

  // Hinter dem Ende der Aufnahme (Dauer 0:06) wird schon vor dem Absenden erklärt.
  await zeitmarken.getByLabel("Anfang „Lied A“").fill("0:01");
  await zeitmarken.getByLabel("Ende „Lied A“").fill("0:20");
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(alarm).toContainText(
    "Das Ende liegt hinter dem Ende der Aufnahme.",
  );
  expect(anfragen.gesendet).toEqual([]);
  await expect(zeitmarken.getByLabel("Ende „Lied A“")).toHaveValue("0:20");
});

test("Redaktion: Serverfehler halten die Eingabe, ein veralteter Stand lädt neu", async ({
  page,
}) => {
  let stellen: Stelle[] = [];
  await mockSeite(page, editorMe, stellen, {
    redaktion: true,
    stand: () => stellenStand(stellen, true),
  });
  let antwort = problem("Das Ende liegt hinter dem Ende der Aufnahme.", 400);
  await page.route(`**/api/recordings/${videoId}/passages`, (route) =>
    route.request().method() === "POST"
      ? route.fulfill(antwort)
      : route.fallback(),
  );
  await page.goto(`/auftritt/?id=${eventId}`);
  const zeitmarken = page.getByRole("group", {
    name: "Zeitmarken · Gesamtmitschnitt Video",
  });
  await zeitmarken.getByLabel("Anfang „Lied A“").fill("0:01");
  await zeitmarken.getByLabel("Ende „Lied A“").fill("0:03");
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(zeitmarken.getByRole("alert")).toContainText(
    "Das Ende liegt hinter dem Ende der Aufnahme.",
  );
  await expect(zeitmarken.getByLabel("Ende „Lied A“")).toHaveValue("0:03");

  // Nicht erlaubt in diesem Zustand: die Eingabe bleibt stehen.
  antwort = problem(
    "Für diese Aufführung gibt es in dieser Aufnahme schon eine Zeitmarke. Bearbeite die vorhandene.",
    409,
  );
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(zeitmarken.getByRole("alert")).toContainText(
    "schon eine Zeitmarke",
  );
  await expect(zeitmarken.getByLabel("Anfang „Lied A“")).toHaveValue("0:01");

  // Veraltet: der frische Stand kommt, die Eingabe gehörte zum alten.
  stellen = [{ ...stelleA, start: 100, ende: 200 }];
  antwort = problem("Die Zeitmarke wurde zwischenzeitlich geändert.", 409);
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect(zeitmarken.getByRole("alert")).toContainText(
    "Die Zeitmarke wurde zwischenzeitlich geändert. Der aktuelle Stand wurde geladen; bitte die Änderung erneut vornehmen.",
  );
  await expect(zeitmarken.getByRole("listitem").nth(0)).toContainText(
    "1:40 – 3:20",
  );
});

test("Redaktion ändert und entfernt eine Zeitmarke mit dem Stand, den sie gesehen hat", async ({
  page,
}) => {
  const stellen: Stelle[] = [{ ...stelleA, version: 4 }];
  const anfragen = await mockSeite(page, editorMe, stellen, {
    redaktion: true,
    stand: () => stellenStand(stellen, true),
  });
  await page.route(
    `**/api/recordings/${videoId}/passages/${passageA}`,
    async (route) => {
      const body = route.request().postDataJSON() as {
        startSeconds: number;
        endSeconds: number;
        expectedVersion: number;
      };
      anfragen.gesendet.push({ methode: "PATCH", pfad: "passage", body });
      stellen[0] = {
        ...stellen[0],
        start: body.startSeconds,
        ende: body.endSeconds,
        version: 5,
      };
      return route.fulfill(json({ passage: eintrag(stellen[0], true) }));
    },
  );
  await page.route(
    `**/api/recordings/${videoId}/passages/${passageA}/delete`,
    async (route) => {
      anfragen.gesendet.push({
        methode: "POST",
        pfad: "delete",
        body: route.request().postDataJSON(),
      });
      stellen.length = 0;
      return route.fulfill({ status: 204 });
    },
  );
  await page.goto(`/auftritt/?id=${eventId}`);
  const zeitmarken = page.getByRole("group", {
    name: "Zeitmarken · Gesamtmitschnitt Video",
  });
  await expect(zeitmarken.getByLabel("Anfang „Lied A“")).toHaveValue("0:01");

  await zeitmarken.getByLabel("Ende „Lied A“").fill("0:02,5");
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ speichern" })
    .click();
  await expect.poll(() => anfragen.gesendet.length).toBe(1);
  expect(anfragen.gesendet[0]).toEqual({
    methode: "PATCH",
    pfad: "passage",
    body: { startSeconds: 1, endSeconds: 2.5, expectedVersion: 4 },
  });
  await expect(
    page.getByText("Zeitmarke für „Lied A“ gespeichert."),
  ).toBeVisible();

  // Entfernen verlangt eine zweite Bestätigung und lässt das Lied unmarkiert zurück.
  const entfernen = zeitmarken.getByRole("button", {
    name: "Zeitmarke für „Lied A“ entfernen",
  });
  await entfernen.click();
  expect(anfragen.gesendet).toHaveLength(1);
  await zeitmarken
    .getByRole("button", { name: "Zeitmarke für „Lied A“ wirklich entfernen" })
    .click();
  await expect.poll(() => anfragen.gesendet.length).toBe(2);
  expect(anfragen.gesendet[1].body).toEqual({ expectedVersion: 5 });
  await expect(zeitmarken.getByRole("listitem").nth(0)).toContainText(
    "Noch nicht markiert",
  );
  await expect(
    page.getByText("Zeitmarke für „Lied A“ entfernt."),
  ).toBeVisible();
});

test("Redaktion sieht zu prüfende Zeitmarken und bestätigt sie gegen die aktuelle Datei", async ({
  page,
}) => {
  let stellen: Stelle[] = [
    { ...stelleA, zustand: "needsReview" },
    { ...stelleB, zustand: "needsReview" },
  ];
  const anfragen = await mockSeite(page, editorMe, stellen, {
    redaktion: true,
    stand: () => stellenStand(stellen, true),
  });
  await page.route(`**/api/recordings/${videoId}/passages/review`, (route) => {
    anfragen.gesendet.push({
      methode: "POST",
      pfad: "review",
      body: route.request().postDataJSON(),
    });
    stellen = stellen.map((s) => ({
      ...s,
      zustand: "current",
      version: s.version + 1,
    }));
    return route.fulfill(
      json({
        recordingId: videoId,
        playbackRevisionId: revisionId,
        passages: stellen.map((s) => eintrag(s, true)),
      }),
    );
  });
  await page.goto(`/auftritt/?id=${eventId}`);
  const zeitmarken = page.getByRole("group", {
    name: "Zeitmarken · Gesamtmitschnitt Video",
  });
  await expect(
    zeitmarken.getByText(
      "2 Zeitmarken wurden für eine frühere Datei gesetzt und müssen geprüft werden.",
    ),
  ).toBeVisible();
  await expect(zeitmarken.getByRole("listitem").nth(0)).toContainText(
    "Zu prüfen",
  );
  await expect(zeitmarken.getByLabel("Anfang „Lied A“")).toHaveValue("0:01");

  await zeitmarken
    .getByRole("button", {
      name: "Alle Zeitmarken für die aktuelle Datei bestätigen",
    })
    .click();
  await expect.poll(() => anfragen.gesendet.length).toBe(1);
  expect(anfragen.gesendet[0].body).toEqual({});
  await expect(
    page.getByText("2 Zeitmarken für die aktuelle Datei bestätigt."),
  ).toBeVisible();
  await expect(zeitmarken.getByText("müssen geprüft werden")).toHaveCount(0);
  await expect(zeitmarken.getByText("Zu prüfen")).toHaveCount(0);
});

test("Redaktion: ohne bestätigte Aufführungen sagt die Liste, was fehlt", async ({
  page,
}) => {
  await mockSeite(page, editorMe, [], {
    redaktion: true,
    stand: () => ({ ...stellenStand([], true), occurrences: [] }),
  });
  await page.goto(`/auftritt/?id=${eventId}`);
  const zeitmarken = page.getByRole("group", {
    name: "Zeitmarken · Gesamtmitschnitt Video",
  });
  await expect(
    zeitmarken.getByText(
      "Zu diesem Auftritt gibt es noch keine bestätigten Aufführungen. Das veröffentlichte Programm ist noch nicht bestätigt: Bestätige zuerst unter „Tatsächlich gesungen“, was gesungen wurde.",
    ),
  ).toBeVisible();
  await expect(zeitmarken.getByRole("listitem")).toHaveCount(0);
});
