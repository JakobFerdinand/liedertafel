import { expect, type Page, test } from "@playwright/test";

// ARC-034: Auswertungsstand der PDF-Textauswertung in den Material-Einträgen
// (Noten und Auftrittsdokumente): ehrliche Zustände, Textvorschau für
// gelesenen Text, erneuter Versuch für gescheiterte Läufe, Nachfragen nur
// solange die Auswertung wirklich unterwegs ist, und nichts für Mitglieder.

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

// Noten-Oberfläche (Lied mit Noten-PDF).
const songId = "00000000-0000-0000-0000-00000000000a";
const arrangementId = "00000000-0000-0000-0000-00000000c00a";
const versionId = "00000000-0000-0000-0000-00000000d00a";
const notenAssetId = "00000000-0000-0000-0000-00000000e00a";
const notenRevisionId = "00000000-0000-0000-0000-00000000f00a";

// Auftritts-Oberfläche (Auftritt mit Dokument-PDF).
const eventId = "00000000-0000-0000-0000-00000000e010";
const dokumentAssetId = "00000000-0000-0000-0000-00000000e012";
const dokumentRevisionId = "00000000-0000-0000-0000-00000000f00c";

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
}

function pfadTeil(url: string, index: number): string {
  return new URL(url).pathname.split("/")[index] ?? "";
}

function revision(revisionId: string) {
  return {
    revisionId,
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-18T10:00:00.000Z",
  };
}

function notenEintrag() {
  return {
    id: notenAssetId,
    assetType: "score",
    voiceLabel: null,
    description: null,
    currentRevision: revision(notenRevisionId),
  };
}

function dokumentEintrag() {
  return {
    id: dokumentAssetId,
    assetType: "document",
    description: "Programmheft des Frühlingskonzerts",
    createdAt: "2026-09-20T10:00:00.000Z",
    currentRevision: revision(dokumentRevisionId),
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

function liedDetail(assets: unknown[]) {
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
          voiceConfiguration: null,
          musicalVersions: [fassung(assets)],
        },
      ],
    },
  };
}

function auftrittDetail(dokumente: unknown[]) {
  return {
    event: {
      id: eventId,
      kind: "concert",
      title: "Frühlingskonzert",
      venue: "Stadtsaal Mining",
      dateYear: 1950,
      dateMonth: 5,
      dateDay: 12,
      dateApproximate: false,
      datePrecision: "day",
      dateDisplay: "12. Mai 1950",
      startTime: "19:30",
      published: true,
      notes: null,
      sourceNote: "Programmheft im Vereinsarchiv, Blatt 3.",
      documents: dokumente,
      createdAt: "2026-09-01T08:00:00.000Z",
      updatedAt: "2026-09-24T10:00:00.000Z",
      publishedAt: "2026-09-20T10:00:00.000Z",
    },
  };
}

function notenZugriff() {
  return {
    assetId: notenAssetId,
    revisionId: notenRevisionId,
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-18T10:00:00.000Z",
    viewUrl: "https://speicher.test/ansicht?sig=ansicht-1",
    downloadUrl: "https://speicher.test/laden?sig=laden-1",
    expiresAt: "2026-09-19T13:00:00.000Z",
  };
}

function dokumentZugriff(assetId: string) {
  return {
    assetId,
    revisionId: dokumentRevisionId,
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-24T10:00:00.000Z",
    viewUrl: `https://speicher.test/ansicht?sig=${assetId}`,
    downloadUrl: `https://speicher.test/laden?sig=${assetId}`,
    expiresAt: "2026-09-24T10:15:00.000Z",
  };
}

/** Auswertungszeile des Stapelabrufs (Vertrag StatusPayload). */
function auswertung(
  revisionId: string,
  assetId: string,
  overwrite: Record<string, unknown> = {},
) {
  return {
    revisionId,
    assetId,
    revisionNumber: 1,
    status: "completed",
    text: "Erste Strophe: Das Wandern ist des Müllers Lust.",
    failureReason: null,
    attemptCount: 1,
    completedAt: "2026-09-24T11:00:00.000Z",
    lastAttemptAt: "2026-09-24T10:59:00.000Z",
    lastEnqueuedAt: "2026-09-24T10:58:00.000Z",
    updatedAt: "2026-09-24T11:00:00.000Z",
    rowVersion: 3,
    ...overwrite,
  };
}

test("Redaktion liest den Auswertungsstand und öffnet die Textvorschau", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(liedDetail([notenEintrag()]))),
  );
  await page.route(`**/api/assets/${notenAssetId}/access`, (route) =>
    route.fulfill(json(notenZugriff())),
  );
  const text = `${"A".repeat(600)}BCD`;
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(
      json({ results: [auswertung(notenRevisionId, notenAssetId, { text })] }),
    ),
  );

  await page.goto(`/lied/?id=${songId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Text ausgelesen",
  );
  await expect(
    page.getByText("Noten · Fassung 1 · 0,4 MB · PDF"),
  ).toBeVisible();

  const umschalter = zeile.getByRole("button", { name: "Textvorschau" });
  await expect(umschalter).toHaveAttribute("aria-expanded", "false");
  await expect(zeile.locator(".extraktion-vorschau")).toHaveCount(0);
  await umschalter.click();
  const vorschau = zeile.locator(".extraktion-vorschau");
  await expect(vorschau).toHaveText(`${"A".repeat(600)}…`);
  const schliessen = zeile.getByRole("button", {
    name: "Vorschau schließen",
  });
  await expect(schliessen).toHaveAttribute("aria-expanded", "true");
  await schliessen.click();
  await expect(vorschau).toHaveCount(0);
  await expect(
    zeile.getByRole("button", { name: "Textvorschau" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Mitglied sieht keine Auswertungszeile", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(liedDetail([notenEintrag()]))),
  );
  let anfragen = 0;
  await page.route("**/api/revisions/extraction*", (route) => {
    anfragen += 1;
    return route.fulfill(
      json({ results: [auswertung(notenRevisionId, notenAssetId)] }),
    );
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Noten · Fassung 1 · 0,4 MB · PDF"),
  ).toBeVisible();
  await expect(page.locator(".extraktion-status")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Textvorschau" })).toHaveCount(
    0,
  );
  expect(anfragen).toBe(0);

  expect(errors).toEqual([]);
});

test("Wartende Auswertung frischt sich selbst auf und ruht, wenn sie fertig ist", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(
      json(
        liedDetail([
          notenEintrag(),
          {
            ...notenEintrag(),
            id: dokumentAssetId,
            voiceLabel: "Sopran",
            currentRevision: revision(dokumentRevisionId),
          },
        ]),
      ),
    ),
  );
  let stand = auswertung(notenRevisionId, notenAssetId, {
    status: "queued",
    text: null,
    completedAt: null,
    lastAttemptAt: null,
    attemptCount: 0,
  });
  let anfragen = 0;
  await page.route("**/api/revisions/extraction*", (route) => {
    anfragen += 1;
    expect(
      new URL(route.request().url()).searchParams.get("ids")?.split(",").sort(),
    ).toEqual([notenRevisionId, dokumentRevisionId].sort());
    return route.fulfill(
      json({
        results: [
          stand,
          {
            ...stand,
            revisionId: dokumentRevisionId,
            assetId: dokumentAssetId,
          },
        ],
      }),
    );
  });

  await page.goto(`/lied/?id=${songId}`);
  const standZeile = page.locator(".extraktion-stand");
  await expect(standZeile).toHaveText([
    "Textauswertung wartet …",
    "Textauswertung wartet …",
  ]);
  expect(anfragen).toBe(1);

  stand = auswertung(notenRevisionId, notenAssetId, {
    status: "running",
    text: null,
    completedAt: null,
  });
  await expect(standZeile).toHaveText(
    ["Textauswertung läuft …", "Textauswertung läuft …"],
    {
      timeout: 10_000,
    },
  );
  expect(anfragen).toBe(2);

  stand = auswertung(notenRevisionId, notenAssetId);
  await expect(standZeile).toHaveText(["Text ausgelesen", "Text ausgelesen"], {
    timeout: 10_000,
  });

  // Fertig ist fertig: ohne laufende Auswertung entsteht keine Nachfrage.
  const bisher = anfragen;
  await page.waitForTimeout(5200);
  expect(anfragen).toBe(bisher);

  expect(errors).toEqual([]);
});

test("Gescheiterte Auswertung zeigt den Grund und läuft nach dem erneuten Versuch weiter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(auftrittDetail([dokumentEintrag()]))),
  );
  await page.route("**/api/assets/*/access", (route) =>
    route.fulfill(json(dokumentZugriff(pfadTeil(route.request().url(), 3)))),
  );
  let stand = auswertung(dokumentRevisionId, dokumentAssetId, {
    status: "failed",
    text: null,
    failureReason:
      "Das Dokument ist verschlüsselt und kann nicht gelesen werden.",
    completedAt: null,
    attemptCount: 2,
  });
  let anfragen = 0;
  await page.route("**/api/revisions/extraction*", (route) => {
    anfragen += 1;
    return route.fulfill(json({ results: [stand] }));
  });
  const wiederholAntraege: unknown[] = [];
  await page.route("**/api/revisions/*/extraction/retry", (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    expect(route.request().postDataJSON()).toEqual({});
    wiederholAntraege.push(route.request().postDataJSON());
    stand = auswertung(dokumentRevisionId, dokumentAssetId, {
      status: "queued",
      text: null,
      failureReason: null,
      attemptCount: 0,
      completedAt: null,
      lastAttemptAt: null,
      rowVersion: 4,
    });
    return route.fulfill(json(stand));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toContainText(
    "Textauswertung gescheitert",
  );
  await expect(zeile.locator(".extraktion-grund")).toHaveText(
    " — Das Dokument ist verschlüsselt und kann nicht gelesen werden.",
  );

  await zeile
    .getByRole("button", {
      name: "Textauswertung für diese Datei erneut starten",
    })
    .click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung wartet …",
  );
  expect(wiederholAntraege).toHaveLength(1);

  // Nach dem Anstoßen fragt die Zeile weiter nach, bis sie fertig ist.
  stand = auswertung(dokumentRevisionId, dokumentAssetId);
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Text ausgelesen",
    { timeout: 10_000 },
  );
  expect(anfragen).toBeGreaterThanOrEqual(2);

  expect(errors).toEqual([]);
});

test("Laufende Auswertung erklärt den abgelehnten erneuten Versuch ehrlich", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(auftrittDetail([dokumentEintrag()]))),
  );
  await page.route("**/api/assets/*/access", (route) =>
    route.fulfill(json(dokumentZugriff(pfadTeil(route.request().url(), 3)))),
  );
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(
      json({
        results: [
          auswertung(dokumentRevisionId, dokumentAssetId, {
            status: "failed",
            text: null,
            failureReason: "Der Auswertungsdienst hat den Lauf abgebrochen.",
            completedAt: null,
          }),
        ],
      }),
    ),
  );
  const wiederholAntraege: unknown[] = [];
  await page.route("**/api/revisions/*/extraction/retry", (route) => {
    wiederholAntraege.push(route.request().postDataJSON());
    return route.fulfill(problem("Die Auswertung läuft bereits.", 409));
  });

  await page.goto(`/auftritt/?id=${eventId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toContainText(
    "Textauswertung gescheitert",
  );
  await zeile
    .getByRole("button", {
      name: "Textauswertung für diese Datei erneut starten",
    })
    .click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung läuft …",
  );
  expect(wiederholAntraege).toHaveLength(1);

  expect(errors).toEqual([]);
});

test("Eingescanntes Dokument bleibt ehrlich ohne Text und ohne Knopf", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/events/${eventId}`, (route) =>
    route.fulfill(json(auftrittDetail([dokumentEintrag()]))),
  );
  await page.route("**/api/assets/*/access", (route) =>
    route.fulfill(json(dokumentZugriff(pfadTeil(route.request().url(), 3)))),
  );
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(
      json({
        results: [
          auswertung(dokumentRevisionId, dokumentAssetId, {
            status: "noText",
            text: null,
          }),
        ],
      }),
    ),
  );

  await page.goto(`/auftritt/?id=${eventId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Kein Text gefunden — vermutlich eingescannt.",
  );
  await expect(zeile.getByRole("button")).toHaveCount(0);
  await expect(zeile.locator(".extraktion-vorschau")).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Gestörter Auswertungsabruf erklärt sich und stört die übrigen Aktionen nicht", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(liedDetail([notenEintrag()]))),
  );
  await page.route(`**/api/assets/${notenAssetId}/access`, (route) =>
    route.fulfill(json(notenZugriff())),
  );
  await page.route("**/api/revisions/extraction*", (route) => route.abort());

  await page.goto(`/lied/?id=${songId}`);
  const zeile = page.locator(".extraktion-status");
  await expect(zeile.locator(".feld-fehler")).toContainText(
    "Auswertungsstatus konnte nicht geladen werden.",
  );
  await expect(
    zeile.getByRole("button", { name: "Erneut versuchen" }),
  ).toBeVisible();

  // Die übrigen Aktionen des Eintrags bleiben benutzbar.
  await page.getByRole("button", { name: "Noten anzeigen" }).click();
  await expect(page.locator("iframe[title^='Noten (PDF)']")).toBeVisible();

  // Der spätere Erfolg tritt an die Stelle der Fehlerzeile.
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(
      json({ results: [auswertung(notenRevisionId, notenAssetId)] }),
    ),
  );
  await zeile.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Text ausgelesen",
  );

  expect(errors).toEqual([]);
});

for (const status of [409, 500]) {
  test(`Abgelehnter Versuch (${status}) behält den ehrlichen Stand und lädt neu`, async ({
    page,
  }) => {
    await mockSitzung(page, editorMe);
    await page.route(`**/api/songs/${songId}`, (route) =>
      route.fulfill(json(liedDetail([notenEintrag()]))),
    );
    let anfragen = 0;
    await page.route("**/api/revisions/extraction*", (route) => {
      anfragen += 1;
      return route.fulfill(
        json({
          results: [
            auswertung(notenRevisionId, notenAssetId, {
              status: "failed",
              text: null,
            }),
          ],
        }),
      );
    });
    const titel = "Der Eintrag wurde zwischenzeitlich geändert.";
    await page.route("**/api/revisions/*/extraction/retry", (route) =>
      route.fulfill(problem(titel, status)),
    );
    await page.goto(`/lied/?id=${songId}`);
    const zeile = page.locator(".extraktion-status");
    await expect(zeile.locator(".extraktion-stand")).toHaveText(
      "Textauswertung gescheitert",
    );
    await zeile
      .getByRole("button", {
        name: "Textauswertung für diese Datei erneut starten",
      })
      .click();
    await expect(zeile.getByRole("alert")).toHaveText(titel);
    await expect.poll(() => anfragen).toBe(2);
    await expect(zeile.locator(".extraktion-stand")).toHaveText(
      "Textauswertung gescheitert",
    );
    await expect(zeile).not.toContainText("Textauswertung läuft");
    await expect(
      zeile.getByRole("button", {
        name: "Textauswertung für diese Datei erneut starten",
      }),
    ).toBeEnabled();
  });
}

for (const fehlt of [false, true]) {
  for (const status of [401, 403]) {
    test(`Berechtigungsgrenze (${status}) verbirgt die Zeile beim ${fehlt ? "Start" : "erneuten Versuch"}`, async ({
      page,
    }) => {
      await mockSitzung(page, editorMe);
      await page.route(`**/api/songs/${songId}`, (route) =>
        route.fulfill(json(liedDetail([notenEintrag()]))),
      );
      await page.route("**/api/revisions/extraction*", (route) =>
        route.fulfill(
          json({
            results: fehlt
              ? []
              : [
                  auswertung(notenRevisionId, notenAssetId, {
                    status: "failed",
                  }),
                ],
          }),
        ),
      );
      await page.route("**/api/revisions/*/extraction/retry", (route) =>
        route.fulfill(
          problem("Keine Berechtigung für das Liedverzeichnis.", status),
        ),
      );
      await page.goto(`/lied/?id=${songId}`);
      const zeile = page.locator(".extraktion-status");
      await zeile
        .getByRole("button", {
          name: fehlt
            ? "Textauswertung starten"
            : "Textauswertung für diese Datei erneut starten",
        })
        .click();
      await expect(zeile).toHaveCount(0);
    });
  }
}

test("Fehlende Auswertungszeile bietet den Start an und wartet erst nach Erfolg", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(liedDetail([notenEintrag()]))),
  );
  let gestartet = false;
  const stand = auswertung(notenRevisionId, notenAssetId, {
    status: "queued",
    text: null,
  });
  await page.route("**/api/revisions/extraction*", (route) =>
    route.fulfill(json({ results: gestartet ? [stand] : [] })),
  );
  let loslassen: () => void = () => {};
  const warte = new Promise<void>((resolve) => {
    loslassen = resolve;
  });
  let posts = 0;
  await page.route(
    `**/api/revisions/${notenRevisionId}/extraction/retry`,
    async (route) => {
      expect(route.request().method()).toBe("POST");
      expect(route.request().postDataJSON()).toEqual({});
      posts += 1;
      await warte;
      gestartet = true;
      await route.fulfill(json(stand));
    },
  );
  await page.goto(`/lied/?id=${songId}`);
  const zeile = page.locator(".extraktion-status");
  const starten = zeile.getByRole("button", { name: "Textauswertung starten" });
  await expect(starten).toHaveClass("knopf-leise");
  await starten.click();
  await expect.poll(() => posts).toBe(1);
  await expect(zeile.locator(".extraktion-stand")).toHaveCount(0);
  loslassen();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Textauswertung wartet …",
  );
});

test("Späte Retry-Antwort der alten Revision verändert die neue Revision nicht", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  let neu = false;
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(
      json(
        liedDetail([
          {
            ...notenEintrag(),
            currentRevision: revision(
              neu ? dokumentRevisionId : notenRevisionId,
            ),
          },
        ]),
      ),
    ),
  );
  await page.route(`**/api/assets/${notenAssetId}`, (route) => {
    expect(route.request().method()).toBe("PATCH");
    neu = true;
    return route.fulfill(json({}));
  });
  await page.route("**/api/revisions/extraction*", (route) => {
    const id = new URL(route.request().url()).searchParams.get("ids");
    return route.fulfill(
      json({
        results: [
          auswertung(id ?? "", notenAssetId, {
            status: id === notenRevisionId ? "failed" : "completed",
            text: id === notenRevisionId ? null : "Text der neuen Revision B",
          }),
        ],
      }),
    );
  });
  let begonnen = false;
  let beendet = false;
  await page.route(
    `**/api/revisions/${notenRevisionId}/extraction/retry`,
    async (route) => {
      begonnen = true;
      await new Promise((resolve) => setTimeout(resolve, 1000));
      await route.fulfill(
        json(
          auswertung(notenRevisionId, notenAssetId, {
            status: "queued",
            text: "Alter Text der Revision A",
          }),
        ),
      );
      beendet = true;
    },
  );
  await page.goto(`/lied/?id=${songId}`);
  const zeile = page.locator(".extraktion-status");
  await zeile
    .getByRole("button", {
      name: "Textauswertung für diese Datei erneut starten",
    })
    .click();
  await expect.poll(() => begonnen).toBe(true);
  const eintrag = page.locator(".material-eintrag");
  await eintrag
    .getByRole("button", { name: "Bearbeiten", exact: true })
    .click();
  await eintrag.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Text ausgelesen",
  );
  // B ist schon sichtbar, während A noch auf die verspätete Antwort wartet.
  expect(beendet).toBe(false);
  await expect.poll(() => beendet).toBe(true);
  await expect(zeile.locator(".extraktion-stand")).toHaveText(
    "Text ausgelesen",
  );
  const vorschau = zeile.getByRole("button", { name: "Textvorschau" });
  await expect(vorschau).toHaveAttribute("aria-expanded", "false");
  await vorschau.click();
  await expect(zeile.locator(".extraktion-vorschau")).toHaveText(
    "Text der neuen Revision B",
  );
  await expect(zeile.getByRole("alert")).toHaveCount(0);
});
