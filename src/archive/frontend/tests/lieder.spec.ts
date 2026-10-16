import { expect, test } from "@playwright/test";
import {
  editorMe,
  json,
  liste,
  memberMe,
  mockSitzung,
  mondEintrag,
  mondId,
  wolkenEintrag,
} from "./lieder-mock";

// #166: Liederkatalog des flachen Liedes — Liste, Titelsuche, Filter für
// Besetzung, Anlass und Dateityp, Seiten und Anlegen von Hand.

test("Mitglied sieht die Lieder mit Angaben und öffnet eines", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route("**/api/lieder", (route) =>
    route.fulfill(json(liste([mondEintrag, wolkenEintrag]))),
  );

  await page.goto("/lieder/");
  await expect(
    page.getByRole("heading", { name: "Alle Lieder" }),
  ).toBeVisible();
  const mond = page.getByRole("listitem").filter({
    hasText: "Der Mond ist aufgegangen",
  });
  await expect(mond.getByText("Komponist: J. A. P. Schulz")).toBeVisible();
  await expect(mond.getByText("TTBB · Abendlied · Noten")).toBeVisible();
  await expect(page.getByText("Urheber nicht erfasst")).toBeVisible();
  // Mitglieder legen keine Lieder an und sehen keinen Veröffentlichungsstatus.
  await expect(page.getByText("Neues Lied")).toHaveCount(0);
  await expect(page.getByText("Entwurf")).toHaveCount(0);

  await page.getByRole("link", { name: "Der Mond ist aufgegangen" }).click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  expect(errors).toEqual([]);
});

test("Die Titelsuche schickt den Begriff und zeigt die Treffer", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route("**/api/lieder*", (route) => {
    const url = new URL(route.request().url());
    anfragen.push(url.search);
    const treffer = url.searchParams.get("q") === "mond";
    return route.fulfill(
      json(
        liste(treffer ? [mondEintrag] : [mondEintrag, wolkenEintrag], {
          query: url.searchParams.get("q"),
        }),
      ),
    );
  });

  await page.goto("/lieder/");
  await page.getByLabel("Lieder suchen").fill("mond");
  await page.getByRole("button", { name: "Suchen" }).click();
  await expect(page).toHaveURL(/suche=mond/);
  await expect(page.getByRole("heading", { name: "1 Treffer" })).toBeVisible();
  await expect(page.getByText("Über den Wolken")).toHaveCount(0);
  expect(anfragen).toContain("?q=mond");
});

test("Filter für Besetzung, Anlass und Dateityp stehen in der Adresse", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  const anfragen: string[] = [];
  await page.route("**/api/lieder*", (route) => {
    const url = new URL(route.request().url());
    anfragen.push(url.search);
    const gefiltert = url.search !== "";
    return route.fulfill(
      json(liste(gefiltert ? [mondEintrag] : [mondEintrag, wolkenEintrag])),
    );
  });

  await page.goto("/lieder/");
  await page.getByText("Filter", { exact: true }).click();
  await page.getByLabel("Besetzung").selectOption("TTBB");
  await page.getByLabel("Anlass").selectOption("Abendlied");
  await page.getByLabel("Noten", { exact: true }).check();
  await page.getByRole("button", { name: "Filtern" }).click();

  await expect(page).toHaveURL(/besetzung=TTBB/);
  await expect(page).toHaveURL(/anlass=Abendlied/);
  await expect(page).toHaveURL(/dateityp=noten/);
  await expect(page.getByRole("heading", { name: "1 Treffer" })).toBeVisible();
  expect(anfragen).toContain("?besetzung=TTBB&anlass=Abendlied&dateiTyp=noten");
  const aktiv = page.getByRole("navigation", { name: "Aktive Filter" });
  await expect(aktiv.getByText("Besetzung: TTBB")).toBeVisible();
  await expect(aktiv.getByText("Dateityp: Noten")).toBeVisible();

  await aktiv
    .getByRole("button", { name: "Besetzung: TTBB entfernen" })
    .click();
  await expect(page).not.toHaveURL(/besetzung=/);
  await expect(page).toHaveURL(/anlass=Abendlied/);

  await page.getByRole("button", { name: "Alle Filter zurücksetzen" }).click();
  await expect(page).toHaveURL(/\/lieder\/$/);
});

test("Mehr als eine Seite blättert vor und zurück", async ({ page }) => {
  await mockSitzung(page, memberMe);
  await page.route("**/api/lieder*", (route) => {
    const seite = Number(
      new URL(route.request().url()).searchParams.get("page") ?? "1",
    );
    return route.fulfill(
      json(
        liste(seite === 1 ? [mondEintrag] : [wolkenEintrag], {
          page: seite,
          total: 21,
        }),
      ),
    );
  });

  await page.goto("/lieder/");
  await expect(page.getByText("Seite 1 von 2")).toBeVisible();
  await page.getByRole("button", { name: "Weiter" }).click();
  await expect(page).toHaveURL(/seite=2/);
  await expect(page.getByText("Seite 2 von 2")).toBeVisible();
  await expect(page.getByText("Über den Wolken")).toBeVisible();
});

test("Redaktion legt ein Lied von Hand an und landet bei den Dateien", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route("**/api/lieder", (route) => {
    if (route.request().method() === "POST") return route.fallback();
    return route.fulfill(json(liste([])));
  });
  let gesendet: unknown = null;
  await page.route("**/api/lieder", (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    gesendet = route.request().postDataJSON();
    return route.fulfill(
      json(
        {
          lied: {
            ...mondEintrag,
            lyrics: null,
            createdAt: "2026-09-19T12:00:00.000Z",
            updatedAt: "2026-09-19T12:00:00.000Z",
            rowVersion: 1,
            dateien: [],
          },
        },
        201,
      ),
    );
  });
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(
      json({
        lied: {
          ...mondEintrag,
          lyrics: null,
          createdAt: "2026-09-19T12:00:00.000Z",
          updatedAt: "2026-09-19T12:00:00.000Z",
          rowVersion: 1,
          dateien: [],
        },
      }),
    ),
  );

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Titel").fill("Der Mond ist aufgegangen");
  await page.getByLabel("Komponist (optional)").fill("J. A. P. Schulz");
  await page.getByLabel("Bearbeiter (optional)").fill("Max Mustermann");
  await page.getByLabel("Texter (optional)").fill("Matthias Claudius");
  await page.getByLabel("Besetzung (optional)").fill("TTBB");
  await page.getByLabel("Tonart (optional)").fill("F-Dur");
  await page.getByLabel("Sprache (optional)").fill("Deutsch");
  await page.getByLabel("Anlass (optional)").fill("Abendlied");
  await page.getByLabel("Liedtext (optional)").fill("Der Mond ist aufgegangen");
  await page.getByRole("button", { name: "Lied anlegen" }).click();

  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  expect(gesendet).toEqual({
    title: "Der Mond ist aufgegangen",
    composer: "J. A. P. Schulz",
    arranger: "Max Mustermann",
    lyricist: "Matthias Claudius",
    voiceConfiguration: "TTBB",
    musicalKey: "F-Dur",
    language: "Deutsch",
    lyrics: "Der Mond ist aufgegangen",
    occasion: "Abendlied",
  });
  await expect(
    page.getByRole("heading", { name: "Noten hochladen" }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

test("Ein Titel ist Pflicht beim Anlegen", async ({ page }) => {
  await mockSitzung(page, editorMe);
  await page.route("**/api/lieder", (route) => route.fulfill(json(liste([]))));

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByRole("button", { name: "Lied anlegen" }).click();
  await expect(page.getByText("Bitte einen Titel eingeben.")).toBeVisible();
});
