import { expect, type Page, test } from "@playwright/test";

// ARC-033: Die Redaktion korrigiert Noten, sieht die aufbewahrten
// Dateistände und legt einen früheren wieder als aktuell fest. Mitglieder
// sehen davon nur den aktuellen Dateistand derselben Fassung.

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
const versionId = "00000000-0000-0000-0000-00000000d00a";
const assetId = "00000000-0000-0000-0000-00000000e00a";
const standEinsId = "00000000-0000-0000-0000-00000000f001";
const standZweiId = "00000000-0000-0000-0000-00000000f002";

const uploadUrl = "https://speicher.test/ubertragung?sig=upload-1";
const uploadMuster = /speicher\.test\/ubertragung/;

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

function revision(nummer: 1 | 2) {
  return {
    revisionId: nummer === 1 ? standEinsId : standZweiId,
    revisionNumber: nummer,
    contentType: "application/pdf",
    sizeBytes: nummer === 1 ? 419430 : 524288,
    createdAt:
      nummer === 1 ? "2026-09-18T10:00:00.000Z" : "2026-09-20T15:30:00.000Z",
  };
}

function detail(aktuell: 1 | 2) {
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
          id: "00000000-0000-0000-0000-00000000c00a",
          label: "Satz für gemischten Chor",
          arranger: null,
          voiceConfiguration: null,
          musicalVersions: [
            {
              id: versionId,
              label: "Standardfassung",
              creator: "Josef Gabriel",
              musicalKey: null,
              assets: [
                {
                  id: assetId,
                  assetType: "score",
                  voiceLabel: null,
                  currentRevision: revision(aktuell),
                },
              ],
            },
          ],
        },
      ],
    },
  };
}

function stand(nummer: 1 | 2, aktuell: boolean) {
  return {
    ...revision(nummer),
    fileName: nummer === 1 ? "wandern.pdf" : "wandern-korrigiert.pdf",
    createdBy: nummer === 1 ? "Anna Erstredaktion" : "Bernd Zweitredaktion",
    isCurrent: aktuell,
  };
}

const hochgeladenEins = {
  kind: "upload",
  revisionId: standEinsId,
  revisionNumber: 1,
  previousRevisionNumber: null,
  changedAt: "2026-09-18T10:00:00.000Z",
  changedBy: "Anna Erstredaktion",
};

const hochgeladenZwei = {
  kind: "upload",
  revisionId: standZweiId,
  revisionNumber: 2,
  previousRevisionNumber: 1,
  changedAt: "2026-09-20T15:30:00.000Z",
  changedBy: "Bernd Zweitredaktion",
};

function verlaufEinStand() {
  return {
    assetId,
    assetType: "score",
    currentRevisionId: standEinsId,
    revisions: [stand(1, true)],
    changes: [hochgeladenEins],
  };
}

function verlaufZweiStaende(aktuell: 1 | 2) {
  return {
    assetId,
    assetType: "score",
    currentRevisionId: aktuell === 1 ? standEinsId : standZweiId,
    revisions: [stand(2, aktuell === 2), stand(1, aktuell === 1)],
    changes:
      aktuell === 2
        ? [hochgeladenZwei, hochgeladenEins]
        : [
            {
              kind: "restore",
              revisionId: standEinsId,
              revisionNumber: 1,
              previousRevisionNumber: 2,
              changedAt: "2026-09-21T09:00:00.000Z",
              changedBy: "Redaktion",
            },
            hochgeladenZwei,
            hochgeladenEins,
          ],
  };
}

async function mockSitzung(page: Page, me: unknown) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
  // Der Extraktionsstatus gehört zu ARC-034 und bleibt hier still.
  await page.route("**/api/revisions/extraction**", (route) =>
    route.fulfill(json({ results: [] })),
  );
}

async function oeffneDateistaende(page: Page) {
  await page.getByRole("button", { name: "Dateistände", exact: true }).click();
  await expect(
    page.getByRole("region", { name: "Dateistände · Noten" }),
  ).toBeVisible();
}

test("Redaktion ersetzt Noten; der frühere Dateistand bleibt erhalten", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  let ersetzt = false;
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(ersetzt ? 2 : 1))),
  );
  await page.route(`**/api/assets/${assetId}/revisions`, (route) =>
    route.fulfill(json(ersetzt ? verlaufZweiStaende(2) : verlaufEinStand())),
  );
  const schritte: string[] = [];
  // Die Korrektur läuft über dasselbe Material — es entsteht kein neues.
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) => {
    schritte.push("neues-material");
    return route.fulfill(problem("unerwartet", 500));
  });
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) => {
    schritte.push("sitzung");
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: 21,
      fileName: "wandern-korrigiert.pdf",
    });
    return route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-20T16:00:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    );
  });
  await page.route(uploadMuster, (route) => {
    schritte.push("uebertragung");
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    schritte.push("finalisierung");
    ersetzt = true;
    return route.fulfill(json({ assetId, ...revision(2) }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Noten · Dateistand 1 · 0,4 MB · PDF"),
  ).toBeVisible();
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  await expect(
    bereich.getByText("Es gibt noch keinen früheren Dateistand."),
  ).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await bereich
    .getByRole("button", { name: "Korrigierte Noten hochladen" })
    .click();
  await (await wahl).setFiles({
    name: "wandern-korrigiert.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 wandern, neu"),
  });

  await expect(
    bereich.getByText(/Dateistand 2 ist jetzt aktuell\./),
  ).toBeVisible();
  // Dieselbe Fassung, derselbe Materialeintrag — nur die Datei ist neu.
  await expect(page.getByText("Fassung: Standardfassung")).toBeVisible();
  await expect(page.locator("article.material-eintrag")).toHaveCount(1);
  await expect(
    page.getByText("Noten · Dateistand 2 · 0,5 MB · PDF"),
  ).toBeVisible();
  const staende = bereich.locator(".noten-verlauf-stand");
  await expect(staende).toHaveCount(2);
  await expect(staende.nth(0)).toContainText("Dateistand 2 · aktuell");
  await expect(staende.nth(0)).toHaveAttribute("aria-current", "true");
  await expect(staende.nth(0)).toContainText(
    "wandern-korrigiert.pdf · 0,5 MB · hochgeladen am 20. September 2026 um 17:30 von Bernd Zweitredaktion",
  );
  await expect(staende.nth(1)).toContainText("Dateistand 1");
  await expect(staende.nth(1)).not.toHaveAttribute("aria-current", "true");
  await expect(staende.nth(1)).toContainText("von Anna Erstredaktion");
  expect(schritte).toEqual([
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
  ]);

  expect(errors).toEqual([]);
});

test("Redaktion sieht einen früheren Dateistand an und legt ihn als aktuell fest", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  let aktuell: 1 | 2 = 2;
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(aktuell))),
  );
  await page.route(`**/api/assets/${assetId}/revisions`, (route) =>
    route.fulfill(json(verlaufZweiStaende(aktuell))),
  );
  await page.route(
    `**/api/assets/${assetId}/revisions/${standEinsId}/access`,
    (route) =>
      route.fulfill(
        json({
          assetId,
          ...revision(1),
          viewUrl: "https://speicher.test/ansicht?sig=stand-1",
          downloadUrl: "https://speicher.test/laden?sig=stand-1",
          expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
        }),
      ),
  );
  let wunsch: unknown = null;
  await page.route(`**/api/assets/${assetId}/current-revision`, (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    wunsch = route.request().postDataJSON();
    aktuell = 1;
    return route.fulfill(json(verlaufZweiStaende(1)));
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Noten · Dateistand 2 · 0,5 MB · PDF"),
  ).toBeVisible();
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });

  // Der aktuelle Stand bietet keinen Rückgriff auf sich selbst an.
  await expect(
    bereich.getByRole("button", {
      name: "Dateistand 2 als aktuell festlegen",
    }),
  ).toHaveCount(0);

  await bereich.getByRole("button", { name: "Dateistand 1 ansehen" }).click();
  await expect(
    bereich.getByRole("link", { name: "Dateistand 1 öffnen" }),
  ).toHaveAttribute("href", "https://speicher.test/ansicht?sig=stand-1");
  await expect(
    bereich.getByRole("link", { name: "Dateistand 1 öffnen" }),
  ).toHaveAttribute("rel", "noreferrer");
  await expect(
    bereich.getByRole("link", { name: "Dateistand 1 herunterladen" }),
  ).toHaveAttribute("href", "https://speicher.test/laden?sig=stand-1");

  await bereich
    .getByRole("button", { name: "Dateistand 1 als aktuell festlegen" })
    .click();
  await expect(
    bereich.getByText(/Dateistand 1 ist wieder aktuell\./),
  ).toBeVisible();
  expect(wunsch).toEqual({
    revisionId: standEinsId,
    expectedCurrentRevisionId: standZweiId,
  });
  // Beide Dateistände bleiben; nur der Zeiger hat gewechselt.
  const staende = bereich.locator(".noten-verlauf-stand");
  await expect(staende).toHaveCount(2);
  await expect(staende.nth(0)).toContainText("Dateistand 2");
  await expect(staende.nth(0)).not.toHaveAttribute("aria-current", "true");
  await expect(staende.nth(1)).toContainText("Dateistand 1 · aktuell");
  await expect(
    page.getByText("Noten · Dateistand 1 · 0,4 MB · PDF"),
  ).toBeVisible();

  await bereich.getByText("Verlauf der Änderungen").click();
  await expect(
    bereich.getByText(
      "Redaktion hat Dateistand 1 wieder als aktuell festgelegt (zuvor Dateistand 2).",
    ),
  ).toBeVisible();
  await expect(
    bereich.getByText(
      "Bernd Zweitredaktion hat Dateistand 2 hochgeladen (zuvor Dateistand 1).",
    ),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

async function mockKorrekturUpload(page: Page, schritte: string[]) {
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) => {
    schritte.push("sitzung");
    return route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-20T16:00:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    );
  });
  await page.route(uploadMuster, (route) => {
    schritte.push("uebertragung");
    return route.fulfill(json({}, 201));
  });
}

async function waehleKorrektur(page: Page) {
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  const wahl = page.waitForEvent("filechooser");
  await bereich
    .getByRole("button", { name: "Korrigierte Noten hochladen" })
    .click();
  await (await wahl).setFiles({
    name: "wandern-korrigiert.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 wandern, neu"),
  });
}

const gleichzeitigGeaendert = "Der Eintrag wurde zwischenzeitlich geändert.";

test("Verlorener Abschluss wird ohne erneute Übertragung wiederholt", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  let ersetzt = false;
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(ersetzt ? 2 : 1))),
  );
  await page.route(`**/api/assets/${assetId}/revisions`, (route) =>
    route.fulfill(json(ersetzt ? verlaufZweiStaende(2) : verlaufEinStand())),
  );
  const schritte: string[] = [];
  await mockKorrekturUpload(page, schritte);
  let abschluesse = 0;
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    schritte.push("finalisierung");
    abschluesse += 1;
    if (abschluesse === 1) {
      return route.fulfill(problem(gleichzeitigGeaendert, 409));
    }
    ersetzt = true;
    return route.fulfill(json({ assetId, ...revision(2) }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await oeffneDateistaende(page);
  await waehleKorrektur(page);

  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  await expect(
    bereich.getByText(/Dateistand 2 ist jetzt aktuell\./),
  ).toBeVisible();
  await expect(bereich.getByRole("alert")).toHaveCount(0);
  // Die Datei geht genau einmal zum Speicherdienst; nur der Abschluss
  // läuft ein zweites Mal.
  expect(schritte).toEqual([
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
    "finalisierung",
  ]);
});

test("Zweimal verlorener Abschluss meldet den Konflikt und lädt den Verlauf neu", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(2))),
  );
  let abrufe = 0;
  let fremdeKorrektur = false;
  await page.route(`**/api/assets/${assetId}/revisions`, (route) => {
    abrufe += 1;
    return route.fulfill(
      json(fremdeKorrektur ? verlaufZweiStaende(2) : verlaufEinStand()),
    );
  });
  const schritte: string[] = [];
  await mockKorrekturUpload(page, schritte);
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    schritte.push("finalisierung");
    // Jemand anderes hat inzwischen Dateistand 2 angelegt.
    fremdeKorrektur = true;
    return route.fulfill(problem(gleichzeitigGeaendert, 409));
  });

  await page.goto(`/lied/?id=${songId}`);
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  await expect(bereich.locator(".noten-verlauf-stand")).toHaveCount(1);
  const abrufeVorher = abrufe;
  await waehleKorrektur(page);

  await expect(bereich.getByRole("alert")).toHaveText(gleichzeitigGeaendert);
  expect(
    schritte.filter((schritt) => schritt === "finalisierung"),
  ).toHaveLength(2);
  // Der Verlauf zeigt jetzt die fremde Korrektur statt des alten Stands.
  await expect.poll(() => abrufe).toBeGreaterThan(abrufeVorher);
  await expect(bereich.locator(".noten-verlauf-stand")).toHaveCount(2);
  await expect(bereich.locator(".noten-verlauf-stand").nth(0)).toContainText(
    "Dateistand 2 · aktuell",
  );
});

test("Abgelaufene Tickets verschwinden; Ansehen holt ein frisches", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(2))),
  );
  await page.route(`**/api/assets/${assetId}/revisions`, (route) =>
    route.fulfill(json(verlaufZweiStaende(2))),
  );
  let tickets = 0;
  await page.route(
    `**/api/assets/${assetId}/revisions/${standEinsId}/access`,
    (route) => {
      tickets += 1;
      // Das erste Ticket ist zwei Sekunden vor der Verfallsgrenze (eine
      // Minute Vorlauf), das zweite ist frisch.
      const restMs = tickets === 1 ? 62_000 : 15 * 60_000;
      return route.fulfill(
        json({
          assetId,
          ...revision(1),
          viewUrl: `https://speicher.test/ansicht?sig=stand-1-${tickets}`,
          downloadUrl: `https://speicher.test/laden?sig=stand-1-${tickets}`,
          expiresAt: new Date(Date.now() + restMs).toISOString(),
        }),
      );
    },
  );

  await page.goto(`/lied/?id=${songId}`);
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  const ansehen = bereich.getByRole("button", { name: "Dateistand 1 ansehen" });
  const oeffnen = bereich.getByRole("link", { name: "Dateistand 1 öffnen" });

  await ansehen.click();
  await expect(oeffnen).toHaveAttribute(
    "href",
    "https://speicher.test/ansicht?sig=stand-1-1",
  );
  // Vor dem Ablauf verschwinden die Links wieder.
  await expect(oeffnen).toHaveCount(0, { timeout: 10_000 });
  await expect(ansehen).toBeVisible();

  await ansehen.click();
  await expect(oeffnen).toHaveAttribute(
    "href",
    "https://speicher.test/ansicht?sig=stand-1-2",
  );
  expect(tickets).toBe(2);
});

test("Veralteter Verlauf wird beim Festlegen abgewiesen und neu geladen", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(2))),
  );
  let abrufe = 0;
  await page.route(`**/api/assets/${assetId}/revisions`, (route) => {
    abrufe += 1;
    return route.fulfill(json(verlaufZweiStaende(2)));
  });
  await page.route(`**/api/assets/${assetId}/current-revision`, (route) =>
    route.fulfill(problem("Der Eintrag wurde zwischenzeitlich geändert.", 409)),
  );

  await page.goto(`/lied/?id=${songId}`);
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  await expect(bereich.locator(".noten-verlauf-stand")).toHaveCount(2);
  const abrufeVorher = abrufe;
  await bereich
    .getByRole("button", { name: "Dateistand 1 als aktuell festlegen" })
    .click();

  await expect(bereich.getByRole("alert")).toContainText(
    "Die Noten wurden zwischenzeitlich geändert.",
  );
  await expect.poll(() => abrufe).toBeGreaterThan(abrufeVorher);
  await expect(bereich.locator(".noten-verlauf-stand").nth(0)).toContainText(
    "Dateistand 2 · aktuell",
  );
});

test("Nicht ladbare Dateistände zeigen eine Meldung und lassen sich erneut laden", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(1))),
  );
  let gestoert = true;
  await page.route(`**/api/assets/${assetId}/revisions`, (route) =>
    gestoert
      ? route.fulfill(problem("Speicherdienst nicht erreichbar.", 502))
      : route.fulfill(json(verlaufEinStand())),
  );

  await page.goto(`/lied/?id=${songId}`);
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  await expect(bereich.getByRole("alert")).toHaveText(
    "Die Dateistände konnten nicht geladen werden.",
  );
  await expect(bereich.locator(".noten-verlauf-stand")).toHaveCount(0);

  gestoert = false;
  await bereich.getByRole("button", { name: "Erneut laden" }).click();
  await expect(bereich.locator(".noten-verlauf-stand")).toHaveCount(1);
  await expect(bereich.getByRole("alert")).toHaveCount(0);
  await expect(bereich.locator(".noten-verlauf-stand")).toContainText(
    "wandern.pdf",
  );
});

test("Fehlende Angaben eines Dateistands werden ausdrücklich benannt", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(1))),
  );
  await page.route(`**/api/assets/${assetId}/revisions`, (route) =>
    route.fulfill(
      json({
        ...verlaufEinStand(),
        revisions: [{ ...stand(1, true), fileName: null, createdBy: null }],
        changes: [],
      }),
    ),
  );

  await page.goto(`/lied/?id=${songId}`);
  await oeffneDateistaende(page);
  const bereich = page.getByRole("region", { name: "Dateistände · Noten" });
  await expect(bereich.locator(".noten-verlauf-stand")).toContainText(
    "Dateiname nicht erfasst · 0,4 MB · hochgeladen am 18. September 2026 um 12:00 von unbekannt",
  );
  await expect(bereich.getByText("Verlauf der Änderungen")).toHaveCount(0);
});

test("Mitglied sieht den aktuellen Dateistand, aber keinen Verlauf", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(2))),
  );
  let verlaufAbrufe = 0;
  await page.route(`**/api/assets/${assetId}/revisions**`, (route) => {
    verlaufAbrufe += 1;
    return route.fulfill(
      problem("Keine Berechtigung für das Liedverzeichnis.", 403),
    );
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Noten · Dateistand 2 · 0,5 MB · PDF"),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Noten anzeigen" }),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: /Dateistände/ })).toHaveCount(
    0,
  );
  expect(verlaufAbrufe).toBe(0);
});
