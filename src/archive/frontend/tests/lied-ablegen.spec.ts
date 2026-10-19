import { expect, test } from "@playwright/test";
import {
  datei,
  editorMe,
  json,
  liedDetail,
  liste,
  mockSitzung,
  mondEintrag,
  mondId,
} from "./lieder-mock";

// #175: Neues Lied durch Ablegen von Dateien — Titel sowie Art und Stimme je
// Datei füllen sich aus den Namen, bearbeiten, ein Klick auf "Speichern".

const sitzungsId = "00000000-0000-0000-0000-0000000000bb";

test("Dateien ablegen, bearbeiten und mit einem Klick als Lied speichern", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  let angelegt: Record<string, unknown> | null = null;
  await page.route("**/api/lieder", (route) => {
    if (route.request().method() !== "POST") {
      return route.fulfill(json(liste([mondEintrag])));
    }
    angelegt = route.request().postDataJSON();
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
  await page.route(/speicher\.test\/ubertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(
      json({ datei: datei("00000000-0000-0000-0000-00000000c001") }),
    ),
  );
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([]))),
  );

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await expect(
    page.getByRole("button", { name: "ohne Datei anlegen" }),
  ).toBeVisible();

  await page.getByLabel("Dateien auswählen").setInputFiles([
    {
      name: "Ave_Maria_T1.mp3",
      mimeType: "audio/mpeg",
      buffer: Buffer.from("ID3"),
    },
    {
      name: "Ave_Maria_T2.mp3",
      mimeType: "audio/mpeg",
      buffer: Buffer.from("ID3"),
    },
    {
      name: "Ave_Maria_B1.mp3",
      mimeType: "audio/mpeg",
      buffer: Buffer.from("ID3"),
    },
    {
      name: "Ave_Maria.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4"),
    },
  ]);

  await expect(page.getByLabel("Titel")).toHaveValue("Ave Maria");
  await expect(page.getByLabel("Stimme von Ave_Maria_T1.mp3")).toHaveValue(
    "Tenor 1",
  );
  await expect(page.getByLabel("Art von Ave_Maria_T1.mp3")).toHaveValue("uebe");
  await expect(page.getByLabel("Stimme von Ave_Maria_B1.mp3")).toHaveValue(
    "Bass 1",
  );
  await expect(page.getByLabel("Art von Ave_Maria.pdf")).toHaveValue(
    "partitur",
  );

  // Alles ist änderbar.
  await page.getByLabel("Titel").fill("Ave Maria (Bruckner)");
  await page.getByLabel("Stimme von Ave_Maria_T2.mp3").fill("Tenor 2 solo");

  await page.getByRole("button", { name: "Speichern" }).click();

  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  expect(angelegt).toMatchObject({ title: "Ave Maria (Bruckner)" });
  expect(
    sitzungen.map((s) => [s.kind, s.isPartitur, s.stimme, s.contentType]),
  ).toEqual([
    ["uebe", false, "Tenor 1", "audio/mpeg"],
    ["uebe", false, "Tenor 2 solo", "audio/mpeg"],
    ["uebe", false, "Bass 1", "audio/mpeg"],
    ["noten", true, null, "application/pdf"],
  ]);
  expect(errors).toEqual([]);
});

test("Ein gescheiterter Upload bleibt sichtbar, die anderen Dateien sind gespeichert", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route("**/api/lieder", (route) => {
    if (route.request().method() !== "POST") {
      return route.fulfill(json(liste([])));
    }
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
  let anzahl = 0;
  await page.route(
    `**/api/lieder/${mondId}/dateien/upload-session`,
    (route) => {
      anzahl += 1;
      if (anzahl === 1) {
        return route.fulfill(json({ title: "Der Speicher ist voll." }, 413));
      }
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
  await page.route(/speicher\.test\/ubertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(
      json({ datei: datei("00000000-0000-0000-0000-00000000c001") }),
    ),
  );

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles([
    { name: "Mond_T1.mp3", mimeType: "audio/mpeg", buffer: Buffer.from("ID3") },
    { name: "Mond_B1.mp3", mimeType: "audio/mpeg", buffer: Buffer.from("ID3") },
  ]);
  await page.getByRole("button", { name: "Speichern" }).click();

  await expect(page.locator("p[role=alert]")).toContainText(
    "Der Speicher ist voll.",
  );
  await expect(page.getByText("Hochgeladen")).toHaveCount(1);
  await expect(page).toHaveURL(/\/lieder\//);
  await expect(page.getByRole("link", { name: "Zum Lied" })).toBeVisible();
});
