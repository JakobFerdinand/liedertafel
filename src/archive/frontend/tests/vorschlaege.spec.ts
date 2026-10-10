import { expect, type Page, test } from "@playwright/test";

// ARC-013-1: die "KI"-Abzeichen im Liedformular mit Zurückholen und die
// "Vorschläge"-Verwaltung. Beides läuft auf gespeicherten Angaben; die
// Nachbildungen hier entsprechen dem echten Antwortumfang Feld für Feld.

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

const songId = "00000000-0000-0000-0000-000000000a01";
const arrangementId = "00000000-0000-0000-0000-00000000b01";
const versionId = "00000000-0000-0000-0000-00000000c01";
const tonartVorschlagId = "00000000-0000-0000-0000-0000000000d1";
const neuesLiedVorschlagId = "00000000-0000-0000-0000-0000000000d2";

function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  };
}

function herkunft(
  entityType: string,
  entityId: string,
  field: string,
  source: "Human" | "Regex" | "Ai",
) {
  return {
    entityType,
    entityId,
    field,
    fieldLabel: field,
    source,
    confidence: "Sicher",
    model: source === "Ai" ? "gpt-5-4-mini" : null,
    promptVersion: source === "Ai" ? "arc034-1" : null,
    changedAt: "2026-10-07T09:30:00.000Z",
    locked: source === "Human",
    canRevert: true,
  };
}

/** Detailantwort der Redaktion mit gespeicherter KI-Herkunft. */
function detail(
  herkunftZeilen: unknown[],
  options?: { tonartHerkunft?: boolean },
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
      updatedAt: "2026-10-07T09:30:00.000Z",
      rowVersion: 7,
      lyrics: null,
      language: null,
      occasion: null,
      tags: [],
      alternateTitles: [],
      provenance: herkunftZeilen,
      arrangements: [
        {
          id: arrangementId,
          label: "Satz für gemischten Chor",
          arranger: null,
          voiceConfiguration: null,
          accompaniment: null,
          rowVersion: 3,
          musicalVersions: [
            {
              id: versionId,
              label: "Standardfassung",
              creator: null,
              musicalKey: "F-Dur",
              rowVersion: 2,
              assets: [],
            },
          ],
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

test("Redaktion sieht KI-Abzeichen und holt den früheren Wert zurück", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  const mitAbzeichen = detail([
    herkunft("song", songId, "composer", "Ai"),
    herkunft("song", songId, "title", "Human"),
    herkunft("musical_version", versionId, "musical_key", "Regex"),
  ]);
  const ohnAbzeichen = detail([]);
  // Nach dem Zurückholen liefert die Ansicht Ansicht keine Herkunft mehr.
  let zeigtAbzeichen = true;
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(zeigtAbzeichen ? mitAbzeichen : ohnAbzeichen)),
  );
  let reverts = 0;
  await page.route("**/api/provenance/revert", async (route) => {
    reverts += 1;
    return route.fulfill(json({ reverted: true }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await page
    .getByRole("heading", { name: "Lied bearbeiten" })
    .waitFor({ state: "visible" });
  const details = page.locator("details.lied-bearbeiten");
  // Der Bearbeitungsaufklapper ruft LiedFormular erst beim Öffnen auf.
  if (!(await details.getAttribute("open"))) {
    await details.locator("summary").first().click();
  }

  // Das KI-Abzeichen am mit KI geschriebenen Feld, gesichertes Feld ohne.
  await expect(details.getByText("KI", { exact: true })).toHaveCount(1);

  await details
    .getByRole("button", { name: "KI-Wert für Komponist zurückholen" })
    .click();
  await expect(
    details.getByText("Früheren Wert für „Komponist“ zurückgeholt."),
  ).toBeVisible();
  expect(reverts).toBe(1);

  // Nach einem erneuten Öffnen listet die Ansicht keine automatische
  // Herkunft mehr; das gesicherte Feld zeigt kein Abzeichen.
  zeigtAbzeichen = false;
  await page.reload();
  await page
    .getByRole("heading", { name: "Lied bearbeiten" })
    .waitFor({ state: "visible" });
  if (!(await details.getAttribute("open"))) {
    await details.locator("summary").first().click();
  }
  await expect(details.getByText("KI", { exact: true })).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Mitglieder sehen kein KI-Abzeichen und keine Vorschläge", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  // Die API liefert Mitgliedern keine Herkunft und keine Abzeichen.
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([]))),
  );
  await page.route("**/api/proposals", (route) =>
    route.fulfill(
      json(
        {
          title: "Keine Berechtigung für die Verwaltung.",
        },
        403,
      ),
    ),
  );

  await page.goto(`/lied/?id=${songId}`);
  await page
    .getByRole("heading", { name: "Das Wandern ist des Müllers Lust" })
    .waitFor({ state: "visible" });
  await expect(page.getByText("KI", { exact: true })).toHaveCount(0);

  await page.goto("/verwaltung/");
  await expect(
    page.getByRole("heading", { name: "Mitgliederverwaltung" }),
  ).toBeVisible();
  await expect(
    page.getByText("Keine Berechtigung für die Verwaltung."),
  ).toHaveCount(0);
  await page.goto("/verwaltung/vorschlaege/");
  await expect(
    page.getByText("Keine Berechtigung für die Verwaltung."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Redaktion arbeitet die Vorschlagsliste nach Art ab", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const vorschlaege = {
    proposals: [
      {
        id: tonartVorschlagId,
        kind: "FieldSuggestion",
        kindLabel: "Feldvorschlag",
        targetEntityType: "musical_version",
        targetEntityId: versionId,
        payload: { field: "musical_key", value: "D-Dur" },
        reason: "Aus dem Notentext gelesen.",
        confidence: "Unsicher",
        source: "Regex",
        model: null,
        promptVersion: null,
        sourceDescription: "Noten Stand 2",
        targetRowVersion: 2,
        targetCurrentRowVersion: 2,
        targetCurrentSummary: "F-Dur",
        isStale: false,
        createdAt: "2026-10-09T08:15:00.000Z",
      },
      {
        id: neuesLiedVorschlagId,
        kind: "SongCreation",
        kindLabel: "Neues Lied",
        targetEntityType: null,
        targetEntityId: null,
        payload: {
          title: "Aus dem Leselauf",
          composer: "Komponist X",
          lyricist: null,
        },
        reason: "Neues Lied aus dem Ordner 47 erkannt.",
        confidence: "Unsicher",
        source: "Ai",
        model: "gpt-5-4-mini",
        promptVersion: "arc036-1",
        sourceDescription: "Ordner 47",
        targetRowVersion: null,
        targetCurrentRowVersion: null,
        targetCurrentSummary: null,
        isStale: false,
        createdAt: "2026-10-09T09:00:00.000Z",
      },
    ],
  };
  await page.route("**/api/proposals", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    return route.fulfill(json(vorschlaege));
  });
  await page.route("**/api/proposals/*/accept", (route) =>
    route.fulfill(json({ accepted: true })),
  );

  await page.goto("/verwaltung/vorschlaege/");
  await expect(
    page.getByRole("heading", { name: "Feldvorschlag", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Neues Lied", exact: true }),
  ).toBeVisible();
  await expect(page.getByText("Gespeicherter Wert")).toHaveCount(0);

  // Annehmen meldet den Erfolg und lädt die Liste neu.
  await page
    .getByRole("button", { name: "Vorschlag für Tonart annehmen" })
    .click();
  await expect(
    page.getByText("Vorschlag angenommen und angewendet."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Ein veraltetes Ziel zeigt beiden Werte und lässt sich auffrischen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const veraltet = {
    proposals: [
      {
        id: tonartVorschlagId,
        kind: "FieldSuggestion",
        kindLabel: "Feldvorschlag",
        targetEntityType: "musical_version",
        targetEntityId: versionId,
        payload: { field: "musical_key", value: "D-Dur" },
        reason: "Aus dem Notentext gelesen.",
        confidence: "Unsicher",
        source: "Regex",
        model: null,
        promptVersion: null,
        sourceDescription: "Noten Stand 2",
        targetRowVersion: 2,
        targetCurrentRowVersion: 5,
        targetCurrentSummary: "F-Dur",
        isStale: true,
        createdAt: "2026-10-09T08:15:00.000Z",
      },
    ],
  };
  await page.route("**/api/proposals", (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    return route.fulfill(json(veraltet));
  });
  await page.route("**/api/proposals/*/refresh", (route) =>
    route.fulfill(json({ refreshed: true })),
  );

  await page.goto("/verwaltung/vorschlaege/");
  await expect(
    page.getByText("Ziel ist zwischenzeitlich geändert."),
  ).toBeVisible();
  await expect(page.getByText("F-Dur")).toBeVisible();
  await expect(
    page
      .getByRole("complementary", {
        name: "Vorschlagsziel wurde zwischenzeitlich geändert",
      })
      .getByText("D-Dur"),
  ).toBeVisible();

  await page
    .getByRole("button", { name: "Vorschlag auf den aktuellen Stand bringen" })
    .click();
  await expect(
    page.getByText(
      "Vorschlag auf den aktuellen Stand gebracht. Bitte noch einmal prüfen.",
    ),
  ).toBeVisible();

  expect(errors).toEqual([]);
});
