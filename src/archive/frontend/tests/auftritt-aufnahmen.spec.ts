import { expect, type Page, type Route, test } from "@playwright/test";

// ARC-030: ganze Aufnahmen am Auftritt. Die API ist im Browser nachgestellt;
// die Antworten folgen Feld für Feld den Formen von
// GET/POST /api/events/{id}/recordings, PATCH /api/recordings/{id},
// POST /api/recordings/{id}/playback und GET /api/recordings/{id}/access.

/** Echtes VP8-WebM, 64×36, 6 Sekunden (mit dem Playwright-ffmpeg erzeugt). */
const webmBase64 =
  "GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJChYECGFOAZwEAAAAAAAX1EU2bdLpNu4tTq4QVSalmU6yBoU27i1OrhBZUrmtTrIHWTbuMU6uEElTDZ1OsggEnTbuMU6uEHFO7a1OsggWB7AEAAAAAAABZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmsCrXsYMPQkBNgIxMYXZmNjEuMS4xMDBXQYxMYXZmNjEuMS4xMDBEiYhAt3AAAAAAABZUrmvMrgEAAAAAAABD14EBc8WIyj+J+NAnib6cgQAitZyDdW5kiIEAhoVWX1ZQOIOBASPjg4QL68IA4JSwgUC6gSSagQJVsIhVt4ECVbiBAhJUw2f6c3OfY8CAZ8iZRaOHRU5DT0RFUkSHjExhdmY2MS4xLjEwMHNz1WPAi2PFiMo/ifjQJ4m+Z8igRaOHRU5DT0RFUkSHk0xhdmM2MS4zLjEwMCBsaWJ2cHhnyKFFo4hEVVJBVElPTkSHkzAwOjAwOjA2LjAwMDAwMDAwMAAfQ7Z1Q2zngQCjwIEAAIBQAwCdASpAACQAAEcIhYWIhYSIAgICdaoD+AIHCNVnmY830gD+/Vez/8/M7txf1W//XmX15l9eZf9daACjloEAyADRAQABEBAAGAAYWC/0AAiMAACjloEBkADRAQABEBAAGAAYWC/0AAiMAACjloECWADRAQABEBAAGAAYWC/0AAiMAACjloEDIADRAQABEBAAGAAYWC/0AAiMAACjvYED6IAQAwCdASpAACQAAEcIhYWIhYSIAgICdaoCBuZA/fQmzy4A/vCl1/+zFP/Zin/sxT/sxT9H1wiyAgCjloEEsADRAQABEBAAGAAYWC/0AAiMAACjloEFeADRAQABEBAAGAAYWC/0AAiMAACjloEGQADRAQABEBAAGAAYWC/0AAiMAACjloEHCADRAQABEBAAGAAYWC/0AAiMAACjwoEH0IBQAwCdASpAACQAAEcIhYWIhYSIAgICdaoD+AIHCNVnmY830gD+8FMf/3+q9/Ve/qv92n/6bN+mzfps3/piwKOWgQiYANEBAAEQEAAYABhYL/QACIwAAKOWgQlgANEBAAEQEAAYABhYL/QACIwAAKOWgQooANEBAAEQEAAYABhYL/QACIwAAKOWgQrwANEBAAEQEAAYABhYL/QACIwAAKO+gQu4gFADAJ0BKkAAJAAARwiFhYiFhIgCAgJ1qgP4AgcI6LfizzMVAP5vH/56ZrzB/k5/+xlfsZX7GV/7EeCjloEMgADRAQABEBAAGAAYWC/0AAiMAACjloENSADRAQABEBAAGAAYWC/0AAiMAACjloEOEADRAQABEBAAGAAYWC/0AAiMAACjloEO2ADRAQABEBAAGAAYWC/0AAiMAACjvoEPoIAQAwCdASpAACQAAEcIhYWIhYSIAgICdaoCBuY1Z5mPN9IA/v64AH/15l/68y/9eZf9eZf9izlsCxAAo5aBEGgA0QEAARAQABgAGFgv9AAIjAAAo5aBETAA0QEAARAQABgAGFgv9AAIjAAAo5aBEfgA0QEAARAQABgAGFgv9AAIjAAAo5aBEsAA0QEAARAQABgAGFgv9AAIjAAAo8KBE4iAUAMAnQEqQAAkAABHCIWFiIWEiAICAnWqA/gCBwjVZ5mPN9IA/vsZy/99APegHvQD/F//9HG/Rxv0cb/0XAAfQ7Z15OeCFFCjloEAAADRAQABEBAAGAAYWC/0AAiMAACjloEAyADRAQABEBAAGAAYWC/0AAiMAACjloEBkADRAQABEBAAGAAYWC/0AAiMAACjloECWADRAQABEBAAGAAYWC/0AAiMAAAcU7tr77uPs4EAt4r3gQHxggGm8IEDu5CzggPot4r3gQHxggGm8IGlu5GzggfQt4v3gQHxggGm8IIBRLuRs4ILuLeL94EB8YIBpvCCAei7kbOCD6C3i/eBAfGCAabwggKIu5GzghOIt4v3gQHxggGm8IIDKA==";

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

const eventId = "00000000-0000-0000-0000-00000000a030";
const videoId = "00000000-0000-0000-0000-00000000a031";
const tonId = "00000000-0000-0000-0000-00000000a032";
const rohId = "00000000-0000-0000-0000-00000000a033";
const neuId = "00000000-0000-0000-0000-00000000a034";

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

function detail() {
  return {
    event: {
      id: eventId,
      kind: "concert",
      title: "Adventkonzert",
      venue: "Pfarrkirche Mining",
      dateYear: 2019,
      dateMonth: 12,
      dateDay: 8,
      dateApproximate: false,
      datePrecision: "day",
      dateDisplay: "8. Dezember 2019",
      startTime: "17:00",
      published: true,
      notes: null,
      sourceNote: null,
      documents: [],
      createdAt: "2026-09-01T08:00:00.000Z",
      updatedAt: "2026-09-24T10:00:00.000Z",
      publishedAt: "2026-09-20T10:00:00.000Z",
    },
  };
}

type Datei = {
  revisionId: string;
  revisionNumber: number;
  contentType: string;
  sizeBytes: number;
  fileName: string | null;
  createdAt: string;
  playable: boolean;
};

function datei(aenderung: Partial<Datei> = {}): Datei {
  return {
    revisionId: "00000000-0000-0000-0000-00000000f030",
    revisionNumber: 1,
    contentType: "video/webm",
    sizeBytes: 1610612736,
    fileName: "advent.webm",
    createdAt: "2026-10-01T10:00:00.000Z",
    playable: true,
    ...aenderung,
  };
}

type RedaktionsBlock = {
  version: number;
  canChangeFiles: boolean;
  publishedAt: string | null;
  original: { assetId: string; file: Datei | null };
  playbackCopy: { assetId: string; file: Datei | null } | null;
};

type AufnahmeForm = {
  id: string;
  eventId: string;
  label: string;
  kind: "audio" | "video";
  isPublished: boolean;
  downloadEnabled: boolean;
  durationSeconds: number | null;
  playback: {
    state: "ready" | "needsPlaybackCopy" | "missing";
    source: "original" | "playbackCopy" | null;
    revisionId: string | null;
    contentType: string | null;
    sizeBytes: number | null;
  };
  createdAt: string;
  editor: RedaktionsBlock | null;
};

function aufnahme(aenderung: Partial<AufnahmeForm> = {}): AufnahmeForm {
  return {
    id: videoId,
    eventId,
    label: "Gesamtmitschnitt Video",
    kind: "video",
    isPublished: true,
    downloadEnabled: false,
    durationSeconds: 5412.5,
    playback: {
      state: "ready",
      source: "original",
      revisionId: "00000000-0000-0000-0000-00000000f030",
      contentType: "video/webm",
      sizeBytes: 1610612736,
    },
    createdAt: "2026-10-01T10:00:00.000Z",
    editor: null,
    ...aenderung,
  };
}

function tonAufnahme(aenderung: Partial<AufnahmeForm> = {}): AufnahmeForm {
  return aufnahme({
    id: tonId,
    label: "Ton vom Mischpult",
    kind: "audio",
    downloadEnabled: true,
    durationSeconds: null,
    playback: {
      state: "ready",
      source: "original",
      revisionId: "00000000-0000-0000-0000-00000000f032",
      contentType: "audio/wav",
      sizeBytes: 3145728,
    },
    ...aenderung,
  });
}

function redaktion(aenderung: Partial<RedaktionsBlock> = {}): RedaktionsBlock {
  return {
    version: 3,
    canChangeFiles: true,
    publishedAt: "2026-10-01T11:00:00.000Z",
    original: {
      assetId: "00000000-0000-0000-0000-00000000b031",
      file: datei(),
    },
    playbackCopy: null,
    ...aenderung,
  };
}

let ticketNummer = 0;

function zugriff(
  recordingId: string,
  aenderung: Record<string, unknown> = {},
): Record<string, unknown> & { viewUrl: string | null } {
  ticketNummer += 1;
  return {
    recordingId,
    kind: "video",
    playbackState: "ready",
    source: "original",
    revisionId: "00000000-0000-0000-0000-00000000f030",
    contentType: "video/webm",
    sizeBytes: 1610612736,
    durationSeconds: 5412.5,
    viewUrl: `https://speicher.test/aufnahme-${ticketNummer}?ticket=ansicht`,
    downloadEnabled: false,
    downloadUrl: null,
    expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
    ...aenderung,
  };
}

/** Abspielbare Mono-WAV-Datei (440-Hz-Sinuston). */
function wavBytes(dauerSekunden = 4, sampleRate = 8000) {
  const anzahl = sampleRate * dauerSekunden;
  const kopf = Buffer.alloc(44);
  kopf.write("RIFF", 0);
  kopf.writeUInt32LE(36 + anzahl * 2, 4);
  kopf.write("WAVE", 8);
  kopf.write("fmt ", 12);
  kopf.writeUInt32LE(16, 16);
  kopf.writeUInt16LE(1, 20);
  kopf.writeUInt16LE(1, 22);
  kopf.writeUInt32LE(sampleRate, 24);
  kopf.writeUInt32LE(sampleRate * 2, 28);
  kopf.writeUInt16LE(2, 32);
  kopf.writeUInt16LE(16, 34);
  kopf.write("data", 36);
  kopf.writeUInt32LE(anzahl * 2, 40);
  const daten = Buffer.alloc(anzahl * 2);
  for (let i = 0; i < anzahl; i += 1) {
    daten.writeInt16LE(
      Math.round(Math.sin((i / sampleRate) * 2 * Math.PI * 440) * 8000),
      i * 2,
    );
  }
  return Buffer.concat([kopf, daten]);
}

async function mockSeite(page: Page, me: unknown, liste: () => unknown[]) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(detail())),
  );
  await page.route(`**/api/events/${eventId}/recordings`, (route) =>
    route.request().method() === "GET"
      ? route.fulfill(json({ eventId, recordings: liste() }))
      : route.fallback(),
  );
}

/**
 * Liefert Dateien des Speicherdienstes je Ticket-Adresse aus und beantwortet
 * Bereichsanfragen wie der echte Blob-Dienst (206 mit Content-Range) – darauf
 * stützt sich der Browser beim Suchen.
 */
async function mockSpeicher(
  page: Page,
  dateien: Map<string, { body: Buffer | string; contentType: string }>,
) {
  await page.route("**/speicher.test/**", (route: Route) => {
    const eintrag = dateien.get(route.request().url());
    if (!eintrag) {
      return route.fulfill({
        status: 403,
        contentType: "text/plain",
        body: "abgelaufen",
      });
    }
    const inhalt = Buffer.from(eintrag.body);
    const bereich = /^bytes=(\d+)-(\d*)$/.exec(
      route.request().headers().range ?? "",
    );
    if (!bereich) {
      return route.fulfill({
        status: 200,
        contentType: eintrag.contentType,
        headers: { "Accept-Ranges": "bytes" },
        body: inhalt,
      });
    }
    const von = Number(bereich[1]);
    const bis = bereich[2] ? Number(bereich[2]) : inhalt.length - 1;
    return route.fulfill({
      status: 206,
      contentType: eintrag.contentType,
      headers: {
        "Accept-Ranges": "bytes",
        "Content-Range": `bytes ${von}-${bis}/${inhalt.length}`,
      },
      body: inhalt.subarray(von, bis + 1),
    });
  });
}

function position(page: Page, element: "audio" | "video") {
  return page.evaluate(
    (name) =>
      (document.querySelector(name) as HTMLMediaElement | null)?.currentTime ??
      0,
    element,
  );
}

test("Mitglied sieht ein Video und hört eine Tonaufnahme desselben Auftritts", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSeite(page, memberMe, () => [aufnahme(), tonAufnahme()]);
  const videoTicket = zugriff(videoId);
  const tonTicket = zugriff(tonId, {
    kind: "audio",
    contentType: "audio/wav",
    sizeBytes: 3145728,
    durationSeconds: null,
    downloadEnabled: true,
    downloadUrl: "https://speicher.test/ton?ticket=laden",
  });
  let videoZugriffe = 0;
  let tonZugriffe = 0;
  await page.route(`**/api/recordings/${videoId}/access`, (route) => {
    videoZugriffe += 1;
    return route.fulfill(json(videoTicket));
  });
  await page.route(`**/api/recordings/${tonId}/access`, (route) => {
    tonZugriffe += 1;
    return route.fulfill(json(tonTicket));
  });
  await mockSpeicher(
    page,
    new Map([
      [
        videoTicket.viewUrl as string,
        { body: Buffer.from(webmBase64, "base64"), contentType: "video/webm" },
      ],
      [
        tonTicket.viewUrl as string,
        { body: wavBytes(), contentType: "audio/wav" },
      ],
    ]),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const video = page.getByRole("article", { name: "Gesamtmitschnitt Video" });
  const ton = page.getByRole("article", { name: "Ton vom Mischpult" });
  await expect(
    video.getByText("Video · WebM · 1,5 GB · Dauer 1:30:12"),
  ).toBeVisible();
  await expect(ton.getByText("Tonaufnahme · WAV · 3 MB")).toBeVisible();
  // Keine Redaktionsdaten und kein Ticket vor dem ersten Klick.
  await expect(page.getByText("Redaktion ·")).toHaveCount(0);
  await expect(page.getByText("Aufnahme hinzufügen")).toHaveCount(0);
  expect(videoZugriffe + tonZugriffe).toBe(0);
  // Herunterladen nur dort, wo die Redaktion es freigegeben hat.
  await expect(
    video.getByRole("button", { name: /herunterladen/ }),
  ).toHaveCount(0);
  await expect(
    ton.getByRole("button", { name: "Ton vom Mischpult herunterladen" }),
  ).toBeVisible();

  await video
    .getByRole("button", { name: "Gesamtmitschnitt Video ansehen" })
    .click();
  const spieler = page.getByRole("region", {
    name: "Video-Spieler · Gesamtmitschnitt Video",
  });
  await expect(spieler).toBeVisible();
  await expect(spieler.locator("video")).toBeVisible();
  // Der Öffnen-Knopf bleibt als Schließen-Knopf im Fokus.
  await expect(
    video.getByRole("button", { name: "Gesamtmitschnitt Video schließen" }),
  ).toBeFocused();
  await expect(spieler.getByText(/^0:00 \/ 0:0[56]$/)).toBeVisible();
  await spieler
    .getByRole("button", { name: "Gesamtmitschnitt Video abspielen" })
    .click();
  await expect(
    spieler.getByRole("button", { name: "Gesamtmitschnitt Video pausieren" }),
  ).toBeVisible();
  await expect
    .poll(() => position(page, "video"), { timeout: 10_000 })
    .toBeGreaterThan(0.2);
  // Suchen springt an die gewählte Stelle.
  await spieler.getByLabel("Position (Gesamtmitschnitt Video)").fill("4");
  await expect.poll(() => position(page, "video")).toBeGreaterThanOrEqual(3.9);
  await expect(
    spieler.getByRole("button", {
      name: "Gesamtmitschnitt Video im Vollbild zeigen",
    }),
  ).toBeVisible();

  // Herunterladen ist für das Video nicht freigegeben: auch das
  // Medienelement selbst bietet keinen Speichern-Weg an.
  await expect(spieler.locator("video")).toHaveAttribute(
    "controlslist",
    "nodownload",
  );
  expect(
    await spieler.locator("video").evaluate((element) => {
      const ereignis = new MouseEvent("contextmenu", {
        bubbles: true,
        cancelable: true,
      });
      element.dispatchEvent(ereignis);
      return ereignis.defaultPrevented;
    }),
  ).toBe(true);

  // Die Tonaufnahme läuft im selben Spieler; das Video pausiert dabei.
  await ton.getByRole("button", { name: "Ton vom Mischpult anhören" }).click();
  const tonSpieler = page.getByRole("region", {
    name: "Audio-Spieler · Ton vom Mischpult",
  });
  await tonSpieler
    .getByRole("button", { name: "Ton vom Mischpult abspielen" })
    .click();
  await expect
    .poll(() => position(page, "audio"), { timeout: 10_000 })
    .toBeGreaterThan(0.2);
  await expect(
    spieler.getByRole("button", { name: "Gesamtmitschnitt Video abspielen" }),
  ).toBeVisible();
  expect(videoZugriffe).toBe(1);
  // Wo das Herunterladen freigegeben ist, bleibt das Element unverändert.
  await expect(tonSpieler.locator("audio")).not.toHaveAttribute(
    "controlslist",
    /.*/,
  );
  expect(
    await tonSpieler.locator("audio").evaluate((element) => {
      const ereignis = new MouseEvent("contextmenu", {
        bubbles: true,
        cancelable: true,
      });
      element.dispatchEvent(ereignis);
      return ereignis.defaultPrevented;
    }),
  ).toBe(false);

  // Herunterladen holt sein Ticket im Augenblick des Klicks.
  const geladen = page.waitForRequest("https://speicher.test/ton?ticket=laden");
  await page.route("https://speicher.test/ton?ticket=laden", (route) =>
    route.fulfill({
      status: 200,
      contentType: "audio/wav",
      headers: { "Content-Disposition": 'attachment; filename="ton.wav"' },
      body: wavBytes(1),
    }),
  );
  const vorher = tonZugriffe;
  await ton
    .getByRole("button", { name: "Ton vom Mischpult herunterladen" })
    .click();
  await geladen;
  expect(tonZugriffe).toBe(vorher + 1);
  expect(errors).toEqual([]);
});

/** Ticket einer Tonaufnahme mit bestimmter Restlaufzeit. */
function tonTicketMit(restMs: number | null) {
  return zugriff(tonId, {
    kind: "audio",
    contentType: "audio/wav",
    ...(restMs === null
      ? {}
      : { expiresAt: new Date(Date.now() + restMs).toISOString() }),
  });
}

function quelle(page: Page, element: "audio" | "video") {
  return page.evaluate(
    (name) => (document.querySelector(name) as HTMLMediaElement | null)?.src,
    element,
  );
}

test("Wiedergabe einer langen Aufnahme läuft über den Ticketablauf an derselben Stelle weiter", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await mockSeite(page, memberMe, () => [tonAufnahme()]);
  const lang = wavBytes(30);
  const dateien = new Map<string, { body: Buffer; contentType: string }>();
  let zugriffe = 0;
  let zweite: string | null = null;
  await page.route(`**/api/recordings/${tonId}/access`, (route) => {
    zugriffe += 1;
    // 66 Sekunden: der Spieler erneuert eine Minute vor Ablauf, also nach
    // etwa sechs Sekunden Wiedergabe.
    const ticket = tonTicketMit(zugriffe === 1 ? 66_000 : null);
    if (zugriffe > 1) zweite = ticket.viewUrl;
    dateien.set(ticket.viewUrl as string, {
      body: lang,
      contentType: "audio/wav",
    });
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, dateien);

  await page.goto(`/auftritt/?id=${eventId}`);
  await page.getByRole("button", { name: "Ton vom Mischpult anhören" }).click();
  const spieler = page.getByRole("region", {
    name: "Audio-Spieler · Ton vom Mischpult",
  });
  await spieler
    .getByRole("button", { name: "Ton vom Mischpult abspielen" })
    .click();
  await expect
    .poll(() => position(page, "audio"), { timeout: 10_000 })
    .toBeGreaterThan(0.5);
  await expect
    .poll(
      async () => zweite !== null && (await quelle(page, "audio")) === zweite,
      {
        timeout: 20_000,
      },
    )
    .toBe(true);
  // Ein Neustart von vorn käme in dieser Frist nicht über drei Sekunden.
  await expect
    .poll(() => position(page, "audio"), { timeout: 2_000 })
    .toBeGreaterThan(3);
  expect(
    await page.evaluate(() => !document.querySelector("audio")?.paused),
  ).toBe(true);
  expect(zugriffe).toBe(2);
  await expect(spieler).toBeVisible();
  await expect(page.getByText(/ist abgelaufen/)).toHaveCount(0);
});

test("Ein kurzer Ausfall beim Erneuern beendet die Tonwiedergabe nicht", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await mockSeite(page, memberMe, () => [tonAufnahme()]);
  const lang = wavBytes(30);
  const dateien = new Map<string, { body: Buffer; contentType: string }>();
  let zugriffe = 0;
  let neue: string | null = null;
  await page.route(`**/api/recordings/${tonId}/access`, (route) => {
    zugriffe += 1;
    // Das zweite Ticket bleibt aus (Störung), das dritte kommt.
    if (zugriffe === 2) {
      return route.fulfill(problem("Speicherdienst nicht erreichbar.", 502));
    }
    const ticket = tonTicketMit(zugriffe === 1 ? 24_000 : null);
    if (zugriffe > 2) neue = ticket.viewUrl;
    dateien.set(ticket.viewUrl as string, {
      body: lang,
      contentType: "audio/wav",
    });
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, dateien);

  await page.goto(`/auftritt/?id=${eventId}`);
  const ton = page.getByRole("article", { name: "Ton vom Mischpult" });
  await ton.getByRole("button", { name: "Ton vom Mischpult anhören" }).click();
  const spieler = page.getByRole("region", {
    name: "Audio-Spieler · Ton vom Mischpult",
  });
  await spieler
    .getByRole("button", { name: "Ton vom Mischpult abspielen" })
    .click();
  await expect
    .poll(async () => neue !== null && (await quelle(page, "audio")) === neue, {
      timeout: 25_000,
    })
    .toBe(true);
  expect(zugriffe).toBeGreaterThanOrEqual(3);
  await expect
    .poll(() => position(page, "audio"), { timeout: 2_000 })
    .toBeGreaterThan(3);
  expect(
    await page.evaluate(() => !document.querySelector("audio")?.paused),
  ).toBe(true);
  await expect(spieler).toBeVisible();
  await expect(ton.getByText(/abgelaufen/)).toHaveCount(0);
});

test("Ein angehaltenes Video behält über die Erneuerung seine Stelle", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await mockSeite(page, memberMe, () => [aufnahme()]);
  const dateien = new Map<string, { body: Buffer; contentType: string }>();
  let zugriffe = 0;
  let neue: string | null = null;
  await page.route(`**/api/recordings/${videoId}/access`, (route) => {
    zugriffe += 1;
    const ticket = zugriff(
      videoId,
      zugriffe === 1
        ? { expiresAt: new Date(Date.now() + 26_000).toISOString() }
        : {},
    );
    if (zugriffe > 1) neue = ticket.viewUrl;
    dateien.set(ticket.viewUrl as string, {
      body: Buffer.from(webmBase64, "base64"),
      contentType: "video/webm",
    });
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, dateien);

  await page.goto(`/auftritt/?id=${eventId}`);
  await page
    .getByRole("button", { name: "Gesamtmitschnitt Video ansehen" })
    .click();
  const spieler = page.getByRole("region", {
    name: "Video-Spieler · Gesamtmitschnitt Video",
  });
  await expect(spieler.getByText(/^0:00 \/ 0:0[56]$/)).toBeVisible();
  await spieler.getByLabel("Position (Gesamtmitschnitt Video)").fill("3");
  await expect.poll(() => position(page, "video")).toBeGreaterThanOrEqual(2.9);
  // Angehalten warten, bis die Liste vor dem Ablauf ein frisches Ticket holt.
  await expect
    .poll(async () => neue !== null && (await quelle(page, "video")) === neue, {
      timeout: 25_000,
    })
    .toBe(true);
  await expect(spieler.getByText(/^0:03 \/ 0:0[56]$/)).toBeVisible();
  await expect
    .poll(() => position(page, "video"), { timeout: 5_000 })
    .toBeGreaterThanOrEqual(2.9);
  expect(
    await page.evaluate(() => document.querySelector("video")?.paused),
  ).toBe(true);
  await expect(page.getByText(/abgelaufen/)).toHaveCount(0);
});

test("Ein nicht erneuerbares Ticket wird vor seinem Ablauf verworfen; erneutes Öffnen setzt an der Stelle fort", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await mockSeite(page, memberMe, () => [tonAufnahme()]);
  const lang = wavBytes(30);
  const dateien = new Map<string, { body: Buffer; contentType: string }>();
  let zugriffe = 0;
  let gestoert = true;
  const erstes = tonTicketMit(12_000);
  dateien.set(erstes.viewUrl as string, {
    body: lang,
    contentType: "audio/wav",
  });
  await page.route(`**/api/recordings/${tonId}/access`, (route) => {
    zugriffe += 1;
    if (zugriffe === 1) return route.fulfill(json(erstes));
    if (gestoert) {
      return route.fulfill(problem("Speicherdienst nicht erreichbar.", 502));
    }
    const ticket = tonTicketMit(null);
    dateien.set(ticket.viewUrl as string, {
      body: lang,
      contentType: "audio/wav",
    });
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, dateien);

  await page.goto(`/auftritt/?id=${eventId}`);
  const ton = page.getByRole("article", { name: "Ton vom Mischpult" });
  await ton.getByRole("button", { name: "Ton vom Mischpult anhören" }).click();
  await page
    .getByRole("region", { name: "Audio-Spieler · Ton vom Mischpult" })
    .getByRole("button", { name: "Ton vom Mischpult abspielen" })
    .click();
  // Mehrere Versuche scheitern; noch vor dem Ablauf verschwindet der
  // Spieler samt Ticket, und der Eintrag sagt warum.
  await expect(
    ton.getByText(
      "Der Zugriff auf die Aufnahme ist abgelaufen und konnte nicht erneuert werden. Bitte erneut öffnen; die Wiedergabe setzt an derselben Stelle fort.",
    ),
  ).toBeVisible({ timeout: 20_000 });
  expect(Date.parse(erstes.expiresAt as string)).toBeGreaterThan(Date.now());
  expect(zugriffe).toBeGreaterThanOrEqual(3);
  await expect(page.locator("audio")).toHaveCount(0);

  // Der Dienst ist wieder da: geöffnet wird an der gemerkten Stelle,
  // ohne von selbst loszuspielen.
  gestoert = false;
  await ton.getByRole("button", { name: "Ton vom Mischpult anhören" }).click();
  await expect
    .poll(() => position(page, "audio"), { timeout: 10_000 })
    .toBeGreaterThan(3);
  expect(
    await page.evaluate(() => document.querySelector("audio")?.paused),
  ).toBe(true);
  await expect(ton.getByText(/abgelaufen/)).toHaveCount(0);
});

test("Fehlende Abspielfassung, nicht abspielbare Datei und leere Liste erklären sich", async ({
  page,
}) => {
  let liste: unknown[] = [
    aufnahme({
      id: rohId,
      label: "Kamera Empore",
      durationSeconds: null,
      downloadEnabled: true,
      playback: {
        state: "needsPlaybackCopy",
        source: null,
        revisionId: null,
        contentType: null,
        sizeBytes: null,
      },
    }),
    aufnahme(),
  ];
  await mockSeite(page, memberMe, () => liste);
  // Jedes Ticket trägt eine neue Adresse, wie beim echten Speicherdienst.
  const kaputt = new Map<string, { body: string; contentType: string }>();
  await page.route(`**/api/recordings/${videoId}/access`, (route) => {
    const ticket = zugriff(videoId);
    kaputt.set(ticket.viewUrl as string, {
      body: "<html>kein Video</html>",
      contentType: "video/webm",
    });
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, kaputt);

  await page.goto(`/auftritt/?id=${eventId}`);
  const roh = page.getByRole("article", { name: "Kamera Empore" });
  await expect(
    roh.getByText(
      "Diese Aufnahme liegt im Archiv, kann aber noch nicht im Browser abgespielt werden. Die Originaldatei kann heruntergeladen werden.",
    ),
  ).toBeVisible();
  await expect(roh.getByRole("button", { name: /ansehen/ })).toHaveCount(0);
  await expect(
    roh.getByRole("button", { name: "Kamera Empore herunterladen" }),
  ).toBeVisible();

  // Der Container passt, der Browser kann den Inhalt aber nicht dekodieren.
  const video = page.getByRole("article", { name: "Gesamtmitschnitt Video" });
  await video
    .getByRole("button", { name: "Gesamtmitschnitt Video ansehen" })
    .click();
  await expect(
    video.getByText(
      "Diese Aufnahme kann in diesem Browser nicht wiedergegeben werden.",
    ),
  ).toBeVisible({ timeout: 15_000 });

  liste = [];
  await page.reload();
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Aufnahmen hinterlegt."),
  ).toBeVisible();
});

test("Ladefehler der Aufnahmen bietet den erneuten Versuch", async ({
  page,
}) => {
  let versuche = 0;
  await mockSeite(page, memberMe, () => []);
  await page.route(`**/api/events/${eventId}/recordings`, (route) => {
    versuche += 1;
    return versuche === 1
      ? route.fulfill(problem("Speicherdienst nicht erreichbar.", 502))
      : route.fulfill(json({ eventId, recordings: [aufnahme()] }));
  });
  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Die Aufnahmen konnten nicht geladen werden."),
  ).toBeVisible();
  // Der Auftritt selbst bleibt lesbar.
  await expect(
    page.getByRole("heading", { name: "Adventkonzert" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Aufnahmen erneut laden" }).click();
  await expect(
    page.getByRole("article", { name: "Gesamtmitschnitt Video" }),
  ).toBeVisible();
});

test("Zurückgezogene Aufnahme meldet sich beim Öffnen", async ({ page }) => {
  await mockSeite(page, memberMe, () => [tonAufnahme()]);
  await page.route(`**/api/recordings/${tonId}/access`, (route) =>
    route.fulfill(problem("Aufnahme nicht gefunden.", 404)),
  );
  await page.goto(`/auftritt/?id=${eventId}`);
  const ton = page.getByRole("article", { name: "Ton vom Mischpult" });
  await ton.getByRole("button", { name: "Ton vom Mischpult anhören" }).click();
  await expect(
    ton.getByText("Die Aufnahme ist nicht mehr verfügbar."),
  ).toBeVisible();
  await expect(page.locator("audio")).toHaveCount(0);
});

test("Verweis mit Zeitangabe öffnet die Aufnahme an der Stelle", async ({
  page,
}) => {
  await mockSeite(page, memberMe, () => [aufnahme(), tonAufnahme()]);
  const ticket = zugriff(tonId, { kind: "audio", contentType: "audio/wav" });
  await page.route(`**/api/recordings/${tonId}/access`, (route) =>
    route.fulfill(json(ticket)),
  );
  await mockSpeicher(
    page,
    new Map([
      [
        ticket.viewUrl as string,
        { body: wavBytes(), contentType: "audio/wav" },
      ],
    ]),
  );
  await page.goto(`/auftritt/?id=${eventId}&aufnahme=${tonId}&t=2`);
  const spieler = page.getByRole("region", {
    name: "Audio-Spieler · Ton vom Mischpult",
  });
  await expect(spieler.getByText(/^0:02 \/ 0:04$/)).toBeVisible();
  // Geöffnet, aber nicht von selbst gestartet.
  await expect(
    spieler.getByRole("button", { name: "Ton vom Mischpult abspielen" }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Ton vom Mischpult" }),
  ).toBeFocused();
});

test("Redaktion legt eine Aufnahme an, überträgt das Original und meldet die Dauer", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const originalAsset = "00000000-0000-0000-0000-00000000b034";
  const revisionId = "00000000-0000-0000-0000-00000000f034";
  let stand: AufnahmeForm | null = null;
  await mockSeite(page, editorMe, () => (stand ? [stand] : []));
  const angelegt: unknown[] = [];
  await page.route(`**/api/events/${eventId}/recordings`, (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    const anfrage = route.request().postDataJSON() as {
      label: string;
      kind: "audio" | "video";
    };
    angelegt.push(anfrage);
    stand = aufnahme({
      id: neuId,
      label: anfrage.label.trim(),
      kind: anfrage.kind,
      isPublished: false,
      durationSeconds: null,
      playback: {
        state: "missing",
        source: null,
        revisionId: null,
        contentType: null,
        sizeBytes: null,
      },
      editor: redaktion({
        version: 0,
        publishedAt: null,
        original: { assetId: originalAsset, file: null },
      }),
    });
    return route.fulfill(json({ recording: stand }, 201));
  });
  const sitzungen: unknown[] = [];
  await page.route(`**/api/assets/${originalAsset}/upload-session`, (route) => {
    sitzungen.push(route.request().postDataJSON());
    return route.fulfill(
      json(
        {
          uploadSessionId: "sitz-neu",
          blobName: null,
          uploadUrl: "https://speicher.test/uebertragung?sig=neu",
          expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
          maxBytes: 10737418240,
          blockBytes: 16000,
        },
        201,
      ),
    );
  });
  const bloecke: string[] = [];
  await page.route(/speicher\.test\/uebertragung/, (route) => {
    bloecke.push(new URL(route.request().url()).searchParams.get("comp") ?? "");
    return route.fulfill(json({}, 201));
  });
  const abschluesse: unknown[] = [];
  await page.route("**/api/upload-sessions/sitz-neu/finalize", (route) => {
    abschluesse.push(route.request().postDataJSON());
    if (stand?.editor) {
      const gespeichert = datei({
        revisionId,
        contentType: "audio/wav",
        sizeBytes: 64044,
        fileName: "mischpult.wav",
      });
      stand = {
        ...stand,
        playback: {
          state: "ready",
          source: "original",
          revisionId,
          contentType: "audio/wav",
          sizeBytes: 64044,
        },
        editor: {
          ...stand.editor,
          version: 1,
          original: { assetId: originalAsset, file: gespeichert },
        },
      };
    }
    return route.fulfill(
      json({
        assetId: originalAsset,
        revisionId,
        revisionNumber: 1,
        contentType: "audio/wav",
        sizeBytes: 64044,
        createdAt: "2026-10-05T10:00:00.000Z",
      }),
    );
  });
  const aenderungen: Record<string, unknown>[] = [];
  await page.route(`**/api/recordings/${neuId}`, (route) => {
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    aenderungen.push(anfrage);
    if (stand?.editor) {
      stand = {
        ...stand,
        durationSeconds: anfrage.durationSeconds as number,
        editor: { ...stand.editor, version: stand.editor.version + 1 },
      };
    }
    return route.fulfill(json({ recording: stand }));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  await expect(
    page.getByText("Zu diesem Auftritt sind noch keine Aufnahmen hinterlegt."),
  ).toBeVisible();
  await page.getByText("Aufnahme hinzufügen").click();
  const formular = page.getByRole("form", { name: "Aufnahme hinzufügen" });
  await formular.getByLabel("Bezeichnung").fill("Ton vom Mischpult");
  await formular.getByLabel("Datei (optional)").setInputFiles({
    name: "mischpult.wav",
    mimeType: "audio/wav",
    buffer: wavBytes(4),
  });
  // Die Art folgt der gewählten Datei, bleibt aber wählbar.
  await expect(formular.getByLabel("Tonaufnahme")).toBeChecked();
  await formular.getByRole("button", { name: "Aufnahme anlegen" }).click();

  const eintrag = page.getByRole("article", { name: "Ton vom Mischpult" });
  await expect(eintrag).toBeVisible();
  await expect(
    page.getByText("Original für „Ton vom Mischpult“ gespeichert."),
  ).toBeVisible({ timeout: 20_000 });
  expect(angelegt).toEqual([{ label: "Ton vom Mischpult", kind: "audio" }]);
  expect(sitzungen).toEqual([{ sizeBytes: 64044, fileName: "mischpult.wav" }]);
  // Fünf Blöcke zu 16000 Byte, dann die Blockliste.
  expect(bloecke).toEqual([
    "block",
    "block",
    "block",
    "block",
    "block",
    "blocklist",
  ]);
  expect(abschluesse).toEqual([
    { sizeBytes: 64044, fileName: "mischpult.wav" },
  ]);
  // Die Länge misst der Browser an der lokalen Datei; kein expectedVersion,
  // weil nichts überschrieben wird, das jemand gesehen haben müsste.
  expect(aenderungen).toHaveLength(1);
  expect(aenderungen[0].durationSeconds as number).toBeCloseTo(4, 1);
  expect(aenderungen[0]).not.toHaveProperty("expectedVersion");
  await expect(
    eintrag.getByText("Tonaufnahme · WAV · 62,5 kB · Dauer 0:04"),
  ).toBeVisible();
  await expect(
    eintrag.getByText("Entwurf · Herunterladen gesperrt"),
  ).toBeVisible();
  await expect(
    eintrag.getByText("mischpult.wav · WAV · 62,5 kB · Dateistand 1"),
  ).toBeVisible();
  await expect(
    eintrag.getByText(
      "Im Browser abspielbar: dient zugleich als Abspielfassung.",
    ),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

test("Redaktion veröffentlicht und gibt das Herunterladen frei", async ({
  page,
}) => {
  let stand = aufnahme({
    isPublished: false,
    editor: redaktion({ publishedAt: null }),
  });
  await mockSeite(page, editorMe, () => [stand]);
  const aenderungen: unknown[] = [];
  await page.route(`**/api/recordings/${videoId}`, (route) => {
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    aenderungen.push(anfrage);
    stand = {
      ...stand,
      label: (anfrage.label as string | undefined)?.trim() ?? stand.label,
      isPublished: (anfrage.isPublished as boolean) ?? stand.isPublished,
      downloadEnabled:
        (anfrage.downloadEnabled as boolean) ?? stand.downloadEnabled,
      editor: redaktion({ version: 4 }),
    };
    return route.fulfill(json({ recording: stand }));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const eintrag = page.getByRole("article", { name: "Gesamtmitschnitt Video" });
  await expect(
    eintrag.getByText("Entwurf · Herunterladen gesperrt"),
  ).toBeVisible();
  const speichern = eintrag.getByRole("button", {
    name: "Gesamtmitschnitt Video speichern",
  });
  await expect(speichern).toBeDisabled();
  await eintrag.getByLabel("Für Mitglieder veröffentlichen").check();
  await eintrag.getByLabel("Herunterladen für Mitglieder erlauben").check();
  await speichern.click();

  await expect(
    page.getByText("Aufnahme „Gesamtmitschnitt Video“ gespeichert."),
  ).toBeVisible();
  // Nur das Geänderte und der gesehene Stand gehen an die API.
  expect(aenderungen).toEqual([
    { isPublished: true, downloadEnabled: true, expectedVersion: 3 },
  ]);
  await expect(
    eintrag.getByText("Veröffentlicht · Herunterladen erlaubt"),
  ).toBeVisible();
  await expect(
    eintrag.getByRole("button", {
      name: "Gesamtmitschnitt Video herunterladen",
      exact: true,
    }),
  ).toBeVisible();
  await expect(speichern).toBeDisabled();
});

test("Veralteter Stand lädt neu, ein unerlaubter Zustand lässt die Eingabe stehen", async ({
  page,
}) => {
  let stand = aufnahme({
    isPublished: false,
    durationSeconds: null,
    playback: {
      state: "missing",
      source: null,
      revisionId: null,
      contentType: null,
      sizeBytes: null,
    },
    editor: redaktion({
      publishedAt: null,
      original: { assetId: "00000000-0000-0000-0000-00000000b031", file: null },
    }),
  });
  let listen = 0;
  await mockSeite(page, editorMe, () => {
    listen += 1;
    return [stand];
  });
  let antwort: "unerlaubt" | "veraltet" = "unerlaubt";
  await page.route(`**/api/recordings/${videoId}`, (route) => {
    if (antwort === "unerlaubt") {
      return route.fulfill(
        problem(
          "Die Aufnahme kann erst veröffentlicht werden, wenn eine Datei hochgeladen ist.",
          409,
        ),
      );
    }
    // Jemand anderes hat inzwischen umbenannt.
    stand = {
      ...stand,
      label: "Mitschnitt Saal",
      editor: stand.editor ? { ...stand.editor, version: 9 } : null,
    };
    return route.fulfill(
      problem("Die Aufnahme wurde zwischenzeitlich geändert.", 409),
    );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  let eintrag = page.getByRole("article", { name: "Gesamtmitschnitt Video" });
  await expect(
    eintrag.getByText("Zu dieser Aufnahme wurde noch keine Datei hochgeladen."),
  ).toBeVisible();
  await eintrag.getByLabel("Bezeichnung").fill("Mein neuer Name");
  await eintrag.getByLabel("Für Mitglieder veröffentlichen").check();
  await eintrag
    .getByRole("button", { name: "Gesamtmitschnitt Video speichern" })
    .click();

  // Nicht erlaubt in diesem Zustand: Meldung im Fokus, Eingabe bleibt,
  // nichts wird neu geladen.
  const unerlaubt = eintrag.getByText(
    "Die Aufnahme kann erst veröffentlicht werden, wenn eine Datei hochgeladen ist.",
  );
  await expect(unerlaubt).toBeFocused();
  await expect(eintrag.getByLabel("Bezeichnung")).toHaveValue(
    "Mein neuer Name",
  );
  await expect(
    eintrag.getByLabel("Für Mitglieder veröffentlichen"),
  ).toBeChecked();
  expect(listen).toBe(1);

  // Veralteter Stand: Meldung im Fokus, der aktuelle Stand ersetzt die Eingabe.
  antwort = "veraltet";
  await eintrag.getByLabel("Für Mitglieder veröffentlichen").uncheck();
  await eintrag
    .getByRole("button", { name: "Gesamtmitschnitt Video speichern" })
    .click();
  eintrag = page.getByRole("article", { name: "Mitschnitt Saal" });
  await expect(eintrag).toBeVisible();
  await expect(
    eintrag.getByText(
      "Die Aufnahme wurde zwischenzeitlich geändert. Der aktuelle Stand wurde geladen; bitte die Änderung erneut vornehmen.",
    ),
  ).toBeFocused();
  await expect(eintrag.getByLabel("Bezeichnung")).toHaveValue(
    "Mitschnitt Saal",
  );
  expect(listen).toBe(2);
});

test("Nicht abspielbares Original bleibt erhalten; eine Abspielfassung wird erst nach Prüfung übernommen", async ({
  page,
}) => {
  const kopieAsset = "00000000-0000-0000-0000-00000000b039";
  const mov = datei({
    contentType: "video/quicktime",
    fileName: "kamera.mov",
    sizeBytes: 8589934592,
    playable: false,
  });
  let stand = aufnahme({
    id: rohId,
    label: "Kamera Empore",
    durationSeconds: null,
    playback: {
      state: "needsPlaybackCopy",
      source: null,
      revisionId: null,
      contentType: null,
      sizeBytes: null,
    },
    editor: redaktion({
      original: { assetId: "00000000-0000-0000-0000-00000000b038", file: mov },
    }),
  });
  await mockSeite(page, editorMe, () => [stand]);
  let plaetze = 0;
  await page.route(`**/api/recordings/${rohId}/playback`, (route) => {
    plaetze += 1;
    if (stand.editor && stand.editor.playbackCopy === null) {
      stand = {
        ...stand,
        editor: {
          ...stand.editor,
          version: stand.editor.version + 1,
          playbackCopy: { assetId: kopieAsset, file: null },
        },
      };
    }
    return route.fulfill(json({ recording: stand }));
  });
  let sitzung = 0;
  await page.route(`**/api/assets/${kopieAsset}/upload-session`, (route) => {
    sitzung += 1;
    return route.fulfill(
      json(
        {
          uploadSessionId: `sitz-kopie-${sitzung}`,
          blobName: null,
          uploadUrl: `https://speicher.test/uebertragung?sig=kopie-${sitzung}`,
          expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
          maxBytes: 10737418240,
          blockBytes: 8388608,
        },
        201,
      ),
    );
  });
  await page.route(/speicher\.test\/uebertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route("**/api/upload-sessions/sitz-kopie-1/finalize", (route) =>
    route.fulfill(
      problem(
        "Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme.",
        422,
      ),
    ),
  );
  const kopieRevision = "00000000-0000-0000-0000-00000000f039";
  await page.route("**/api/upload-sessions/sitz-kopie-2/finalize", (route) => {
    if (stand.editor) {
      stand = {
        ...stand,
        playback: {
          state: "ready",
          source: "playbackCopy",
          revisionId: kopieRevision,
          contentType: "video/webm",
          sizeBytes: 1573,
        },
        editor: {
          ...stand.editor,
          playbackCopy: {
            assetId: kopieAsset,
            file: datei({
              revisionId: kopieRevision,
              fileName: "kamera-web.webm",
              sizeBytes: 1573,
            }),
          },
        },
      };
    }
    return route.fulfill(
      json({
        assetId: kopieAsset,
        revisionId: kopieRevision,
        revisionNumber: 1,
        contentType: "video/webm",
        sizeBytes: 1573,
        createdAt: "2026-10-05T10:00:00.000Z",
      }),
    );
  });
  const dauern: unknown[] = [];
  await page.route(`**/api/recordings/${rohId}`, (route) => {
    const anfrage = route.request().postDataJSON() as Record<string, unknown>;
    dauern.push(anfrage);
    stand = { ...stand, durationSeconds: anfrage.durationSeconds as number };
    return route.fulfill(json({ recording: stand }));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const eintrag = page.getByRole("article", { name: "Kamera Empore" });
  await expect(
    eintrag.getByText("kamera.mov · QuickTime (MOV) · 8 GB · Dateistand 1"),
  ).toBeVisible();
  await expect(
    eintrag.getByText(
      "Bleibt als Original erhalten, ist aber im Browser nicht abspielbar.",
    ),
  ).toBeVisible();
  await expect(eintrag.getByText(/^Fehlt\. Bitte eine/)).toBeVisible();
  await expect(eintrag.getByRole("button", { name: /ansehen/ })).toHaveCount(0);

  // Erster Versuch: wieder eine MOV-Datei. Die Prüfung lehnt ab.
  const kopieWahl = page.getByTestId(`aufnahme-datei-abspielfassung-${rohId}`);
  await expect(
    eintrag.getByRole("button", {
      name: "Abspielfassung für Kamera Empore hochladen",
    }),
  ).toBeVisible();
  await kopieWahl.setInputFiles({
    name: "kamera-klein.mov",
    mimeType: "video/quicktime",
    buffer: Buffer.from("kein abspielbares Video"),
  });
  await expect(
    eintrag.getByText(
      "Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme.",
    ),
  ).toBeVisible();
  await expect(
    eintrag.getByText("kamera-klein.mov: gescheitert"),
  ).toBeVisible();
  await expect(eintrag.getByRole("button", { name: /ansehen/ })).toHaveCount(0);
  await expect(eintrag.getByText("kamera.mov · QuickTime (MOV)")).toBeVisible();

  // Zweiter Versuch: ein echtes WebM besteht und wird zur Abspielfassung.
  await kopieWahl.setInputFiles({
    name: "kamera-web.webm",
    mimeType: "video/webm",
    buffer: Buffer.from(webmBase64, "base64"),
  });
  await expect(
    page.getByText("Abspielfassung für „Kamera Empore“ gespeichert."),
  ).toBeVisible({ timeout: 20_000 });
  await expect(
    eintrag.getByText("kamera-web.webm · WebM · 1,5 kB · Dateistand 1"),
  ).toBeVisible();
  await expect(
    eintrag.getByRole("button", { name: "Kamera Empore ansehen" }),
  ).toBeVisible();
  // Das Original steht unverändert daneben.
  await expect(
    eintrag.getByText("kamera.mov · QuickTime (MOV) · 8 GB · Dateistand 1"),
  ).toBeVisible();
  expect(plaetze).toBe(1);
  expect(dauern).toHaveLength(1);
  expect(
    (dauern[0] as { durationSeconds: number }).durationSeconds,
  ).toBeCloseTo(6, 0);
});

test("Fremde Aufnahme: Redaktion darf veröffentlichen, aber keine Dateien ändern", async ({
  page,
}) => {
  await mockSeite(page, editorMe, () => [
    aufnahme({ editor: redaktion({ canChangeFiles: false }) }),
  ]);
  await page.goto(`/auftritt/?id=${eventId}`);
  const eintrag = page.getByRole("article", { name: "Gesamtmitschnitt Video" });
  await expect(
    eintrag.getByText(
      "Dateien kann nur die Person ändern, die die Aufnahme angelegt hat.",
    ),
  ).toBeVisible();
  await expect(
    eintrag.getByRole("button", { name: /hochladen|ersetzen/ }),
  ).toHaveCount(0);
  await expect(
    eintrag.getByLabel("Für Mitglieder veröffentlichen"),
  ).toBeEnabled();
});

test("Verweise mit unbrauchbaren Angaben stören nicht", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSeite(page, memberMe, () => [aufnahme(), tonAufnahme()]);
  let zugriffe = 0;
  const dateien = new Map<string, { body: Buffer; contentType: string }>();
  await page.route(`**/api/recordings/${tonId}/access`, (route) => {
    zugriffe += 1;
    const ticket = tonTicketMit(null);
    dateien.set(ticket.viewUrl as string, {
      body: wavBytes(),
      contentType: "audio/wav",
    });
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, dateien);
  const spieler = page.getByRole("region", {
    name: "Audio-Spieler · Ton vom Mischpult",
  });

  // Unbekannte Aufnahme: nichts öffnet sich, nichts wird gemeldet.
  await page.goto(
    `/auftritt/?id=${eventId}&aufnahme=00000000-0000-0000-0000-00000000dead&t=5`,
  );
  await expect(
    page.getByRole("article", { name: "Ton vom Mischpult" }),
  ).toBeVisible();
  await expect(page.locator("audio, video")).toHaveCount(0);
  await expect(page.locator(".aufnahmen .feld-fehler")).toHaveCount(0);
  expect(zugriffe).toBe(0);

  // Leere, unlesbare und negative Zeit: die Aufnahme öffnet sich am Anfang.
  for (const zeit of ["", "abc", "-4"]) {
    await page.goto(`/auftritt/?id=${eventId}&aufnahme=${tonId}&t=${zeit}`);
    await expect(spieler.getByText(/^0:00 \/ 0:04$/)).toBeVisible();
  }
  // Eine Zeit hinter dem Ende beginnt vorn statt am Schluss.
  await page.goto(`/auftritt/?id=${eventId}&aufnahme=${tonId}&t=99`);
  await expect(spieler.getByText(/^0:00 \/ 0:04$/)).toBeVisible();
  expect(await position(page, "audio")).toBe(0);
  expect(errors).toEqual([]);
});

test("Die Zeitangabe des Verweises übersteht einen gescheiterten ersten Ladeversuch", async ({
  page,
}) => {
  await mockSeite(page, memberMe, () => [tonAufnahme()]);
  let zugriffe = 0;
  const dateien = new Map<string, { body: Buffer; contentType: string }>();
  await page.route(`**/api/recordings/${tonId}/access`, (route) => {
    zugriffe += 1;
    const ticket = tonTicketMit(null);
    // Die erste Adresse liefert der Speicherdienst nicht aus.
    if (zugriffe > 1) {
      dateien.set(ticket.viewUrl as string, {
        body: wavBytes(),
        contentType: "audio/wav",
      });
    }
    return route.fulfill(json(ticket));
  });
  await mockSpeicher(page, dateien);
  await page.goto(`/auftritt/?id=${eventId}&aufnahme=${tonId}&t=2`);
  const spieler = page.getByRole("region", {
    name: "Audio-Spieler · Ton vom Mischpult",
  });
  await expect(spieler.getByText(/^0:02 \/ 0:04$/)).toBeVisible({
    timeout: 15_000,
  });
  expect(zugriffe).toBe(2);
  await expect.poll(() => position(page, "audio")).toBeGreaterThanOrEqual(1.9);
});

/** Redaktionsseite mit einer Aufnahme, deren Abspielfassung noch fehlt. */
async function mockAbspielfassungFehlt(page: Page, kopieAsset: string) {
  const stand = aufnahme({
    id: rohId,
    label: "Kamera Empore",
    durationSeconds: null,
    playback: {
      state: "needsPlaybackCopy",
      source: null,
      revisionId: null,
      contentType: null,
      sizeBytes: null,
    },
    editor: redaktion({
      original: {
        assetId: "00000000-0000-0000-0000-00000000b038",
        file: datei({
          contentType: "video/quicktime",
          fileName: "kamera.mov",
          playable: false,
        }),
      },
      playbackCopy: { assetId: kopieAsset, file: null },
    }),
  });
  await mockSeite(page, editorMe, () => [stand]);
  await page.route(/speicher\.test\/uebertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route(`**/api/recordings/${rohId}`, (route) =>
    route.fulfill(json({ recording: stand })),
  );
}

/**
 * Wählt eine Datei mit festem Änderungsdatum – die Wiedererkennung einer
 * unterbrochenen Übertragung hängt an Name, Größe und diesem Datum.
 */
async function waehleDieselbeDatei(page: Page, testId: string) {
  await page.getByTestId(testId).evaluate(
    (element, [base64, name]) => {
      const bytes = Uint8Array.from(atob(base64), (z) => z.charCodeAt(0));
      const transfer = new DataTransfer();
      transfer.items.add(
        new File([bytes], name, {
          type: "video/webm",
          lastModified: 1_760_000_000_000,
        }),
      );
      (element as HTMLInputElement).files = transfer.files;
      element.dispatchEvent(new Event("change", { bubbles: true }));
    },
    [webmBase64, "kamera-web.webm"] as const,
  );
}

function sitzungsAntwort(kennung: string) {
  return json(
    {
      uploadSessionId: kennung,
      blobName: null,
      uploadUrl: `https://speicher.test/uebertragung?sig=${kennung}`,
      expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
      maxBytes: 10737418240,
      blockBytes: 8388608,
    },
    201,
  );
}

test("Nach einer abgelehnten Datei beginnt dieselbe Datei eine frische Übertragung", async ({
  page,
}) => {
  const kopieAsset = "00000000-0000-0000-0000-00000000b041";
  await mockAbspielfassungFehlt(page, kopieAsset);
  let sitzungen = 0;
  await page.route(`**/api/assets/${kopieAsset}/upload-session`, (route) => {
    sitzungen += 1;
    return route.fulfill(sitzungsAntwort(`sitz-ablehnung-${sitzungen}`));
  });
  let erneuerungen = 0;
  await page.route("**/api/upload-sessions/*/renew", (route) => {
    erneuerungen += 1;
    return route.fulfill(problem("Upload wurde abgebrochen.", 409));
  });
  const abschluesse: string[] = [];
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    const kennung = new URL(route.request().url()).pathname.split("/")[3];
    abschluesse.push(kennung);
    // Die erste Prüfung lehnt ab und beendet ihre Sitzung endgültig.
    return kennung === "sitz-ablehnung-1"
      ? route.fulfill(
          problem(
            "Die Datei ist keine im Browser abspielbare Fassung. Geeignet sind MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme.",
            422,
          ),
        )
      : route.fulfill(
          json({
            assetId: kopieAsset,
            revisionId: "00000000-0000-0000-0000-00000000f041",
            revisionNumber: 1,
            contentType: "video/webm",
            sizeBytes: 1573,
            createdAt: "2026-10-05T10:00:00.000Z",
          }),
        );
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const eintrag = page.getByRole("article", { name: "Kamera Empore" });
  const wahl = `aufnahme-datei-abspielfassung-${rohId}`;
  await waehleDieselbeDatei(page, wahl);
  await expect(eintrag.getByText("kamera-web.webm: gescheitert")).toBeVisible();
  // Nichts bleibt als „unterbrochen“ gemerkt.
  await expect(eintrag.getByText(/wurde unterbrochen/)).toHaveCount(0);
  expect(
    await page.evaluate(
      (schluessel) => window.localStorage.getItem(schluessel),
      `arc-upload-${kopieAsset}`,
    ),
  ).toBeNull();

  await waehleDieselbeDatei(page, wahl);
  await expect(
    page.getByText("Abspielfassung für „Kamera Empore“ gespeichert."),
  ).toBeVisible({ timeout: 20_000 });
  expect(sitzungen).toBe(2);
  expect(erneuerungen).toBe(0);
  expect(abschluesse).toEqual(["sitz-ablehnung-1", "sitz-ablehnung-2"]);
});

test("Eine gemerkte, serverseitig beendete Übertragung weicht einer frischen Sitzung", async ({
  page,
}) => {
  const kopieAsset = "00000000-0000-0000-0000-00000000b042";
  const inhalt = Buffer.from(webmBase64, "base64");
  await mockAbspielfassungFehlt(page, kopieAsset);
  await page.addInitScript(
    ([schluessel, groesse]) => {
      window.localStorage.setItem(
        schluessel as string,
        JSON.stringify({
          uploadSessionId: "sitz-alt",
          fileName: "kamera-web.webm",
          sizeBytes: groesse,
          lastModified: 1_760_000_000_000,
          blockBytes: 8388608,
        }),
      );
    },
    [`arc-upload-${kopieAsset}`, inhalt.length] as const,
  );
  const erneuerungen: string[] = [];
  await page.route("**/api/upload-sessions/*/renew", (route) => {
    erneuerungen.push(new URL(route.request().url()).pathname.split("/")[3]);
    return route.fulfill(problem("Upload wurde abgebrochen.", 409));
  });
  let sitzungen = 0;
  await page.route(`**/api/assets/${kopieAsset}/upload-session`, (route) => {
    sitzungen += 1;
    return route.fulfill(sitzungsAntwort("sitz-frisch"));
  });
  await page.route("**/api/upload-sessions/sitz-frisch/finalize", (route) =>
    route.fulfill(
      json({
        assetId: kopieAsset,
        revisionId: "00000000-0000-0000-0000-00000000f042",
        revisionNumber: 1,
        contentType: "video/webm",
        sizeBytes: 1573,
        createdAt: "2026-10-05T10:00:00.000Z",
      }),
    ),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const eintrag = page.getByRole("article", { name: "Kamera Empore" });
  await expect(
    eintrag.getByText(
      "Die Übertragung von „kamera-web.webm“ wurde unterbrochen. Dieselbe Datei erneut wählen, um sie fortzusetzen.",
    ),
  ).toBeVisible();
  await waehleDieselbeDatei(page, `aufnahme-datei-abspielfassung-${rohId}`);
  await expect(
    page.getByText("Abspielfassung für „Kamera Empore“ gespeichert."),
  ).toBeVisible({ timeout: 20_000 });
  // Ein Versuch, die alte Sitzung fortzusetzen, dann die frische.
  expect(erneuerungen).toEqual(["sitz-alt"]);
  expect(sitzungen).toBe(1);
});
