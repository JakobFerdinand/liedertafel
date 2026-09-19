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
  await expect(
    page.getByRole("heading", { name: "Liederkatalog" }),
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
