import { expect, type Page, test } from "@playwright/test";

const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

const songId = "00000000-0000-0000-0000-00000000000a";
const arrangementId = "00000000-0000-0000-0000-00000000c00a";
const versionId = "00000000-0000-0000-0000-00000000d00a";
const midiAssetId = "00000000-0000-0000-0000-00000000e10a";
const altAssetId = "00000000-0000-0000-0000-00000000e20a";

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

function fassung(assets: unknown) {
  return {
    id: versionId,
    label: "Standardfassung",
    creator: "Josef Gabriel",
    musicalKey: null,
    assets,
  };
}

function midiRevision(over: Record<string, unknown> = {}) {
  return {
    revisionId: "00000000-0000-0000-0000-00000000f10a",
    revisionNumber: 3,
    contentType: "audio/midi",
    sizeBytes: stueckBytes.length,
    createdAt: "2026-09-18T10:00:00.000Z",
    ...over,
  };
}

function midiAsset(over: Record<string, unknown> = {}) {
  return {
    id: midiAssetId,
    assetType: "midi",
    voiceLabel: "Sopran",
    description: null,
    currentRevision: midiRevision(),
    ...over,
  };
}

function audioRevision(over: Record<string, unknown> = {}) {
  return {
    revisionId: "00000000-0000-0000-0000-00000000f20a",
    revisionNumber: 3,
    contentType: "audio/wav",
    sizeBytes: 3145728,
    createdAt: "2026-09-18T10:00:00.000Z",
    ...over,
  };
}

function audioAsset(over: Record<string, unknown> = {}) {
  return {
    id: altAssetId,
    assetType: "audio",
    voiceLabel: "Alt",
    description: null,
    currentRevision: audioRevision(),
    ...over,
  };
}

function detail(songAssets: unknown[]) {
  return {
    song: {
      id: songId,
      title: "Das Wandern ist des Müllers Lust",
      composer: "Carl Friedrich Zöllner",
      lyricist: "Wilhelm Müller",
      published: true,
      publishedAt: "2026-09-01T10:00:00.000Z",
      createdAt: "2026-08-20T08:00:00.000Z",
      updatedAt: "2026-09-01T10:00:00.000Z",
      arrangements: [
        {
          id: arrangementId,
          label: "Satz für gemischten Chor",
          arranger: null,
          voiceConfiguration: "S,A,T,B + Schlagwerk",
          musicalVersions: [fassung(songAssets)],
        },
      ],
    },
  };
}

async function mockSitzung(page: Page, me: unknown) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

/** Kodiert eine Zahl als MIDI-Laufzeitwert (Variable Length Quantity). */
function vlq(wert: number): number[] {
  const bytes = [wert & 0x7f];
  let rest = wert >> 7;
  while (rest > 0) {
    bytes.unshift((rest & 0x7f) | 0x80);
    rest >>= 7;
  }
  return bytes;
}

function midiSpur(ereignisse: number[]): Buffer {
  const daten = Buffer.from(ereignisse);
  const kopf = Buffer.alloc(8);
  kopf.write("MTrk", 0);
  kopf.writeUInt32BE(daten.length, 4);
  return Buffer.concat([kopf, daten]);
}

/**
 * Erzeugt eine gültige mehrspurige Standard-MIDI-Datei (Format 1): Tempo-Spur,
 * eine Notenspur mit abwechselnden Viertelnoten C5/E5 und eine Begleitungs­spur
 * mit gleichzeitig klingenden Ganzennoten (C4/G4) – bei 120 BPM (500000 µs pro
 * Viertel, Division 480) genau 8 Sekunden Stücklänge mit Akkordklängen.
 */
function midiBytes(viertelNoten = 16) {
  const division = 480;
  // Tempo-Spur: Set-Tempo 500000 µs/Viertel plus Ende-der-Spur.
  const tempoSpur = midiSpur([
    0x00, 0xff, 0x51, 0x03, 0x07, 0xa1, 0x20, 0x00, 0xff, 0x2f, 0x00,
  ]);
  // Notenspur: Note-an, 480 Ticks klingen, Note-aus, nächster Anschlag.
  const ereignisse: number[] = [];
  const toene = [72, 76];
  for (let index = 0; index < viertelNoten; index += 1) {
    const ton = toene[index % 2];
    ereignisse.push(...vlq(0), 0x90, ton, 90);
    ereignisse.push(...vlq(division), 0x80, ton, 0);
  }
  ereignisse.push(0x00, 0xff, 0x2f, 0x00);
  const notenSpur = midiSpur(ereignisse);
  // Begleitung: alle zwei Sekunden klingt ein ganztaktiger Akkord
  // (C4 und G4 gleichzeitig), während oben die Melodie läuft.
  const begleitung: number[] = [];
  for (let akkord = 0; akkord < viertelNoten / 4; akkord += 1) {
    for (const ton of [60, 67]) {
      begleitung.push(...vlq(0), 0x90, ton, 70);
    }
    begleitung.push(...vlq(division * 4), 0x80, 60, 0);
    begleitung.push(...vlq(0), 0x80, 67, 0);
  }
  begleitung.push(0x00, 0xff, 0x2f, 0x00);
  const begleitungsSpur = midiSpur(begleitung);
  const kopf = Buffer.alloc(14);
  kopf.write("MThd", 0);
  kopf.writeUInt32BE(6, 4);
  kopf.writeUInt16BE(1, 8); // Format 1
  kopf.writeUInt16BE(3, 10); // drei Spuren
  kopf.writeUInt16BE(division, 12);
  return Buffer.concat([kopf, tempoSpur, notenSpur, begleitungsSpur]);
}

const stueckBytes = midiBytes();

type Auslieferung = {
  status: number;
  body?: Buffer | string;
  contentType: string;
};

/** status 0 bricht die Übertragung ab (echter Netzwerkfehler). */
function blobDienst(auslieferungen: Map<string, Auslieferung>) {
  return async (route: {
    request: () => { url: () => string };
    fulfill: (options: {
      status: number;
      contentType: string;
      body: Buffer;
    }) => Promise<unknown>;
    abort: () => Promise<unknown>;
  }) => {
    const eintrag = auslieferungen.get(route.request().url());
    if (!eintrag) {
      return route.fulfill({
        status: 404,
        contentType: "text/plain",
        body: Buffer.from("fehlt"),
      });
    }
    if (eintrag.status === 0) {
      return route.abort();
    }
    return route.fulfill({
      status: eintrag.status,
      contentType: eintrag.contentType,
      body: Buffer.from(eintrag.body ?? ""),
    });
  };
}

let zugriffsNummer = 0;

function zugriff(assetId: string, aenderung: Record<string, unknown> = {}) {
  zugriffsNummer += 1;
  const nummer = zugriffsNummer;
  const standardlaufzeit = 15 * 60 * 1000;
  return {
    assetId,
    revisionId: "00000000-0000-0000-0000-00000000f10a",
    revisionNumber: 3,
    contentType: "audio/midi",
    sizeBytes: stueckBytes.length,
    createdAt: "2026-09-18T10:00:00.000Z",
    viewUrl: `https://speicher.test/midi-${nummer}?ticket=ansicht`,
    downloadUrl: `https://speicher.test/midi-${nummer}?ticket=laden`,
    expiresAt: new Date(Date.now() + standardlaufzeit).toISOString(),
    ...aenderung,
  };
}

/** Die Position folgt dem Suchregler; die Stückzeit ist die Wahrheit. */
async function lesePosition(page: Page) {
  const wert = await page
    .getByRole("region", { name: "MIDI-Spieler · Sopran" })
    .getByLabel("Position (Sopran)")
    .inputValue();
  return Number(wert);
}

test("Mitglied hört MIDI-Stück mit Bedienelementen und behält den Download", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([midiAsset()]))),
  );
  const antwort = zugriff(midiAssetId);
  const auslieferungen = new Map<string, Auslieferung>([
    [
      antwort.viewUrl,
      { status: 200, body: stueckBytes, contentType: "audio/midi" },
    ],
  ]);
  let zugriffe = 0;
  await page.route(`**/api/assets/${midiAssetId}/access`, (route) => {
    zugriffe += 1;
    return route.fulfill(json(antwort));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText(/^MIDI · Fassung 3 · .+ · audio\/midi$/),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Anhören" })).toBeVisible();
  expect(zugriffe).toBe(0);

  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "MIDI-Spieler · Sopran" });
  await expect(spieler).toBeVisible();
  await expect.poll(() => zugriffe).toBe(1);
  await expect(spieler.getByText("0:00 / 0:08")).toBeVisible();

  await spieler
    .getByRole("button", { name: "Sopran (MIDI) abspielen" })
    .click();
  await expect(
    spieler.getByRole("button", { name: "Sopran (MIDI) pausieren" }),
  ).toBeVisible();
  await expect
    .poll(() => lesePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.2);

  // Bei 100 % läuft die Stückzeit höchstens so schnell wie die Echtzeit.
  const langsamVorher = await lesePosition(page);
  const langsamStart = Date.now();
  await page.waitForTimeout(1000);
  const langsamDelta = (await lesePosition(page)) - langsamVorher;
  expect(langsamDelta).toBeLessThan((Date.now() - langsamStart) / 1000 + 0.8);

  // Suchen springt an dieselbe Position.
  await spieler.getByLabel("Position (Sopran)").fill("4");
  await expect(spieler.getByText("0:04 / 0:08")).toBeVisible();

  // Herunterladen bleibt erhalten.
  const laden = page
    .getByRole("article", { name: "MIDI · Sopran" })
    .getByRole("link", { name: "Herunterladen" });
  await expect(laden).toHaveAttribute("href", antwort.downloadUrl);
  await expect(laden).toHaveAttribute("download", "");
  expect(zugriffe).toBe(1);
  expect(errors).toEqual([]);
});

test("Tempoänderung beschleunigt laufende Wiedergabe ohne Positionsprung", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([midiAsset()]))),
  );
  const antwort = zugriff(midiAssetId);
  const auslieferungen = new Map<string, Auslieferung>([
    [
      antwort.viewUrl,
      { status: 200, body: stueckBytes, contentType: "audio/midi" },
    ],
  ]);
  await page.route(`**/api/assets/${midiAssetId}/access`, (route) =>
    route.fulfill(json(antwort)),
  );
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "MIDI-Spieler · Sopran" });
  await spieler
    .getByRole("button", { name: "Sopran (MIDI) abspielen" })
    .click();
  await expect
    .poll(() => lesePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.5);

  const vorTempo = await lesePosition(page);
  await spieler.getByLabel("Tempo (Sopran)").fill("150");
  await expect(spieler.getByText("150 %")).toBeVisible();
  // Die Wiedergabe läuft weiter und springt nicht zurück.
  await expect(
    spieler.getByRole("button", { name: "Sopran (MIDI) pausieren" }),
  ).toBeVisible();
  const nachTempo = await lesePosition(page);
  expect(nachTempo).toBeGreaterThanOrEqual(vorTempo - 0.2);

  // Über ~1,5 s Echtzeit wächst die Stückzeit deutlich schneller:
  // 150 % von 1,5 s sind 2,25 s Stückzeit; die Untergrenze bleibt großzügig.
  const messung1 = await lesePosition(page);
  await page.waitForTimeout(1500);
  const messung2 = await lesePosition(page);
  expect(messung2).toBeGreaterThan(messung1);
  expect(messung2 - messung1).toBeGreaterThan(1.0);
  expect(errors).toEqual([]);
});

test("Zerbrochene MIDI-Datei meldet verständlichen Zustand und lässt anderes Material unberührt", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(
      json(detail([midiAsset(), audioAsset({ voiceLabel: "Alt" })])),
    ),
  );
  const antwort = zugriff(midiAssetId);
  const auslieferungen = new Map<string, Auslieferung>([
    [
      antwort.viewUrl,
      {
        status: 200,
        body: "<html>nicht einmal MIDI</html>",
        contentType: "audio/midi",
      },
    ],
  ]);
  let zugriffe = 0;
  await page.route(`**/api/assets/${midiAssetId}/access`, (route) => {
    zugriffe += 1;
    return route.fulfill(json(antwort));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page
    .getByRole("article", { name: "MIDI · Sopran" })
    .getByRole("button", { name: "Anhören" })
    .click();
  const spieler = page.getByRole("region", { name: "MIDI-Spieler · Sopran" });

  // Das Parsen scheitert beim Laden: Warnung statt Spieler, kein Abspielen.
  await expect(
    spieler.getByText(
      "Diese MIDI-Datei kann nicht abgespielt werden. Die Datei kann weiterhin heruntergeladen werden.",
    ),
  ).toBeVisible({ timeout: 20_000 });
  await expect(
    spieler.getByRole("button", { name: "Sopran (MIDI) abspielen" }),
  ).toBeDisabled();
  expect(zugriffe).toBe(1);

  // Der Download bleibt möglich.
  await expect(
    page
      .getByRole("article", { name: "MIDI · Sopran" })
      .getByRole("link", { name: "Herunterladen" }),
  ).toHaveAttribute("href", antwort.downloadUrl);

  // Anderes Material bleibt unberührt: Alt ist weiterhin anhörbar.
  const alt = page.getByRole("article", { name: "Audio · Alt" });
  await expect(alt.getByRole("button", { name: "Anhören" })).toBeEnabled();
  await expect(
    page.getByRole("region", { name: "Audio-Spieler · Alt" }),
  ).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("Vorübergehende Störung bietet Erneut versuchen und erholt sich", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([midiAsset()]))),
  );

  const auslieferungen = new Map<string, Auslieferung>();
  let zugriffe = 0;
  let erfolgreich: ReturnType<typeof zugriff> | null = null;
  await page.route(`**/api/assets/${midiAssetId}/access`, (route) => {
    zugriffe += 1;
    if (zugriffe === 1) {
      // Erster Aufruf liefert Tickets, aber die Übertragung bricht ab.
      const gestoert = zugriff(midiAssetId);
      auslieferungen.set(gestoert.viewUrl, {
        status: 0,
        contentType: "audio/midi",
      });
      return route.fulfill(json(gestoert));
    }
    erfolgreich = zugriff(midiAssetId);
    auslieferungen.set(erfolgreich.viewUrl, {
      status: 200,
      body: stueckBytes,
      contentType: "audio/midi",
    });
    return route.fulfill(json(erfolgreich));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "MIDI-Spieler · Sopran" });

  await expect(
    spieler.getByText(
      "MIDI konnte nicht geladen werden. Bitte erneut versuchen.",
    ),
  ).toBeVisible({ timeout: 20_000 });

  await spieler.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(spieler.getByText("0:00 / 0:08")).toBeVisible({
    timeout: 20_000,
  });
  expect(zugriffe).toBe(2);
  expect(erfolgreich).not.toBeNull();
  await expect(
    spieler.getByRole("button", { name: "Sopran (MIDI) abspielen" }),
  ).toBeEnabled();

  await spieler
    .getByRole("button", { name: "Sopran (MIDI) abspielen" })
    .click();
  await expect
    .poll(() => lesePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.2);
  expect(errors).toEqual([]);
});

test("Pause und Wiederaufnahme setzen an derselben Position fort", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([midiAsset()]))),
  );
  const antwort = zugriff(midiAssetId);
  const auslieferungen = new Map<string, Auslieferung>([
    [
      antwort.viewUrl,
      { status: 200, body: stueckBytes, contentType: "audio/midi" },
    ],
  ]);
  let zugriffe = 0;
  await page.route(`**/api/assets/${midiAssetId}/access`, (route) => {
    zugriffe += 1;
    return route.fulfill(json(antwort));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "MIDI-Spieler · Sopran" });
  const abspielen = spieler.getByRole("button", {
    name: "Sopran (MIDI) abspielen",
  });
  const pausieren = spieler.getByRole("button", {
    name: "Sopran (MIDI) pausieren",
  });
  await abspielen.click();
  await expect
    .poll(() => lesePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.5);

  // Pause friert die Position ein; sie wandert nicht weiter.
  await pausieren.click();
  await expect(abspielen).toBeVisible();
  const pausenPosition = await lesePosition(page);
  await page.waitForTimeout(1000);
  expect(await lesePosition(page)).toBe(pausenPosition);

  // Wiederaufnahme läuft von der eingefrorenen Position weiter.
  await abspielen.click();
  await expect
    .poll(() => lesePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(pausenPosition + 0.2);
  expect(zugriffe).toBe(1);
  expect(errors).toEqual([]);
});

test("Verweigerter Zugriff meldet die abgelaufene Anmeldung und erhält den Download", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([midiAsset()]))),
  );

  // Die Tickets werden geliefert, der Speicher verweigert die Bytes aber.
  const abgelaufen = zugriff(midiAssetId);
  let zugriffe = 0;
  await page.route(`**/api/assets/${midiAssetId}/access`, (route) => {
    zugriffe += 1;
    if (zugriffe === 1) return route.fulfill(json(abgelaufen));
    return route.fulfill(
      problem("Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.", 401),
    );
  });
  await page.route("**/speicher.test/**", (route) =>
    route.fulfill({
      status: 401,
      contentType: "text/plain",
      body: Buffer.from("abgelaufen"),
    }),
  );

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "MIDI-Spieler · Sopran" });
  await expect(
    spieler.getByText(
      "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.",
    ),
  ).toBeVisible({ timeout: 20_000 });
  await expect(
    page
      .getByRole("article", { name: "MIDI · Sopran" })
      .getByRole("link", { name: "Herunterladen" }),
  ).toHaveAttribute("href", abgelaufen.downloadUrl);

  // Auch der Wiederholungsversuch bleibt verweigert; die Meldung bleibt stehen.
  await spieler.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect.poll(() => zugriffe).toBe(2);
  await expect(
    spieler.getByText(
      "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.",
    ),
  ).toBeVisible();
  expect(errors).toEqual([]);
});
