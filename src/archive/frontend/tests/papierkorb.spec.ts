import { expect, test } from "@playwright/test";
import {
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
