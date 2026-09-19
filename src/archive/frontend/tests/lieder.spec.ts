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

const publishedId = "00000000-0000-0000-0000-00000000000a";
const draftId = "00000000-0000-0000-0000-00000000000b";

const publishedSong = {
  id: publishedId,
  title: "Das Wandern ist des Müllers Lust",
  composer: "Carl Friedrich Zöllner",
  lyricist: "Wilhelm Müller",
  published: true,
  publishedAt: "2026-09-01T10:00:00.000Z",
};

const draftSong = {
  id: draftId,
  title: "Nachtgesang",
  composer: null,
  lyricist: null,
  published: false,
  publishedAt: null,
};

const publishedDetail = {
  song: {
    ...publishedSong,
    createdAt: "2026-08-20T08:00:00.000Z",
    updatedAt: "2026-09-01T10:00:00.000Z",
    arrangements: [
      {
        id: "00000000-0000-0000-0000-00000000c00a",
        label: "Satz für gemischten Chor",
        arranger: "Josef Gabriel",
        musicalVersions: [
          {
            id: "00000000-0000-0000-0000-00000000d00a",
            label: "Standardfassung",
            creator: "Josef Gabriel",
          },
          {
            id: "00000000-0000-0000-0000-00000000d00b",
            label: "Einfache Fassung",
            creator: null,
          },
        ],
      },
    ],
  },
};

const originalfassungId = "00000000-0000-0000-0000-00000000c10a";
const maennerchorId = "00000000-0000-0000-0000-00000000c20a";
const chorsatzGId = "00000000-0000-0000-0000-00000000d10a";
const chorsatzEsId = "00000000-0000-0000-0000-00000000d10b";
const maennerchorFassungId = "00000000-0000-0000-0000-00000000d20a";

const mehrfachDetail = {
  song: {
    ...publishedSong,
    createdAt: "2026-08-20T08:00:00.000Z",
    updatedAt: "2026-09-10T08:00:00.000Z",
    arrangements: [
      {
        id: originalfassungId,
        label: "Originalfassung",
        arranger: null,
        voiceConfiguration: null,
        musicalVersions: [
          {
            id: chorsatzGId,
            label: "Chorsatz",
            creator: null,
            musicalKey: "G-Dur",
          },
          {
            id: chorsatzEsId,
            label: "Chorsatz",
            creator: null,
            musicalKey: "Es-Dur",
          },
        ],
      },
      {
        id: maennerchorId,
        label: "Satz für Männerchor",
        arranger: "Hans Schmid",
        voiceConfiguration: "TTBB",
        musicalVersions: [
          {
            id: maennerchorFassungId,
            label: "Männerchor",
            creator: "Hans Schmid",
            musicalKey: null,
          },
        ],
      },
    ],
  },
};

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

test("Mitglied sieht veröffentlichte Lieder ohne Redaktionswerkzeuge", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route("**/api/songs", (route) =>
    route.fulfill(json({ songs: [publishedSong] })),
  );

  await page.goto("/lieder/");
  // Die Seite liefert h1 und die Komponente zusätzlich ein h2 mit gleichem
  // Titel; die Auswahl bleibt deshalb auf das erste Element begrenzt.
  await expect(
    page.getByRole("heading", { name: "Liederkatalog" }).first(),
  ).toBeVisible();
  const eintrag = page.getByRole("link", {
    name: "Das Wandern ist des Müllers Lust",
  });
  await expect(eintrag).toBeVisible();
  await expect(eintrag).toHaveAttribute(
    "href",
    new RegExp(`lied/\\?id=${publishedId}`),
  );
  await expect(
    page.getByText("Komponist: Carl Friedrich Zöllner"),
  ).toBeVisible();
  await expect(page.getByText("Nachtgesang")).toHaveCount(0);
  await expect(page.getByText("Entwurf")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Neues Lied" })).toHaveCount(
    0,
  );
  await expect(
    page.getByRole("button", { name: "Veröffentlichen" }),
  ).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Zurückziehen" })).toHaveCount(
    0,
  );
  await expect(
    page.getByRole("button", { name: "Bearbeiten", exact: true }),
  ).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Mitglied öffnet ein veröffentlichtes Lied; Entwürfe bleiben unfindbar", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route("**/api/songs", (route) =>
    route.fulfill(json({ songs: [publishedSong] })),
  );
  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(publishedDetail)),
  );
  await page.route(`**/api/songs/${draftId}`, (route) =>
    route.fulfill(problem("Lied nicht gefunden.", 404)),
  );

  await page.goto(`/lied/?id=${publishedId}`);
  await expect(
    page.getByRole("heading", { name: "Das Wandern ist des Müllers Lust" }),
  ).toBeVisible();
  await expect(
    page.getByText("Komponist: Carl Friedrich Zöllner"),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Satz für gemischten Chor" }),
  ).toBeVisible();
  await expect(page.getByText("Standardfassung · Josef Gabriel")).toBeVisible();
  await expect(page.getByText("Einfache Fassung")).toBeVisible();
  await expect(page.getByText("Entwurf")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "Veröffentlichen" }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("heading", { name: "Lied bearbeiten" }),
  ).toHaveCount(0);

  await page.goto(`/lied/?id=${draftId}`);
  await expect(
    page.getByText("Dieses Lied wurde nicht gefunden."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Redaktion sieht Entwürfe, legt ein Lied an und veröffentlicht es", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  type LiedArt = {
    id: string;
    title: string;
    composer: string | null;
    lyricist: string | null;
    published: boolean;
    publishedAt: string | null;
  };
  let bestand: LiedArt[] = [publishedSong, draftSong];
  await page.route("**/api/songs", (route) => {
    if (route.request().method() === "POST") {
      const neu = {
        id: "00000000-0000-0000-0000-00000000000c",
        title: "Aurora",
        composer: null,
        lyricist: null,
        published: false,
        publishedAt: null,
      };
      bestand = [...bestand, neu];
      return route.fulfill(json({ song: neu }, 201));
    }
    return route.fulfill(json({ songs: bestand }));
  });
  await page.route(`**/api/songs/${draftId}/publish`, (route) => {
    bestand = [
      publishedSong,
      {
        ...draftSong,
        published: true,
        publishedAt: "2026-09-19T12:00:00.000Z",
      },
    ];
    return route.fulfill(json({ song: bestand[1] }));
  });

  await page.goto("/lieder/");
  await expect(page.getByRole("heading", { name: "Neues Lied" })).toBeVisible();
  await expect(page.getByText("Entwurf")).toBeVisible();

  await page.getByLabel("Titel", { exact: true }).fill("Aurora");
  await page.getByRole("button", { name: "Lied anlegen" }).click();
  await expect(page.getByText("Lied angelegt.")).toBeVisible();
  await expect(page.getByText("Aurora").first()).toBeVisible();

  await page
    .getByRole("listitem")
    .filter({ hasText: "Nachtgesang" })
    .getByRole("button", { name: "Veröffentlichen" })
    .click();
  await expect(page.getByText("Lied veröffentlicht.")).toBeVisible();
  const nachtZeile = page
    .getByRole("listitem")
    .filter({ hasText: "Nachtgesang" });
  await expect(nachtZeile.getByText("Entwurf")).toHaveCount(0);
  await expect(nachtZeile.getByText("Veröffentlicht")).toBeVisible();
  await expect(
    nachtZeile.getByRole("button", { name: "Zurückziehen" }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Fehlermeldungen aus ProblemDetails werden angezeigt", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route("**/api/songs", (route) => {
    if (route.request().method() === "POST") {
      return route.fulfill(
        problem("Bitte geben Sie einen gültigen Titel an.", 400),
      );
    }
    return route.fulfill(json({ songs: [publishedSong, draftSong] }));
  });
  await page.route(`**/api/songs/${publishedId}`, (route) => {
    if (route.request().method() === "PATCH") {
      return route.fulfill(
        problem("Der Eintrag wurde zwischenzeitlich geändert.", 409),
      );
    }
    return route.fulfill(json(publishedDetail));
  });

  await page.goto("/lieder/");

  await page.getByRole("button", { name: "Lied anlegen" }).click();
  await expect(page.getByText("Bitte einen Titel eingeben.")).toBeVisible();

  await page.getByLabel("Titel", { exact: true }).fill("Doppelter Titel");
  await page.getByRole("button", { name: "Lied anlegen" }).click();
  await expect(
    page.getByText("Bitte geben Sie einen gültigen Titel an."),
  ).toBeVisible();

  const zeile = page
    .getByRole("listitem")
    .filter({ hasText: "Das Wandern ist des Müllers Lust" });
  await zeile.getByRole("button", { name: "Bearbeiten", exact: true }).click();
  await zeile.getByLabel(/Titel/).fill("Geänderte Weise");
  await zeile.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(
    page.getByText("Der Eintrag wurde zwischenzeitlich geändert."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Redaktion sieht Veröffentlicht-Marke und Steuerungen am Lied", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(publishedDetail)),
  );
  await page.route(`**/api/songs/${draftId}`, (route) =>
    route.fulfill(
      json({
        song: {
          ...draftSong,
          createdAt: "2026-09-01T08:00:00.000Z",
          updatedAt: "2026-09-01T08:00:00.000Z",
          arrangements: [],
        },
      }),
    ),
  );

  await page.goto(`/lied/?id=${publishedId}`);
  await expect(page.getByText("Veröffentlicht")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Veröffentlichung zurückziehen" }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Lied bearbeiten" }),
  ).toBeVisible();

  await page.goto(`/lied/?id=${draftId}`);
  await expect(page.getByText("Entwurf")).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Veröffentlichen" }),
  ).toBeVisible();
  await expect(
    page.getByText("Für dieses Lied ist noch keine Fassung erfasst."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Liederkatalog verlangt Anmeldung ohne Sitzung", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, { authenticated: false });

  await page.goto("/lieder/");
  await expect(
    page.getByText("Bitte anmelden, um den Liederkatalog zu sehen."),
  ).toBeVisible();
  await page.getByRole("main").getByRole("link", { name: "Anmelden" }).click();
  await expect(page).toHaveURL(/\/anmelden\//);

  expect(errors).toEqual([]);
});

test("Mitglied sieht beide Arrangements mit Tonarten und wählt per Klick", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(mehrfachDetail)),
  );

  await page.goto(`/lied/?id=${publishedId}`);
  const wahl = page.getByRole("group");
  await expect(wahl).toBeVisible();

  const originalGruppe = page
    .getByRole("heading", { name: "Originalfassung" })
    .locator("..");
  const maennerGruppe = page
    .getByRole("heading", { name: "Satz für Männerchor" })
    .locator("..");
  await expect(originalGruppe).toHaveAttribute("data-gewaehlt", "true");
  await expect(maennerGruppe).not.toHaveAttribute("data-gewaehlt");
  await expect(
    maennerGruppe.getByText("Bearbeitung: Hans Schmid"),
  ).toBeVisible();
  await expect(
    maennerGruppe.getByText("Stimmkonfiguration: TTBB"),
  ).toBeVisible();

  const chorsatzG = wahl.getByRole("button", {
    name: "Chorsatz · Tonart: G-Dur",
  });
  const chorsatzEs = wahl.getByRole("button", {
    name: "Chorsatz · Tonart: Es-Dur",
  });
  const maennerFassung = wahl.getByRole("button", {
    name: "Männerchor · Hans Schmid",
  });
  await expect(chorsatzG).toHaveAttribute("aria-pressed", "true");
  await expect(chorsatzEs).toHaveAttribute("aria-pressed", "false");
  await expect(maennerFassung).toHaveAttribute("aria-pressed", "false");

  await maennerFassung.click();
  await expect(maennerGruppe).toHaveAttribute("data-gewaehlt", "true");
  await expect(originalGruppe).not.toHaveAttribute("data-gewaehlt");
  await expect(maennerFassung).toHaveAttribute("aria-pressed", "true");
  await expect(chorsatzG).toHaveAttribute("aria-pressed", "false");

  await chorsatzEs.click();
  await expect(originalGruppe).toHaveAttribute("data-gewaehlt", "true");
  await expect(chorsatzEs).toHaveAttribute("aria-pressed", "true");
  await expect(chorsatzG).toHaveAttribute("aria-pressed", "false");
  await expect(page).toHaveURL(
    new RegExp(`fassung=${originalfassungId}&version=${chorsatzEsId}`),
  );

  expect(errors).toEqual([]);
});

test("Direktlink mit Fassungs- und Versionsparameter wählt die richtige Fassung", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(mehrfachDetail)),
  );

  await page.goto(
    `/lied/?id=${publishedId}&fassung=${maennerchorId}&version=${maennerchorFassungId}`,
  );
  const maennerGruppe = page
    .getByRole("heading", { name: "Satz für Männerchor" })
    .locator("..");
  await expect(maennerGruppe).toHaveAttribute("data-gewaehlt", "true");
  await expect(
    maennerGruppe.getByRole("button", { name: "Männerchor · Hans Schmid" }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(
    page.getByRole("heading", { name: "Originalfassung" }).locator(".."),
  ).not.toHaveAttribute("data-gewaehlt");

  expect(errors).toEqual([]);
});

test("Ungültige Fassungsparameter fallen auf die erste Fassung zurück", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(mehrfachDetail)),
  );

  await page.goto(
    `/lied/?id=${publishedId}&fassung=nicht-ein-uuid&version=auch-nicht`,
  );
  const originalGruppe = page
    .getByRole("heading", { name: "Originalfassung" })
    .locator("..");
  await expect(originalGruppe).toHaveAttribute("data-gewaehlt", "true");
  await expect(
    originalGruppe.getByRole("button", { name: "Chorsatz · Tonart: G-Dur" }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(
    page.getByRole("heading", { name: "Satz für Männerchor" }).locator(".."),
  ).not.toHaveAttribute("data-gewaehlt");

  expect(errors).toEqual([]);
});

test("Redaktion legt Arrangement und Fassung an und sieht Tonarten", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const arrangementNachAnlage = {
    id: "00000000-0000-0000-0000-00000000c30a",
    label: "Satz für Frauenchor",
    arranger: "Anna Berger",
    voiceConfiguration: "SSA",
    musicalVersions: [],
  };
  const versionNachAnlage = {
    id: "00000000-0000-0000-0000-00000000d30a",
    label: "Frauenchor",
    creator: "Anna Berger",
    musicalKey: "F-Dur",
  };

  const detailVorher = JSON.parse(
    JSON.stringify(mehrfachDetail),
  ) as typeof mehrfachDetail;
  const detailMitArrangement = {
    song: {
      ...detailVorher.song,
      updatedAt: "2026-09-19T09:00:00.000Z",
      arrangements: [...detailVorher.song.arrangements, arrangementNachAnlage],
    },
  };
  const detailMitFassung = {
    song: {
      ...detailMitArrangement.song,
      updatedAt: "2026-09-19T09:10:00.000Z",
      arrangements: detailMitArrangement.song.arrangements.map((eintrag) =>
        eintrag.id === arrangementNachAnlage.id
          ? {
              ...eintrag,
              musicalVersions: [...eintrag.musicalVersions, versionNachAnlage],
            }
          : eintrag,
      ),
    },
  };

  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(detailVorher)),
  );
  await page.route(`**/api/songs/${publishedId}/arrangements`, (route) =>
    route.fulfill(json(detailMitArrangement, 201)),
  );
  await page.route(
    `**/api/arrangements/${arrangementNachAnlage.id}/versions`,
    (route) => route.fulfill(json(detailMitFassung, 201)),
  );

  await page.goto(`/lied/?id=${publishedId}`);

  await page
    .getByLabel("Bezeichnung", { exact: true })
    .and(page.locator("#neues-arrangement-label"))
    .fill("Satz für Frauenchor");
  await page
    .getByLabel("Bearbeiter (optional)")
    .and(page.locator("#neues-arrangement-person"))
    .fill("Anna Berger");
  await page
    .getByLabel("Stimmkonfiguration (optional)")
    .and(page.locator("#neues-arrangement-zusatz"))
    .fill("SSA");
  await page.getByRole("button", { name: "Arrangement anlegen" }).click();
  await expect(page.getByText("Arrangement angelegt.")).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Satz für Frauenchor" }),
  ).toBeVisible();
  await expect(page.getByText("Stimmkonfiguration: SSA")).toBeVisible();
  await expect(page.getByText("Bearbeitung: Anna Berger")).toBeVisible();

  const frauenBlock = page
    .locator(".lied-fassung-block")
    .filter({ hasText: "Satz für Frauenchor" });
  await frauenBlock.getByRole("button", { name: "Fassung hinzufügen" }).click();
  await frauenBlock
    .locator("input[id^='fassung-'][id$='-label']")
    .fill("Frauenchor");
  await frauenBlock
    .locator("input[id^='fassung-'][id$='-person']")
    .fill("Anna Berger");
  await frauenBlock
    .locator("input[id^='fassung-'][id$='-zusatz']")
    .fill("F-Dur");
  await frauenBlock.getByRole("button", { name: "Fassung anlegen" }).click();
  await expect(page.getByText("Fassung angelegt.")).toBeVisible();
  const frauenWahl = page
    .getByRole("heading", { name: "Satz für Frauenchor" })
    .locator("..");
  await expect(
    frauenWahl.getByRole("button", {
      name: "Frauenchor · Anna Berger · Tonart: F-Dur",
    }),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Fehlende optionale Felder erscheinen nicht als Platzhaltertext", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${publishedId}`, (route) =>
    route.fulfill(json(mehrfachDetail)),
  );

  await page.goto(`/lied/?id=${publishedId}`);
  await expect(page.getByText("Stimmkonfiguration: TTBB")).toBeVisible();
  await expect(page.getByText(/Stimmkonfiguration:/)).toHaveCount(1);
  await expect(page.getByText(/Bearbeitung:/)).toHaveCount(1);
  await expect(page.getByText(/Tonart: G-Dur/)).toBeVisible();
  await expect(page.getByText(/Tonart: Es-Dur/)).toBeVisible();
  await expect(page.getByText(/Tonart:/)).toHaveCount(2);
  await expect(page.getByText("unbekannt")).toHaveCount(0);

  expect(errors).toEqual([]);
});
