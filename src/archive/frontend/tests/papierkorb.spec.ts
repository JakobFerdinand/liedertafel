import { expect, test } from "@playwright/test";
import {
  adminMe,
  datei,
  editorMe,
  json,
  liedDetail,
  memberMe,
  mockSitzung,
  mondId,
} from "./lieder-mock";

// #168: Datei ersetzen und löschen, Papierkorb der Redaktion, Mitglieder sehen
// keine Aktionen.

const altId = "00000000-0000-0000-0000-00000000c001";
const neuId = "00000000-0000-0000-0000-00000000c002";
const sitzungsId = "00000000-0000-0000-0000-0000000000bb";
const uploadMuster = /speicher\.test\/ubertragung/;

const alt = datei(altId, { fileName: "alt.pdf", rowVersion: 1 });
const neu = datei(neuId, { fileName: "neu.pdf", rowVersion: 1 });

test("Redaktion ersetzt eine Noten-Datei; das Lied zeigt nur die neue", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  let aktuell = [alt];
  let ladungen = 0;
  await page.route(`**/api/lieder/${mondId}`, (route) => {
    ladungen++;
    return route.fulfill(json(liedDetail(aktuell)));
  });
  const sitzungen: Record<string, unknown>[] = [];
  await page.route(
    `**/api/lieder/${mondId}/dateien/upload-session`,
    (route) => {
      sitzungen.push(route.request().postDataJSON());
      return route.fulfill(
        json(
          {
            uploadSessionId: sitzungsId,
            blobName: null,
            uploadUrl: "https://speicher.test/ubertragung?sig=1",
            expiresAt: "2026-09-19T12:15:00.000Z",
            maxBytes: 5242880,
            blockBytes: 5242880,
          },
          201,
        ),
      );
    },
  );
  await page.route(uploadMuster, (route) => route.fulfill(json({}, 201)));
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    aktuell = [neu];
    return route.fulfill(json({ datei: neu }));
  });

  await page.goto(`/lied/?id=${mondId}`);
  await expect(
    page.getByRole("article", { name: "Noten · Tenor 1" }),
  ).toBeVisible();
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Ersetzen · Tenor 1" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "neu.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.7"),
  });

  await expect.poll(() => sitzungen.length).toBe(1);
  expect(sitzungen[0]).toMatchObject({
    kind: "noten",
    replacesDateiId: altId,
    fileName: "neu.pdf",
  });
  await expect.poll(() => ladungen).toBeGreaterThan(1);
  await expect(
    page.getByRole("article", { name: "Noten · Tenor 1" }),
  ).toHaveCount(1);
  expect(errors).toEqual([]);
});

test("Redaktion löscht eine Datei nach Rückfrage", async ({ page }) => {
  await mockSitzung(page, editorMe);
  let aktuell = [alt];
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail(aktuell))),
  );
  const loeschungen: unknown[] = [];
  await page.route(`**/api/dateien/${altId}/delete`, (route) => {
    loeschungen.push(route.request().postDataJSON());
    aktuell = [];
    return route.fulfill({ status: 204 });
  });

  await page.goto(`/lied/?id=${mondId}`);
  await page.getByRole("button", { name: "Löschen · Tenor 1" }).click();
  await expect(
    page.getByText("„Tenor 1“ in den Papierkorb legen?"),
  ).toBeVisible();
  expect(loeschungen).toEqual([]);
  await page.getByRole("button", { name: "Ja, löschen" }).click();
  await expect(page.getByText("Noch keine Dateien hochgeladen.")).toBeVisible();
  expect(loeschungen).toEqual([{ rowVersion: 1 }]);
});

test("Mitglied sieht weder Ersetzen noch Löschen", async ({ page }) => {
  await mockSitzung(page, memberMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([alt]))),
  );
  await page.goto(`/lied/?id=${mondId}`);
  await expect(
    page.getByRole("article", { name: "Noten · Tenor 1" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: /Ersetzen|Löschen/ }),
  ).toHaveCount(0);
});

test("Der Papierkorb zeigt Lieder, Auftritte und Dateien; die Redaktion sieht kein endgültiges Entfernen", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(
      json({
        dateien: [
          {
            id: altId,
            kind: "noten",
            isPartitur: false,
            stimme: "Tenor 1",
            fileName: "alt.pdf",
            sizeBytes: 400000,
            liedId: mondId,
            liedTitle: "Der Mond ist aufgegangen",
            eventId: null,
            eventTitle: null,
            deletedAt: "2026-09-20T10:30:00.000Z",
            deletedBy: "Redaktion",
            replaced: true,
          },
        ],
        lieder: [
          {
            id: mondId,
            title: "Der Mond ist aufgegangen",
            deletedAt: "2026-09-21T08:00:00.000Z",
            deletedBy: "Chef",
          },
        ],
        auftritte: [
          {
            id: "00000000-0000-0000-0000-00000000b001",
            kind: "concert",
            title: "Frühjahrskonzert",
            deletedAt: "2026-09-22T19:00:00.000Z",
            deletedBy: "Chef",
          },
        ],
      }),
    ),
  );

  await page.goto("/verwaltung/papierkorb/");
  await expect(
    page.getByRole("heading", { name: "Papierkorb", level: 1 }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Lieder", level: 2 }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Auftritte", level: 2 }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Dateien", level: 2 }),
  ).toBeVisible();
  const lied = page
    .getByRole("listitem")
    .filter({ hasText: "Der Mond ist aufgegangen" });
  await expect(lied).toContainText("21.09.2026");
  const auftritt = page
    .getByRole("listitem")
    .filter({ hasText: "Frühjahrskonzert" });
  await expect(auftritt).toContainText("Konzert");
  await expect(
    page.getByRole("button", { name: /Endgültig entfernen/ }),
  ).toHaveCount(0);
});

test("Redaktion stellt ein Lied wieder her; die Frist-Ablehnung zeigt den deutschen Hinweis", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  let papierkorb = {
    dateien: [],
    lieder: [
      {
        id: mondId,
        title: "Der Mond ist aufgegangen",
        deletedAt: "2026-09-20T08:00:00.000Z",
        deletedBy: "Chef",
      },
    ],
    auftritte: [],
  };
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(json(papierkorb)),
  );
  await page.route(`**/api/lieder/${mondId}/restore`, (route) =>
    route.fulfill(
      json(
        {
          title: "Die Aufbewahrungsfrist ist abgelaufen.",
          detail:
            "Einträge bleiben 7 Tage im Papierkorb und werden danach endgültig entfernt.",
          status: 409,
        },
        409,
      ),
    ),
  );

  await page.goto("/verwaltung/papierkorb/");
  await page
    .getByRole("button", { name: `Wiederherstellen · Der Mond ist aufgegangen` })
    .click();
  await expect(
    page.getByText(
      "Einträge bleiben 7 Tage im Papierkorb und werden danach endgültig entfernt.",
    ),
  ).toBeVisible();
  await expect(
    page.getByRole("button", {
      name: `Wiederherstellen · Der Mond ist aufgegangen`,
    }),
  ).toBeVisible();

  // A second try succeeds: the empty trash shows its empty state.
  await page.unroute(`**/api/lieder/${mondId}/restore`);
  await page.route(`**/api/lieder/${mondId}/restore`, () => {
    papierkorb = { ...papierkorb, lieder: [] };
    return route.fulfill({ status: 204 });
  });
  await page
    .getByRole("button", { name: `Wiederherstellen · Der Mond ist aufgegangen` })
    .click();
  await expect(page.getByText("Der Papierkorb ist leer.")).toBeVisible();
});

test("Redaktion stellt einen Auftritt wieder her", async ({ page }) => {
  await mockSitzung(page, editorMe);
  const auftrittId = "00000000-0000-0000-0000-00000000b001";
  let papierkorb = {
    dateien: [],
    lieder: [],
    auftritte: [
      {
        id: auftrittId,
        kind: "concert",
        title: "Jubiläumskonzert",
        deletedAt: "2026-09-22T19:00:00.000Z",
        deletedBy: "Chef",
      },
    ],
  };
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(json(papierkorb)),
  );
  await page.route(`**/api/events/${auftrittId}/restore`, (route) => {
    papierkorb = { ...papierkorb, auftritte: [] };
    return route.fulfill({ status: 204 });
  });

  await page.goto("/verwaltung/papierkorb/");
  const wiederherstellen = page.getByRole("button", {
    name: "Wiederherstellen · Jubiläumskonzert",
  });
  await expect(wiederherstellen).toBeVisible();
  await wiederherstellen.click();
  await expect(page.getByText("Der Papierkorb ist leer.")).toBeVisible();
});

test("Die Verwaltung entfernt ein Lied endgültig; die Bestätigung schützt vor dem Versehen", async ({
  page,
}) => {
  await mockSitzung(page, adminMe);
  let papierkorb = {
    dateien: [],
    lieder: [
      {
        id: mondId,
        title: "Der Mond ist aufgegangen",
        deletedAt: "2026-09-20T08:00:00.000Z",
        deletedBy: "Chef",
      },
    ],
    auftritte: [],
  };
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(json(papierkorb)),
  );
  const entfernungen: unknown[] = [];
  await page.route(`**/api/papierkorb/lieder/${mondId}/remove`, (route) => {
    entfernungen.push(route.request().postDataJSON());
    papierkorb = { ...papierkorb, lieder: [] };
    return route.fulfill({ status: 204 });
  });

  await page.goto("/verwaltung/papierkorb/");
  expect(entfernungen).toEqual([]);
  await page
    .getByRole("button", { name: "Endgültig entfernen · Der Mond ist aufgegangen" })
    .click();
  expect(entfernungen).toEqual([]);
  await page
    .getByRole("button", {
      name: "Ja, endgültig entfernen · Der Mond ist aufgegangen",
    })
    .click();
  await expect(page.getByText("Der Papierkorb ist leer.")).toBeVisible();
  expect(entfernungen).toEqual([{}]);
});

test("Die Verwaltung entfernt einen Auftritt endgültig", async ({ page }) => {
  await mockSitzung(page, adminMe);
  const auftrittId = "00000000-0000-0000-0000-00000000b001";
  let papierkorb = {
    dateien: [],
    lieder: [],
    auftritte: [
      {
        id: auftrittId,
        kind: "concert",
        title: "Jubiläumskonzert",
        deletedAt: "2026-09-22T19:00:00.000Z",
        deletedBy: "Chef",
      },
    ],
  };
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(json(papierkorb)),
  );
  await page.route(`**/api/papierkorb/auftritte/${auftrittId}/remove`, (route) => {
    papierkorb = { ...papierkorb, auftritte: [] };
    return route.fulfill({ status: 204 });
  });

  await page.goto("/verwaltung/papierkorb/");
  await page
    .getByRole("button", { name: "Endgültig entfernen · Jubiläumskonzert" })
    .click();
  await page
    .getByRole("button", { name: "Ja, endgültig entfernen · Jubiläumskonzert" })
    .click();
  await expect(page.getByText("Der Papierkorb ist leer.")).toBeVisible();
});

test("Der Papierkorb listet Dateiname, Lied und Datum für die Redaktion", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(
      json({
        dateien: [
          {
            id: altId,
            kind: "noten",
            isPartitur: false,
            stimme: "Tenor 1",
            fileName: "alt.pdf",
            sizeBytes: 400000,
            liedId: mondId,
            liedTitle: "Der Mond ist aufgegangen",
            eventId: null,
            eventTitle: null,
            deletedAt: "2026-09-20T10:30:00.000Z",
            deletedBy: "Redaktion",
            replaced: true,
          },
        ],
      }),
    ),
  );

  await page.goto("/verwaltung/");
  await page.getByRole("link", { name: "Papierkorb" }).click();
  await expect(
    page.getByRole("heading", { name: "Papierkorb", level: 1 }),
  ).toBeVisible();
  const eintrag = page.getByRole("listitem").filter({ hasText: "alt.pdf" });
  await expect(eintrag).toContainText("Der Mond ist aufgegangen");
  await expect(eintrag).toContainText("Ersetzt am");
  await expect(eintrag).toContainText("20.09.2026");
  await expect(eintrag).toContainText("von Redaktion");
});

test("Der Papierkorb ist für Mitglieder nicht sichtbar", async ({ page }) => {
  await mockSitzung(page, memberMe);
  await page.goto("/verwaltung/papierkorb/");
  await expect(
    page.getByText("Der Papierkorb ist nur für die Redaktion sichtbar."),
  ).toBeVisible();
});

test("Redaktion stellt eine Datei wieder her; die Ablehnung zeigt den deutschen Hinweis", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  let aktuell = [
    {
      id: altId,
      kind: "noten",
      isPartitur: false,
      stimme: "Tenor 1",
      fileName: "alt.pdf",
      sizeBytes: 400000,
      liedId: mondId,
      liedTitle: "Der Mond ist aufgegangen",
      eventId: null,
      eventTitle: null,
      deletedAt: "2026-09-20T10:30:00.000Z",
      deletedBy: "Redaktion",
      replaced: false,
    },
  ];
  await page.route("**/api/papierkorb", (route) =>
    route.fulfill(json({ dateien: aktuell })),
  );
  await page.route(`**/api/dateien/${altId}/restore`, (route) =>
    route.fulfill(
      json(
        {
          title: "Der übergeordnete Eintrag ist noch im Papierkorb.",
          detail:
            "Der übergeordnete Eintrag ist noch im Papierkorb und muss erst wiederhergestellt werden.",
          status: 409,
        },
        409,
      ),
    ),
  );

  await page.goto("/verwaltung/papierkorb/");
  await page
    .getByRole("button", { name: "Wiederherstellen · alt.pdf" })
    .click();
  await expect(
    page.getByText(
      "Der übergeordnete Eintrag ist noch im Papierkorb und muss erst wiederhergestellt werden.",
    ),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Wiederherstellen · alt.pdf" }),
  ).toBeVisible();

  // A second try succeeds: the entry leaves the list.
  await page.unroute(`**/api/dateien/${altId}/restore`);
  await page.route(`**/api/dateien/${altId}/restore`, (route) => {
    aktuell = [];
    return route.fulfill({ status: 204 });
  });
  await page
    .getByRole("button", { name: "Wiederherstellen · alt.pdf" })
    .click();
  await expect(page.getByText("Der Papierkorb ist leer.")).toBeVisible();
});
