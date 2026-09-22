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
const assetId = "00000000-0000-0000-0000-00000000e00a";

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

function audioRevision(over: Record<string, unknown> = {}) {
  return {
    revisionId: "00000000-0000-0000-0000-00000000f00a",
    revisionNumber: 3,
    contentType: "audio/wav",
    sizeBytes: 3145728,
    createdAt: "2026-09-18T10:00:00.000Z",
    ...over,
  };
}

function audioAsset(over: Record<string, unknown> = {}) {
  return {
    id: assetId,
    assetType: "audio",
    voiceLabel: "Sopran",
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

/** Erzeugt eine abspielbare Mono-WAV-Datei (440-Hz-Sinuston). */
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

function zugriff(aenderung: Record<string, unknown> = {}) {
  zugriffsNummer += 1;
  const nummer = zugriffsNummer;
  const standardlaufzeit = 15 * 60 * 1000;
  return {
    assetId,
    revisionId: "00000000-0000-0000-0000-00000000f00a",
    revisionNumber: 3,
    contentType: "audio/wav",
    sizeBytes: 3145728,
    createdAt: "2026-09-18T10:00:00.000Z",
    viewUrl: `https://speicher.test/lied-${nummer}?ticket=ansicht`,
    downloadUrl: `https://speicher.test/lied-${nummer}?ticket=laden`,
    expiresAt: new Date(Date.now() + standardlaufzeit).toISOString(),
    ...aenderung,
  };
}

async function aktuellePosition(page: Page) {
  return page.evaluate(() => document.querySelector("audio")?.currentTime ?? 0);
}

test("Mitglied hört Sprachaufnahme mit Bedienelementen und behält den Download", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([audioAsset()]))),
  );
  const antwort = zugriff();
  const auslieferungen = new Map<string, Auslieferung>([
    [
      antwort.viewUrl,
      { status: 200, body: wavBytes(), contentType: "audio/wav" },
    ],
  ]);
  let zugriffe = 0;
  await page.route(`**/api/assets/${assetId}/access`, (route) => {
    zugriffe += 1;
    return route.fulfill(json(antwort));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Audio · Fassung 3 · 3 MB · audio/wav"),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Anhören" })).toBeVisible();
  expect(zugriffe).toBe(0);

  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "Audio-Spieler · Sopran" });
  await expect(spieler).toBeVisible();

  await spieler.getByRole("button", { name: "Sopran abspielen" }).click();
  await expect(
    spieler.getByRole("button", { name: "Sopran pausieren" }),
  ).toBeVisible();
  await expect
    .poll(() => aktuellePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.2);

  // Suchen springt an dieselbe Position.
  await spieler.getByLabel("Position (Sopran)").fill("2");
  await expect(spieler.getByText(/^0:02 \/ 0:04$/)).toBeVisible();

  // Herunterladen bleibt erhalten.
  const laden = page
    .getByRole("article", { name: "Audio · Sopran" })
    .getByRole("link", { name: "Herunterladen" });
  await expect(laden).toHaveAttribute("href", antwort.downloadUrl);
  await expect(laden).toHaveAttribute("download", "");
  expect(zugriffe).toBe(1);
  expect(errors).toEqual([]);
});

test("Wiedergabe läuft nach erneuertem Ticket an derselben Position weiter", async ({
  page,
}) => {
  test.setTimeout(90_000);
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([audioAsset()]))),
  );

  const erste = zugriff({
    expiresAt: new Date(Date.now() + 3_000).toISOString(),
  });
  let zweite: string | null = null;
  const langesLied = wavBytes(30);
  const auslieferungen = new Map<string, Auslieferung>([
    [
      erste.viewUrl,
      { status: 200, body: langesLied, contentType: "audio/wav" },
    ],
  ]);
  let zugriffe = 0;
  await page.route(`**/api/assets/${assetId}/access`, (route) => {
    zugriffe += 1;
    if (zugriffe === 1) return route.fulfill(json(erste));
    const neu = zugriff();
    zweite = neu.viewUrl;
    auslieferungen.set(neu.viewUrl, {
      status: 200,
      body: langesLied,
      contentType: "audio/wav",
    });
    return route.fulfill(json(neu));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "Audio-Spieler · Sopran" });
  await spieler.getByRole("button", { name: "Sopran abspielen" }).click();
  await expect
    .poll(() => aktuellePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.5);

  // Der Spieler fordert kurz vor Ablauf frische Tickets an und setzt ohne
  // Neustart an derselben Position fort.
  await expect
    .poll(
      async () => {
        const quelle = await page.evaluate(
          () => document.querySelector("audio")?.src,
        );
        return zweite !== null && quelle === zweite;
      },
      { timeout: 20_000 },
    )
    .toBe(true);
  expect(zugriffe).toBe(2);
  await expect
    .poll(() => aktuellePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.2);
  const laeuftWeiter = await page.evaluate(
    () => !document.querySelector("audio")?.paused,
  );
  expect(laeuftWeiter).toBe(true);
  expect(errors).toEqual([]);
});

test("Nicht abspielbares Format meldet einen verständlichen Zustand", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([audioAsset()]))),
  );
  const antwort = zugriff();
  const auslieferungen = new Map<string, Auslieferung>([
    [
      antwort.viewUrl,
      {
        status: 200,
        body: "<html>nicht einmal Audio</html>",
        contentType: "audio/mpeg",
      },
    ],
  ]);
  await page.route(`**/api/assets/${assetId}/access`, (route) => {
    return route.fulfill(json(antwort));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "Audio-Spieler · Sopran" });
  await spieler.getByRole("button", { name: "Sopran abspielen" }).click();
  await expect(
    spieler.getByText(
      "Dieses Audioformat kann im Browser nicht wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden.",
    ),
  ).toBeVisible();
  await expect(
    page
      .getByRole("article", { name: "Audio · Sopran" })
      .getByRole("link", { name: "Herunterladen" }),
  ).toHaveAttribute("href", antwort.downloadUrl);
  expect(errors).toEqual([]);
});

test("Vorübergehende Störung bietet Erneut versuchen und erholt sich", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([audioAsset()]))),
  );

  const auslieferungen = new Map<string, Auslieferung>();
  let zugriffe = 0;
  let erfolgreich: ReturnType<typeof zugriff> | null = null;
  await page.route(`**/api/assets/${assetId}/access`, (route) => {
    zugriffe += 1;
    if (zugriffe === 1) {
      // Erster Aufruf liefert Tickets, aber die Übertragung bricht ab.
      const gestoert = zugriff();
      return route.fulfill(json(gestoert));
    }
    if (zugriffe === 2) {
      return route.fulfill(problem("Speicherdienst nicht erreichbar.", 503));
    }
    erfolgreich = zugriff();
    auslieferungen.set(erfolgreich.viewUrl, {
      status: 200,
      body: wavBytes(),
      contentType: "audio/wav",
    });
    return route.fulfill(json(erfolgreich));
  });
  await page.route("**/speicher.test/**", blobDienst(auslieferungen));

  await page.goto(`/lied/?id=${songId}`);
  await page.getByRole("button", { name: "Anhören" }).click();
  const spieler = page.getByRole("region", { name: "Audio-Spieler · Sopran" });

  // Der erste Fehler löst den stillen Neustart aus, der ebenfalls scheitert.
  await expect(
    spieler.getByText(
      "Audio konnte nicht geladen werden. Bitte erneut versuchen.",
    ),
  ).toBeVisible({ timeout: 20_000 });
  expect(zugriffe).toBe(2);

  await spieler.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect.poll(() => aktuellePosition(page), { timeout: 20_000 }).toBe(0);
  expect(zugriffe).toBe(3);
  expect(erfolgreich).not.toBeNull();
  await spieler.getByRole("button", { name: "Sopran abspielen" }).click();
  await expect
    .poll(() => aktuellePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.2);
  expect(errors).toEqual([]);
});
