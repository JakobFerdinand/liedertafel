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

const wandernId = "00000000-0000-0000-0000-0000000002a1";
const abendstilleId = "00000000-0000-0000-0000-0000000002a2";
const fassungId = "00000000-0000-0000-0000-00000000c20a";
const versionId = "00000000-0000-0000-0000-00000000d20a";
const zweiteFassungId = "00000000-0000-0000-0000-00000000c20b";

type SuchLied = {
  id: string;
  title: string;
  composer: string | null;
  lyricist: string | null;
  published: boolean;
  publishedAt: string | null;
  alternateTitles: string[];
  arrangements: Array<{ id: string; label: string; arranger: string | null }>;
  matchedIn: string[];
  matchedArrangements: Array<{
    id: string;
    label: string;
    arranger: string | null;
  }>;
  lyricsSnippet: null;
  lyrics?: string | null;
  language?: string | null;
  occasion?: string | null;
  tags?: string[];
};

// Die gemischte Fassung erfüllt die Fassungsfilter (ARC-023); die Treffer-
// zeile „Passende Fassung" liest die passende Fassung aus matchedArrangements.
const gemischteFassung = {
  id: fassungId,
  label: "Satz für gemischten Chor",
  arranger: "Josef Gabriel",
};

const wanderlied: SuchLied = {
  id: wandernId,
  title: "Das Wandern ist des Müllers Lust",
  composer: "Carl Friedrich Zöllner",
  lyricist: "Wilhelm Müller",
  published: true,
  publishedAt: "2026-09-01T10:00:00.000Z",
  alternateTitles: ["Müllertanz"],
  arrangements: [gemischteFassung],
  matchedIn: ["title"],
  matchedArrangements: [gemischteFassung],
  lyricsSnippet: null,
};

const abendstilleLied: SuchLied = {
  id: abendstilleId,
  title: "Abendstille",
  composer: null,
  lyricist: null,
  published: true,
  publishedAt: "2026-09-02T10:00:00.000Z",
  alternateTitles: [],
  arrangements: [],
  matchedIn: ["title"],
  matchedArrangements: [],
  lyricsSnippet: null,
};

const maennerlied: SuchLied = {
  id: "00000000-0000-0000-0000-0000000002a3",
  title: "Wacht auf, es winnetleith",
  composer: null,
  lyricist: null,
  published: true,
  publishedAt: "2026-09-03T10:00:00.000Z",
  alternateTitles: [],
  arrangements: [
    {
      id: zweiteFassungId,
      label: "Satz für Männerchor",
      arranger: "Hans Schmid",
    },
  ],
  matchedIn: ["title"],
  matchedArrangements: [
    {
      id: zweiteFassungId,
      label: "Satz für Männerchor",
      arranger: "Hans Schmid",
    },
  ],
  lyricsSnippet: null,
};

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
}

function suchErgebnis(
  query: string | null,
  page: number,
  total: number,
  songs: unknown[],
) {
  return { query, page, pageSize: 20, total, songs };
}

// Die Suchroute umfasst die Abfragezeichenkette; ein regulärer Ausdruck
// greift deshalb zuverlässiger als ein Glob.
const suchRoute = /\/api\/songs(\?.*)?$/;
const liedRoute = /\/api\/songs\/[0-9a-f-]+$/;
const arrangementBearbeiten = /\/api\/arrangements\/[0-9a-f-]+$/;

// Öffnet den Filteraufklapper (er ist zu, solange kein Filterparameter in
// der Adresse steht) und wählt ein Material.
async function filterAufklappen(page: Page) {
  await page.locator("details.lieder-filter > summary").click();
  await expect(page.locator("details.lieder-filter")).toHaveAttribute(
    "open",
    "",
  );
}

test("Filter schicken die erwarteten Abfrageparameter an den Katalog", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    anfragen.push(route.request().url());
    return route.fulfill(json(suchErgebnis(null, 1, 1, [wanderlied])));
  });

  await page.goto("/lieder/");
  // Ohne Filterparameter bleibt der Aufklapper zu.
  await expect(page.locator("details.lieder-filter")).not.toHaveAttribute(
    "open",
    "",
  );
  await filterAufklappen(page);
  await page.getByLabel("Stimmverteilung").fill("SATB");
  await page.getByLabel("Begleitung").fill("Klavier");
  await page.getByLabel("Tonart").fill("G-Dur");
  await page.getByLabel("Sprache").fill("Deutsch");
  await page.getByLabel("Anlass").fill("Sommerfest");
  await page.getByLabel("Schlagwort").fill("Wanderlied");
  await page.getByRole("button", { name: "Filtern", exact: true }).click();

  await expect(page).toHaveURL(
    /\/lieder\/\?stimmbesetzung=SATB&begleitung=Klavier&tonart=G-Dur&sprache=Deutsch&anlass=Sommerfest&tag=Wanderlied&seite=1$/,
  );
  // Der erste Abruf gehört zum Aufruf selbst; der zweite kommt vom Filtern.
  await expect
    .poll(() => anfragen.length, { message: "Katalogsuche abgeschickt" })
    .toBe(2);
  const anfrage = new URL(anfragen[1]);
  expect(anfrage.searchParams.get("voiceConfiguration")).toBe("SATB");
  expect(anfrage.searchParams.get("accompaniment")).toBe("Klavier");
  expect(anfrage.searchParams.get("musicalKey")).toBe("G-Dur");
  expect(anfrage.searchParams.get("language")).toBe("Deutsch");
  expect(anfrage.searchParams.get("occasion")).toBe("Sommerfest");
  expect(anfrage.searchParams.get("tag")).toBe("Wanderlied");
  expect(anfrage.searchParams.has("q")).toBe(false);
  expect(anfrage.searchParams.has("page")).toBe(false);

  expect(errors).toEqual([]);
});

test("Materialwahl übersetzt die Werte in die Materialarten", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    anfragen.push(route.request().url());
    return route.fulfill(json(suchErgebnis(null, 1, 1, [wanderlied])));
  });

  await page.goto("/lieder/");
  await filterAufklappen(page);
  await page.getByLabel("Noten").check();
  await page.getByRole("button", { name: "Filtern", exact: true }).click();

  await expect(page).toHaveURL(/\/lieder\/\?material=noten&seite=1$/);
  // Der erste Abruf gehört zum Aufruf selbst; der zweite kommt vom Filtern.
  await expect
    .poll(() => anfragen.length, { message: "Katalogsuche abgeschickt" })
    .toBe(2);
  expect(new URL(anfragen[1]).searchParams.get("material")).toBe("score");

  // Ein zweiter Materialtyp kommt als kombinierte Auswahl mit.
  await page.getByLabel("Audio").check();
  await page.getByRole("button", { name: "Filtern", exact: true }).click();

  await expect(page).toHaveURL(/\/lieder\/\?material=noten%2Caudio&seite=1$/);
  await expect
    .poll(() => anfragen.length, { message: "Zweite Filteranfrage" })
    .toBe(3);
  expect(new URL(anfragen[2]).searchParams.get("material")).toBe("score,audio");

  expect(errors).toEqual([]);
});

test("Filtern startet vorne und Blättern behält die Filter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const studien = Array.from({ length: 45 }, (_, index) => ({
    id: `00000000-0000-0000-0000-${String(300001 + index).padStart(12, "0")}`,
    title: `Studie Nummer ${index + 1}`,
    composer: null,
    lyricist: null,
    published: true,
    publishedAt: "2026-09-03T10:00:00.000Z",
    alternateTitles: [],
    arrangements: [],
    matchedIn: ["title"],
    matchedArrangements: [],
    lyricsSnippet: null,
  }));
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    const url = route.request().url();
    anfragen.push(url);
    const seite =
      Number.parseInt(new URL(url).searchParams.get("page") ?? "1", 10) || 1;
    const start = (seite - 1) * 20;
    return route.fulfill(
      json(
        suchErgebnis(
          "Studie",
          seite,
          studien.length,
          studien.slice(start, start + 20),
        ),
      ),
    );
  });

  await page.goto("/lieder/?suche=Studie&stimmbesetzung=SATB&seite=2");
  await expect(page.getByText("Studie Nummer 21")).toBeVisible();
  const ersteAnfrage = new URL(anfragen[0]);
  expect(ersteAnfrage.searchParams.get("q")).toBe("Studie");
  expect(ersteAnfrage.searchParams.get("voiceConfiguration")).toBe("SATB");
  expect(ersteAnfrage.searchParams.get("page")).toBe("2");

  // Filtern ändert die Fassungsbedingung und setzt auf Seite 1 zurück.
  const weiter = page.getByRole("button", { name: "Weiter" });
  await page.getByLabel("Stimmverteilung").fill("SSAA");
  await page.getByRole("button", { name: "Filtern", exact: true }).click();
  await expect(page).toHaveURL(
    /\/lieder\/\?suche=Studie&stimmbesetzung=SSAA&seite=1$/,
  );
  await expect
    .poll(() => anfragen.length, { message: "Gefilterte Suche abgeschickt" })
    .toBe(2);
  const filterAnfrage = new URL(anfragen[1]);
  expect(filterAnfrage.searchParams.get("voiceConfiguration")).toBe("SSAA");
  expect(filterAnfrage.searchParams.has("page")).toBe(false);

  // Blättern führt Suche, Filter und Seite weiter.
  await weiter.click();
  await expect(page).toHaveURL(
    /\/lieder\/\?suche=Studie&stimmbesetzung=SSAA&seite=2$/,
  );
  await expect
    .poll(() => anfragen.length, { message: "Zweite Seite geladen" })
    .toBe(3);
  const blattAnfrage = new URL(anfragen[2]);
  expect(blattAnfrage.searchParams.get("q")).toBe("Studie");
  expect(blattAnfrage.searchParams.get("voiceConfiguration")).toBe("SSAA");
  expect(blattAnfrage.searchParams.get("page")).toBe("2");

  expect(errors).toEqual([]);
});

test("Aufgerufene Adresse stellt Filter und Suche wieder her", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    anfragen.push(route.request().url());
    return route.fulfill(json(suchErgebnis("Wandern", 1, 1, [wanderlied])));
  });

  await page.goto("/lieder/?suche=Wandern&stimmbesetzung=SATB&material=noten");
  // Der Aufklapper steht offen und die Felder tragen die Adresswerte.
  await expect(page.locator("details.lieder-filter")).toHaveAttribute(
    "open",
    "",
  );
  await expect(page.getByLabel("Stimmverteilung")).toHaveValue("SATB");
  await expect(page.getByLabel("Noten")).toBeChecked();
  await expect(page.getByLabel("Audio")).not.toBeChecked();
  await expect(page.getByLabel("Lieder suchen")).toHaveValue("Wandern");
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();
  await expect
    .poll(() => anfragen.length, { message: "Katalogsuche abgeschickt" })
    .toBe(1);
  const anfrage = new URL(anfragen[0]);
  expect(anfrage.searchParams.get("q")).toBe("Wandern");
  expect(anfrage.searchParams.get("voiceConfiguration")).toBe("SATB");
  expect(anfrage.searchParams.get("material")).toBe("score");

  // Neuladen hält den Filterzustand fest.
  await page.reload();
  await expect(page.getByLabel("Stimmverteilung")).toHaveValue("SATB");
  await expect(page.getByLabel("Noten")).toBeChecked();
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();
  await expect
    .poll(() => anfragen.length, { message: "Erneute Katalogsuche" })
    .toBe(2);
  for (const anfrageZeile of anfragen) {
    const pruefung = new URL(anfrageZeile);
    expect(pruefung.searchParams.get("q")).toBe("Wandern");
    expect(pruefung.searchParams.get("voiceConfiguration")).toBe("SATB");
    expect(pruefung.searchParams.get("material")).toBe("score");
  }

  expect(errors).toEqual([]);
});

test("Passende Fassung erscheint nur bei Fassungsfiltern", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(suchRoute, (route) =>
    route.fulfill(json(suchErgebnis(null, 1, 2, [wanderlied, maennerlied]))),
  );

  // Fassungsfilter: jeder Treffer nennt seine passende Fassung.
  await page.goto("/lieder/?stimmbesetzung=SATB");
  await expect(
    page.getByText("Passende Fassung: „Satz für gemischten Chor“"),
  ).toBeVisible();
  await page.goto("/lieder/?material=noten");
  await expect(
    page.getByText("Passende Fassung: „Satz für gemischten Chor“"),
  ).toBeVisible();

  // Reine Liedfilter und Suche nennen keine Fassungen, auch wenn
  // matchedArrangements geliefert würden.
  await page.goto("/lieder/?sprache=Deutsch");
  await expect(page.getByText(/Passende Fassung:/)).toHaveCount(0);
  await page.goto("/lieder/?suche=Wandern");
  await expect(page.getByText(/Passende Fassung:/)).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Trefferzahl nennt die begrenzte Gesamtzahl bei Suche und Filter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(suchRoute, (route) => {
    const abfrage = new URL(route.request().url()).searchParams.get("q");
    if (abfrage === "Wandern") {
      return route.fulfill(
        json(suchErgebnis("Wandern", 1, 2, [wanderlied, maennerlied])),
      );
    }
    if (abfrage === "Abendstille") {
      return route.fulfill(
        json(suchErgebnis("Abendstille", 1, 1, [abendstilleLied])),
      );
    }
    return route.fulfill(
      json(suchErgebnis(null, 1, 2, [wanderlied, maennerlied])),
    );
  });

  await page.goto("/lieder/?suche=Wandern&seite=1");
  await expect(page.getByText("2 Lieder gefunden.")).toBeVisible();

  await page.goto("/lieder/?suche=Abendstille&seite=1");
  await expect(page.getByText("1 Lied gefunden.")).toBeVisible();

  // Ohne Suche und Filter zählt die Liste nicht vor.
  await page.goto("/lieder/");
  await expect(page.getByText(/Lieder gefunden./)).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Leere Filterergebnisse bieten das Zurücksetzen an", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(suchRoute, (route) => {
    const frage = new URL(route.request().url());
    const abfrage = frage.searchParams.get("q");
    const gefiltert = [
      "voiceConfiguration",
      "accompaniment",
      "musicalKey",
      "language",
      "occasion",
      "tag",
      "material",
    ].some((name) => frage.searchParams.has(name));
    if (abfrage || gefiltert) {
      return route.fulfill(json(suchErgebnis(abfrage, 1, 0, [])));
    }
    return route.fulfill(json(suchErgebnis(null, 1, 1, [wanderlied])));
  });

  await page.goto("/lieder/?anlass=Sommerfest&seite=1");
  await expect(page.getByText("Keine Lieder gefunden.")).toBeVisible();
  await page.getByRole("button", { name: "Filter zurücksetzen" }).click();

  // Das Zurücksetzen kehrt zum ungefilterten Bestand zurück.
  await expect(page).toHaveURL(/\/lieder\/$/);
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Zurücksetzen löscht die Filter und behält die Suche", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    anfragen.push(route.request().url());
    return route.fulfill(json(suchErgebnis("Wandern", 1, 1, [wanderlied])));
  });

  await page.goto("/lieder/?suche=Wandern&stimmbesetzung=SATB&material=noten");
  await page
    .locator("details.lieder-filter")
    .getByRole("button", { name: "Zurücksetzen" })
    .click();

  // Ohne seite-Parameter: das Zurücksetzen startet die Suche neu.
  await expect(page).toHaveURL(/\/lieder\/\?suche=Wandern$/);
  await expect
    .poll(() => anfragen.length, { message: "Gefilterte Suche abgeschickt" })
    .toBe(2);
  const rueckAnfrage = new URL(anfragen[1]);
  expect(rueckAnfrage.searchParams.get("q")).toBe("Wandern");
  expect(rueckAnfrage.searchParams.has("voiceConfiguration")).toBe(false);
  expect(rueckAnfrage.searchParams.has("material")).toBe(false);

  expect(errors).toEqual([]);
});

test("Redaktion pflegt Sprache, Anlass und Schlagwörter im Katalog", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  // Der Katalogbestand trägt die bisherigen Werte (ARC-023): die Redaktion
  // bearbeitet sie sichtbar, statt sie unbesehen zu ersetzen.
  const song: SuchLied = {
    ...abendstilleLied,
    language: "Französisch",
    occasion: "Jahresabschluss",
    tags: ["Abendgesang"],
  };
  const patchKörper: Array<{
    language?: string;
    occasion?: string;
    tags?: string[];
  }> = [];
  await page.route(suchRoute, (route) =>
    route.fulfill(json(suchErgebnis("Abendstille", 1, 1, [song]))),
  );
  await page.route(liedRoute, (route) => {
    if (route.request().method() !== "PATCH") {
      return route.fulfill(json({ song }));
    }
    const körper = route.request().postDataJSON() as {
      language?: string;
      occasion?: string;
      tags?: string[];
    };
    patchKörper.push(körper);
    // Der bearbeitete Bestand trägt die neuen Werte (der Server nimmt sie an).
    song.language = körper.language ?? null;
    song.occasion = körper.occasion ?? null;
    song.tags = körper.tags ?? [];
    return route.fulfill(json({ song }));
  });

  await page.goto("/lieder/?suche=Abendstille&seite=1");
  const zeile = page
    .locator(".lieder-eintrag")
    .filter({ hasText: "Abendstille" });
  await zeile.getByRole("button", { name: "Bearbeiten", exact: true }).click();
  // Die bisherigen Werte stehen sichtbar im Formular.
  await expect(zeile.getByLabel("Sprache (optional)")).toHaveValue(
    "Französisch",
  );
  await expect(zeile.getByLabel("Anlass (optional)")).toHaveValue(
    "Jahresabschluss",
  );
  await expect(zeile.getByLabel("Schlagwort 1", { exact: true })).toHaveValue(
    "Abendgesang",
  );
  await zeile.getByLabel("Sprache (optional)").fill("Deutsch");
  await zeile.getByLabel("Anlass (optional)").fill("Sommerfest");
  await zeile.getByRole("button", { name: "Schlagwort hinzufügen" }).click();
  await zeile.getByLabel("Schlagwort 2", { exact: true }).fill("Wanderlied");
  await zeile.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();

  // Bekannte Werte wandern vollständig mit; das neue Schlagwort ergänzt die
  // vorhandene Liste, statt sie zu überschreiben.
  expect(patchKörper).toHaveLength(1);
  expect(patchKörper[0].language).toBe("Deutsch");
  expect(patchKörper[0].occasion).toBe("Sommerfest");
  expect(patchKörper[0].tags).toEqual(["Abendgesang", "Wanderlied"]);

  // Ein geleertes bekanntes Feld räumt den Wert weg (leere Zeichenkette
  // löscht), die unveränderten Werte bleiben dabei erhalten.
  await zeile.getByRole("button", { name: "Bearbeiten", exact: true }).click();
  await zeile.getByLabel("Sprache (optional)").fill("");
  await zeile.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();
  expect(patchKörper).toHaveLength(2);
  expect(patchKörper[1].language).toBe("");
  expect(patchKörper[1].occasion).toBe("Sommerfest");
  expect(patchKörper[1].tags).toEqual(["Abendgesang", "Wanderlied"]);

  expect(errors).toEqual([]);
});

test("Neues Lied übernimmt Sprache, Anlass und Schlagwörter", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  const postKörper: Array<{
    language?: string | null;
    occasion?: string | null;
    tags?: string[];
  }> = [];
  await page.route(suchRoute, (route) => {
    if (route.request().method() === "POST") {
      const körper = route.request().postDataJSON() as {
        language?: string | null;
        occasion?: string | null;
        tags?: string[];
      };
      postKörper.push(körper);
      return route.fulfill(
        json(
          {
            song: {
              id: "00000000-0000-0000-0000-0000000002a9",
              title: "Aurora",
              composer: null,
              lyricist: null,
              published: false,
              publishedAt: null,
              alternateTitles: [],
              arrangements: [],
              matchedIn: [],
              matchedArrangements: [],
              lyricsSnippet: null,
            },
          },
          201,
        ),
      );
    }
    return route.fulfill(json(suchErgebnis(null, 1, 0, [])));
  });

  await page.goto("/lieder/");
  await page.getByLabel("Titel", { exact: true }).fill("Aurora");
  await page.getByLabel("Sprache (optional)").fill("Deutsch");
  await page.getByLabel("Anlass (optional)").fill("Sommerfest");
  await page.getByRole("button", { name: "Schlagwort hinzufügen" }).click();
  await page.getByLabel("Schlagwort 1", { exact: true }).fill("Sommer");
  await page.getByRole("button", { name: "Lied anlegen" }).click();
  await expect(page.getByText("Lied angelegt.")).toBeVisible();

  expect(postKörper).toHaveLength(1);
  expect(postKörper[0].language).toBe("Deutsch");
  expect(postKörper[0].occasion).toBe("Sommerfest");
  expect(postKörper[0].tags).toEqual(["Sommer"]);

  expect(errors).toEqual([]);
});

const gepflegtesDetail = {
  song: {
    id: wandernId,
    title: "Das Wandern ist des Müllers Lust",
    composer: "Carl Friedrich Zöllner",
    lyricist: "Wilhelm Müller",
    published: true,
    publishedAt: "2026-09-01T10:00:00.000Z",
    createdAt: "2026-08-20T08:00:00.000Z",
    updatedAt: "2026-09-01T10:00:00.000Z",
    lyrics: null,
    language: "Deutsch",
    occasion: "Sommerfest",
    tags: ["Wanderlied"],
    alternateTitles: [],
    arrangements: [
      {
        id: fassungId,
        label: "Satz für gemischten Chor",
        arranger: "Josef Gabriel",
        voiceConfiguration: "SATB",
        accompaniment: "Klavier",
        musicalVersions: [
          {
            id: versionId,
            label: "Standardfassung",
            creator: "Josef Gabriel",
            assets: [],
          },
        ],
      },
    ],
  },
};

test("Fassungsformular schickt die Begleitung beim Arrangement mit", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  const arrangementKörper: Array<{
    accompaniment?: string | null;
    label?: string;
  }> = [];
  await page.route(`**/api/songs/${wandernId}`, (route) =>
    route.fulfill(json(gepflegtesDetail)),
  );
  // Die Bearbeitung schickt PATCH an die Arrangement-Adresse, die Anlage
  // einen POST an die Anlage-Adresse; beide tragen die Begleitung.
  await page.route(arrangementBearbeiten, (route) => {
    arrangementKörper.push(
      route.request().postDataJSON() as { accompaniment?: string | null },
    );
    return route.fulfill(json(gepflegtesDetail));
  });
  await page.route(`**/api/songs/${wandernId}/arrangements`, (route) => {
    arrangementKörper.push(
      route.request().postDataJSON() as { accompaniment?: string | null },
    );
    return route.fulfill(json(gepflegtesDetail, 201));
  });

  await page.goto(`/lied/?id=${wandernId}`);

  // Die Bearbeitung zeigt die bisherige Begleitung und schickt die neue.
  await page.getByRole("button", { name: "Arrangement bearbeiten" }).click();
  const bearbeitenBegleitung = page
    .getByLabel("Begleitung (optional)")
    .and(page.locator(`#arrangement-${fassungId}-begleitung`));
  await expect(bearbeitenBegleitung).toHaveValue("Klavier");
  await bearbeitenBegleitung.fill("Streicher");
  await page.getByRole("button", { name: "Arrangement speichern" }).click();
  await expect(page.getByText("Arrangement gespeichert.")).toBeVisible();

  expect(arrangementKörper).toHaveLength(1);
  expect(arrangementKörper[0].accompaniment).toBe("Streicher");

  // Ein neues Arrangement übernimmt die Begleitung ebenfalls.
  await page
    .getByLabel("Bezeichnung", { exact: true })
    .and(page.locator("#neues-arrangement-label"))
    .fill("Satz für Frauenchor");
  await page
    .getByLabel("Begleitung (optional)")
    .and(page.locator("#neues-arrangement-begleitung"))
    .fill("Stimme und Gitarre");
  await page.getByRole("button", { name: "Arrangement anlegen" }).click();
  await expect(page.getByText("Arrangement angelegt.")).toBeVisible();

  expect(arrangementKörper).toHaveLength(2);
  expect(arrangementKörper[1].accompaniment).toBe("Stimme und Gitarre");

  expect(errors).toEqual([]);
});

test("Lied bearbeiten zeigt Sprache, Anlass und Schlagwörter und räumt auf", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  let detail = JSON.parse(
    JSON.stringify(gepflegtesDetail),
  ) as typeof gepflegtesDetail;
  const patchKörper: Array<{
    language?: string;
    occasion?: string;
    tags?: string[];
  }> = [];
  await page.route(`**/api/songs/${wandernId}`, (route) => {
    if (route.request().method() !== "PATCH") {
      return route.fulfill(json(detail));
    }
    const körper = route.request().postDataJSON() as {
      language?: string;
      occasion?: string;
      tags?: string[];
    };
    patchKörper.push(körper);
    detail = JSON.parse(
      JSON.stringify({
        ...detail,
        song: {
          ...detail.song,
          language: körper.language ?? null,
          occasion: körper.occasion ?? null,
          tags: körper.tags ?? [],
        },
      }),
    ) as typeof gepflegtesDetail;
    return route.fulfill(json(detail));
  });

  await page.goto(`/lied/?id=${wandernId}`);
  await expect(page.getByLabel("Sprache (optional)")).toHaveValue("Deutsch");
  await expect(page.getByLabel("Anlass (optional)")).toHaveValue("Sommerfest");
  await expect(page.getByLabel("Schlagwort 1", { exact: true })).toHaveValue(
    "Wanderlied",
  );

  // Geleerte Felder räumen weg; die Bekannten gehen stets mit.
  await page.getByLabel("Anlass (optional)").fill("");
  await page.getByRole("button", { name: "Schlagwort 1 entfernen" }).click();
  await page.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();

  expect(patchKörper).toHaveLength(1);
  expect(patchKörper[0].language).toBe("Deutsch");
  expect(patchKörper[0].occasion).toBe("");
  expect(patchKörper[0].tags).toEqual([]);

  expect(errors).toEqual([]);
});
