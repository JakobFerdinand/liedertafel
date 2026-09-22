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
const entwurfId = "00000000-0000-0000-0000-0000000002b1";
const fassungId = "00000000-0000-0000-0000-00000000c20a";

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
  lyricsSnippet: null;
  lyrics?: string | null;
};

const wandernLied: SuchLied = {
  id: wandernId,
  title: "Das Wandern ist des Müllers Lust",
  composer: "Carl Friedrich Zöllner",
  lyricist: "Wilhelm Müller",
  published: true,
  publishedAt: "2026-09-01T10:00:00.000Z",
  alternateTitles: ["Müllertanz"],
  arrangements: [
    {
      id: fassungId,
      label: "Satz für gemischten Chor",
      arranger: "Josef Gabriel",
    },
  ],
  matchedIn: ["title", "alternateTitles", "arrangements", "lyrics"],
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
  lyricsSnippet: null,
};

const entwurfLied: SuchLied = {
  id: entwurfId,
  title: "Nachtgesang",
  composer: null,
  lyricist: null,
  published: false,
  publishedAt: null,
  alternateTitles: ["Abendlied"],
  arrangements: [],
  matchedIn: ["alternateTitles"],
  lyricsSnippet: null,
};

// Der Member-Katalog listet nur veröffentlichte Lieder; die Redaktion sieht
// zusätzlich Entwürfe (gleiche Route, andere Bestandsansicht).
function bestandFür(rolle: "editor" | "member") {
  return rolle === "editor" ? [wandernLied, entwurfLied] : [wandernLied];
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

test("Startseite-Suche öffnet den Katalog mit der gesuchten Seite", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route("**/api/build", (route) =>
    route.fulfill(
      json({ application: "archive", version: "test", development: true }),
    ),
  );
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    anfragen.push(route.request().url());
    return route.fulfill(json(suchErgebnis("Wandern", 1, 1, [wandernLied])));
  });

  await page.goto("/");
  await page.getByLabel("Lieder suchen").fill("Wandern");
  await page.getByRole("button", { name: "Suchen" }).click();
  await expect(page).toHaveURL(/\/lieder\/\?suche=Wandern&seite=1$/);
  await expect
    .poll(() => anfragen.length, { message: "Katalogsuche abgeschickt" })
    .toBe(1);
  expect(anfragen[0]).toContain("/api/songs?q=Wandern");
  expect(anfragen[0]).not.toContain("page=");
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Suchergebnisse zeigen Fundstellen und behalten die Abfrage beim Neuladen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route(suchRoute, (route) => {
    anfragen.push(route.request().url());
    return route.fulfill(json(suchErgebnis("Wandern", 1, 1, [wandernLied])));
  });

  await page.goto("/lieder/?suche=Wandern&seite=1");
  const eintrag = page.getByRole("link", {
    name: "Das Wandern ist des Müllers Lust",
  });
  await expect(eintrag).toBeVisible();
  await expect(eintrag).toHaveAttribute(
    "href",
    new RegExp(`lied/\\?id=${wandernId}`),
  );
  await expect(page.getByText("Auch bekannt als: Müllertanz")).toBeVisible();
  await expect(
    page.getByText("Getroffen: Fassung „Satz für gemischten Chor“"),
  ).toBeVisible();
  await expect(page.getByText("Getroffen im Liedtext.")).toBeVisible();

  await page.reload();
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();
  expect(anfragen.length).toBeGreaterThanOrEqual(2);
  for (const url of anfragen) {
    expect(url).toContain("/api/songs?q=Wandern");
  }

  expect(errors).toEqual([]);
});

test("Leere Trefferlisten erklären sich auf Deutsch", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(suchRoute, (route) => {
    const frage = new URL(route.request().url());
    const abfrage = frage.searchParams.get("q");
    return route.fulfill(json(suchErgebnis(abfrage, 1, 0, [])));
  });

  await page.goto("/lieder/?suche=Spinnrad&seite=1");
  await expect(page.getByText("Keine Lieder gefunden.")).toBeVisible();

  await page.goto("/lieder/");
  await expect(page.getByText("Noch keine Lieder im Katalog.")).toBeVisible();

  expect(errors).toEqual([]);
});

test("Ausgefallene Suche lässt sich erneut versuchen", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  let ausgefallen = true;
  await page.route(suchRoute, (route) => {
    if (ausgefallen) {
      return route.fulfill(problem("Suchdienst nicht verfügbar.", 500));
    }
    return route.fulfill(json(suchErgebnis("Wandern", 1, 1, [wandernLied])));
  });

  await page.goto("/lieder/?suche=Wandern&seite=1");
  await expect(
    page.getByText("Das Archiv antwortet nicht. Bitte erneut versuchen."),
  ).toBeVisible();

  ausgefallen = false;
  await page.getByRole("button", { name: "Erneut versuchen" }).click();
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Blättern erhält die Abfrage und liefert die passenden Seiten", async ({
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

  await page.goto("/lieder/?suche=Studie&seite=1");
  await expect(
    page.getByText("Studie Nummer 1", { exact: true }),
  ).toBeVisible();
  await expect(page.getByText("Seite 1 von 3")).toBeVisible();
  const zurück = page.getByRole("button", { name: "Zurück" });
  const weiter = page.getByRole("button", { name: "Weiter" });
  await expect(zurück).toBeDisabled();
  await expect(weiter).toBeEnabled();

  await weiter.click();
  await expect(page).toHaveURL(/seite=2/);
  await expect(page.getByText("Studie Nummer 21")).toBeVisible();
  await expect(page.getByText("Seite 2 von 3")).toBeVisible();
  await expect(zurück).toBeEnabled();
  expect(anfragen.at(-1)).toContain("/api/songs?q=Studie&page=2");

  await weiter.click();
  await expect(page).toHaveURL(/seite=3/);
  await expect(page.getByText("Studie Nummer 41")).toBeVisible();
  await expect(page.getByText("Seite 3 von 3")).toBeVisible();
  await expect(weiter).toBeDisabled();
  await expect(zurück).toBeEnabled();
  expect(anfragen.at(-1)).toContain("/api/songs?q=Studie&page=3");

  await zurück.click();
  await expect(page).toHaveURL(/seite=2/);
  await expect(page.getByText("Studie Nummer 21")).toBeVisible();
  await expect(weiter).toBeEnabled();
  expect(anfragen.at(-1)).toContain("/api/songs?q=Studie&page=2");

  expect(errors).toEqual([]);
});

test("Redaktion sieht Entwürfe in Suchergebnissen", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(suchRoute, (route) =>
    route.fulfill(
      json(suchErgebnis("gesang", 1, 2, [abendstilleLied, entwurfLied])),
    ),
  );

  await page.goto("/lieder/?suche=gesang&seite=1");
  await expect(page.getByText("Entwurf")).toBeVisible();
  await expect(page.getByText("Veröffentlicht")).toBeVisible();
  await expect(page.getByRole("link", { name: "Nachtgesang" })).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Bearbeiten", exact: true }),
  ).toHaveCount(2);
  await expect(page.getByRole("heading", { name: "Neues Lied" })).toBeVisible();

  expect(errors).toEqual([]);
});

test("Mitglieder sehen in Suchergebnissen nur Veröffentlichtes", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(suchRoute, (route) =>
    route.fulfill(json(suchErgebnis("gesang", 1, 1, bestandFür("member")))),
  );

  await page.goto("/lieder/?suche=gesang&seite=1");
  await expect(
    page.getByRole("link", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();
  await expect(page.getByText("Nachtgesang")).toHaveCount(0);
  await expect(page.getByText("Entwurf")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Bearbeiten", exact: true }),
  ).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Neues Lied" })).toHaveCount(
    0,
  );

  expect(errors).toEqual([]);
});

test("Redaktion pflegt Liedtext und andere Titel aus dem Suchergebnis", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  const song: SuchLied = { ...abendstilleLied };
  const patchKörper: Array<{
    lyrics?: string | null;
    alternateTitles?: string[];
  }> = [];
  await page.route(suchRoute, (route) =>
    route.fulfill(json(suchErgebnis("Abendstille", 1, 1, [song]))),
  );
  await page.route(liedRoute, (route) => {
    if (route.request().method() !== "PATCH") {
      return route.fulfill(json({ song }));
    }
    const körper = route.request().postDataJSON() as {
      lyrics?: string | null;
      alternateTitles?: string[];
    };
    patchKörper.push(körper);
    song.lyrics = körper.lyrics ?? null;
    song.alternateTitles = körper.alternateTitles ?? [];
    return route.fulfill(json({ song }));
  });

  await page.goto("/lieder/?suche=Abendstille&seite=1");
  const zeile = page
    .locator(".lieder-eintrag")
    .filter({ hasText: "Abendstille" });
  await zeile.getByRole("button", { name: "Bearbeiten", exact: true }).click();
  await zeile
    .getByLabel("Liedtext (optional)")
    .fill("Abendstille überall, der Tag klingt aus.");
  await zeile.getByRole("button", { name: "Anderen Titel hinzufügen" }).click();
  await zeile.getByLabel("Anderer Titel 1").fill("Ruhelied");
  await zeile.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();
  await expect(page.getByText("Auch bekannt als: Ruhelied")).toBeVisible();

  expect(patchKörper).toHaveLength(1);
  expect(patchKörper[0].lyrics).toBe(
    "Abendstille überall, der Tag klingt aus.",
  );
  expect(patchKörper[0].alternateTitles).toEqual(["Ruhelied"]);

  expect(errors).toEqual([]);
});
