import { expect, type Page, test } from "@playwright/test";

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

const songId = "00000000-0000-0000-0000-00000000000a";
const arrangementId = "00000000-0000-0000-0000-00000000c00a";
const versionId = "00000000-0000-0000-0000-00000000d00a";

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

function revision(overwrite: Record<string, unknown> = {}) {
  return {
    revisionId: "00000000-0000-0000-0000-00000000f00a",
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-18T10:00:00.000Z",
    ...overwrite,
  };
}

function detail(
  songAssets: unknown[] = [],
  voiceConfiguration: string | null = "S,A,T,B + Schlagwerk",
) {
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
          voiceConfiguration,
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

function pfadTeil(url: string, index: number): string {
  return new URL(url).pathname.split("/")[index] ?? "";
}

function dateiPfad(name: string, mimeType: string, inhalt: string) {
  return { name, mimeType, buffer: Buffer.from(inhalt) };
}

test("Mehrere Dateien werden nacheinander mit Status und ohne Duplikate hochgeladen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const schritte: Record<string, string[]> = {
    Sopran: [],
    Alt: [],
    Tenor: [],
  };
  const assetNachStimme = new Map<string, string>();
  const sitzungNachStimme = new Map<string, string>();
  let altGescheitert = false;
  const erwarteterInhaltstyp: Record<string, string> = {
    Sopran: "application/pdf",
    Alt: "audio/mpeg",
    Tenor: "audio/midi",
  };
  let erstellt = 0;
  let sitzungen = 0;
  let gleichzeitig = 0;
  let maximalGleichzeitig = 0;

  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail())),
  );
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) => {
    erstellt += 1;
    const anfrage = route.request().postDataJSON() as {
      assetType?: string;
      voiceLabel?: string;
    };
    const stimme = anfrage.voiceLabel ?? "";
    schritte[stimme]?.push("asset");
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    const assetId = `asset-${stimme.toLowerCase()}`;
    assetNachStimme.set(stimme, assetId);
    return route.fulfill(
      json(
        {
          id: assetId,
          musicalVersionId: versionId,
          assetType: anfrage.assetType ?? "score",
          voiceLabel: stimme || null,
          description: null,
          createdAt: "2026-09-19T12:00:00.000Z",
          currentRevision: null,
        },
        201,
      ),
    );
  });
  await page.route(/\/api\/assets\/[^/]+\/upload-session$/, (route) => {
    const assetId = pfadTeil(route.request().url(), 3);
    const stimme =
      [...assetNachStimme.entries()].find(
        ([, kandidat]) => kandidat === assetId,
      )?.[0] ?? "";
    schritte[stimme]?.push("sitzung");
    sitzungen += 1;
    const sitzungsId = `sitz-${stimme.toLowerCase()}-${sitzungen}`;
    sitzungNachStimme.set(sitzungsId, stimme);
    return route.fulfill(
      json(
        {
          uploadSessionId: sitzungsId,
          blobName: null,
          uploadUrl: `https://speicher.test/ubertragung?sig=${stimme}`,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    );
  });
  await page.route(/speicher\.test\/ubertragung/, async (route) => {
    const stimme = new URL(route.request().url()).searchParams.get("sig") ?? "";
    schritte[stimme]?.push("uebertragung");
    const anfrage = route.request();
    expect(anfrage.method()).toBe("PUT");
    if (!anfrage.url().includes("comp=blocklist")) {
      expect(anfrage.headers()["content-type"]).toBe(
        erwarteterInhaltstyp[stimme],
      );
    }
    gleichzeitig += 1;
    maximalGleichzeitig = Math.max(maximalGleichzeitig, gleichzeitig);
    try {
      await route.fulfill(json({}, 201));
    } finally {
      gleichzeitig -= 1;
    }
  });
  await page.route("**/api/upload-sessions/*/finalize", async (route) => {
    const sitzungsId = pfadTeil(route.request().url(), 3);
    const stimme = sitzungNachStimme.get(sitzungsId) ?? "";
    schritte[stimme]?.push("finalisierung");
    expect(route.request().method()).toBe("POST");
    if (stimme === "Alt" && !altGescheitert) {
      altGescheitert = true;
      return route.fulfill(
        problem("Die Datei ist keine gültige Audiodatei.", 422),
      );
    }
    return route.fulfill(
      json({
        assetId: assetNachStimme.get(stimme),
        revisionId: `rev-${stimme.toLowerCase()}`,
        revisionNumber: 1,
        contentType: erwarteterInhaltstyp[stimme],
        sizeBytes: 419430,
        createdAt: "2026-09-19T12:00:00.000Z",
      }),
    );
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByRole("button", { name: "Material hochladen" }),
  ).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles([
    dateiPfad("noten-sopran.pdf", "application/pdf", "%PDF-1.4 sopran"),
    dateiPfad("aufnahme-alt.mp3", "audio/mpeg", "audio-alt"),
    dateiPfad("satz-tenor.mid", "", "midi-tenor"),
  ]);
  await expect(page.locator(".material-datei")).toHaveCount(3);
  await expect(page.getByText("wartet")).toHaveCount(3);
  const zeileAlt = page
    .locator(".material-datei")
    .filter({ hasText: "aufnahme-alt.mp3" });
  await expect(zeileAlt.locator(".material-datei-groesse")).toContainText("B");

  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("2 von 3 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByText("Die Datei ist keine gültige Audiodatei."),
  ).toBeVisible();
  await expect(zeileAlt.getByText("gescheitert")).toBeVisible();
  await expect(
    page
      .locator(".material-datei")
      .filter({ hasText: "noten-sopran.pdf" })
      .getByText("gespeichert."),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toHaveCount(1);

  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(page.getByText("3 von 3 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut versuchen" }),
  ).toHaveCount(0);
  await expect(erstellt).toBe(3);
  await expect(sitzungen).toBe(4);
  expect(maximalGleichzeitig).toBeLessThanOrEqual(2);
  expect(schritte.Sopran).toEqual([
    "asset",
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
  ]);
  expect(schritte.Tenor).toEqual([
    "asset",
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
  ]);
  expect(schritte.Alt).toEqual([
    "asset",
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
  ]);

  expect(errors).toEqual([]);
});

test("Stimmenlabels können vor der Übertragung gesetzt werden", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const antraege: Record<string, unknown>[] = [];
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail())),
  );
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) => {
    antraege.push(route.request().postDataJSON() as Record<string, unknown>);
    const stimulus = antraege.length;
    return route.fulfill(
      json(
        {
          id: `asset-neu-${stimulus}`,
          musicalVersionId: versionId,
          assetType: "audio",
          voiceLabel: null,
          description: null,
          createdAt: "2026-09-19T12:00:00.000Z",
          currentRevision: null,
        },
        201,
      ),
    );
  });
  await page.route(/\/api\/assets\/[^/]+\/upload-session$/, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: `sitz-${pfadTeil(route.request().url(), 3)}`,
          blobName: null,
          uploadUrl: "https://speicher.test/ubertragung?sig=probe",
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    ),
  );
  const inhaltstypen: string[] = [];
  await page.route(/speicher\.test\/ubertragung/, async (route) => {
    if (!route.request().url().includes("comp=blocklist")) {
      inhaltstypen.push(route.request().headers()["content-type"] ?? "");
    }
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(
      json({
        assetId: "asset-neu",
        revisionId: "rev-neu",
        revisionNumber: 1,
        contentType: "audio/mpeg",
        sizeBytes: 419430,
        createdAt: "2026-09-19T12:00:00.000Z",
      }),
    ),
  );

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByRole("button", { name: "Material hochladen" }),
  ).toBeVisible();
  const vorschlaege = await page
    .locator("#material-stimmen option")
    .evaluateAll((knoten) =>
      knoten.map((knoten_) => (knoten_ as HTMLOptionElement).value),
    );
  expect(vorschlaege).toEqual([
    "Vollmix",
    "Sopran",
    "Alt",
    "Tenor",
    "Bass",
    "Schlagwerk",
  ]);

  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles([
    dateiPfad("probe.mp3", "audio/mpeg", "audio-probe"),
    dateiPfad("satz.mid", "", "midi-satz"),
  ]);
  await expect(page.locator(".material-datei")).toHaveCount(2);

  const zeileProbe = page
    .locator(".material-datei")
    .filter({ hasText: "probe.mp3" });
  await zeileProbe.locator("select").selectOption("audio");
  await zeileProbe.getByLabel("Stimme (optional)").fill("Sopran");
  await zeileProbe
    .getByLabel("Beschreibung (optional)")
    .fill("Probenmitschnitt");
  const zeileSatz = page
    .locator(".material-datei")
    .filter({ hasText: "satz.mid" });
  await expect(zeileSatz.locator("select")).toHaveValue("midi");
  await zeileSatz.getByLabel("Beschreibung (optional)").fill("MIDI zum Üben");

  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("2 von 2 Dateien gespeichert.")).toBeVisible();
  expect(antraege).toHaveLength(2);
  expect(antraege).toContainEqual({
    assetType: "audio",
    voiceLabel: "Sopran",
    description: "Probenmitschnitt",
  });
  expect(antraege).toContainEqual({
    assetType: "midi",
    description: "MIDI zum Üben",
  });
  expect([...inhaltstypen].sort()).toEqual(["audio/midi", "audio/mpeg"]);

  expect(errors).toEqual([]);
});

test("Materialien sind nach Typ und Stimme gruppiert", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);

  let zugriffe = 0;
  await page.route("**/api/assets/*/access", async (route) => {
    zugriffe += 1;
    await route.fulfill(json({}));
  });
  await page.route(`**/api/songs/${songId}`, (route) => {
    return route.fulfill(
      json(
        detail([
          {
            id: "asset-sopran",
            assetType: "score",
            voiceLabel: "Sopran",
            description: "Chornoten für die Sopranstimme",
            currentRevision: revision({ revisionNumber: 1 }),
          },
          {
            id: "asset-vollmix",
            assetType: "score",
            voiceLabel: null,
            description: "Partitur für den Vollmix",
            currentRevision: revision({ revisionNumber: 2 }),
          },
          {
            id: "asset-alt",
            assetType: "audio",
            voiceLabel: "Alt",
            description: "Aufnahme der Altstimme",
            currentRevision: revision({
              revisionId: "00000000-0000-0000-0000-00000000f00b",
              revisionNumber: 3,
              contentType: "audio/mpeg",
              sizeBytes: 1258291,
            }),
          },
          {
            id: "asset-midi",
            assetType: "midi",
            voiceLabel: null,
            description: null,
            currentRevision: revision({
              revisionId: "00000000-0000-0000-0000-00000000f00c",
              revisionNumber: 1,
              contentType: "audio/midi",
              sizeBytes: 20480,
            }),
          },
          {
            id: "asset-pending",
            assetType: "audio",
            voiceLabel: "Tenor",
            description: "Noch in Arbeit",
            currentRevision: null,
          },
        ]),
      ),
    );
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(page.getByText("Fassung: Standardfassung")).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Noten", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Audio", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "MIDI", exact: true }),
  ).toBeVisible();
  await expect(page.getByText("Sopran", { exact: true })).toBeVisible();
  await expect(page.getByText("Alt", { exact: true })).toBeVisible();
  await expect(page.getByText("Vollmix", { exact: true })).toHaveCount(2);
  await expect(page.getByText("Chornoten für die Sopranstimme")).toBeVisible();
  await expect(page.getByText("Aufnahme der Altstimme")).toBeVisible();
  await expect(
    page.getByText("Noten · Fassung 1 · 0,4 MB · PDF"),
  ).toBeVisible();
  await expect(
    page.getByText("Audio · Fassung 3 · 1,2 MB · audio/mpeg"),
  ).toBeVisible();
  await expect(page.getByText("Tenor")).toHaveCount(0);
  await expect(page.getByText("Noch in Arbeit")).toHaveCount(0);
  expect(zugriffe).toBe(0);
  await expect(
    page.getByRole("button", { name: "Material hochladen" }),
  ).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Gescheiterte Änderung am Material zeigt Fehler", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(
      json(
        detail([
          {
            id: "asset-alt",
            assetType: "audio",
            voiceLabel: "Alt",
            description: "Aufnahme der Altstimme",
            currentRevision: revision({
              revisionId: "00000000-0000-0000-0000-00000000f00b",
              contentType: "audio/mpeg",
            }),
          },
        ]),
      ),
    ),
  );
  await page.route(/\/api\/assets\/asset-alt$/, (route) => {
    expect(route.request().method()).toBe("PATCH");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    return route.fulfill(
      problem("Nur die anlegendende Person kann das Material bearbeiten.", 403),
    );
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Audio · Fassung 1 · 0,4 MB · audio/mpeg"),
  ).toBeVisible();
  const eintrag = page
    .locator("article")
    .filter({ hasText: "Aufnahme der Altstimme" });
  await eintrag.getByRole("button", { name: "Bearbeiten" }).click();
  await eintrag
    .getByLabel("Beschreibung (optional)")
    .fill("Aufnahme der Altstimme, neu gemischt");
  await eintrag.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(
    page.getByText("Nur die anlegendende Person kann das Material bearbeiten."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});
