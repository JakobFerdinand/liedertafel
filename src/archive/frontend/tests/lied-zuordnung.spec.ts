import { expect, type Page, test } from "@playwright/test";
import {
  datei,
  editorMe,
  json,
  liedDetail,
  liste,
  mockEntwuerfe,
  mockSitzung,
  mondEintrag,
  mondId,
} from "./lieder-mock";

// #179: Passt der Titel zu einem vorhandenen Lied, wird es angeboten; Annehmen
// fügt die Dateien dort hinzu, Ablehnen legt ein neues Lied an. Die Ablage auf
// der Lied-Seite benutzt denselben Ablauf. Die Reducer-Folgen prüft ziel.test.ts.

const sitzungsId = "00000000-0000-0000-0000-0000000000bb";
const neuId = "00000000-0000-0000-0000-00000000a099";

const mp3 = (name: string) => ({
  name,
  mimeType: "audio/mpeg",
  buffer: Buffer.from("ID3"),
});

type Zustand = {
  angelegt: Record<string, unknown> | null;
  sitzungen: string[];
  sucheVor: Promise<void>;
};

async function mockAblage(page: Page, suche?: Promise<void>) {
  const state: Zustand = {
    angelegt: null,
    sitzungen: [],
    sucheVor: suche ?? Promise.resolve(),
  };
  await mockSitzung(page, editorMe);
  await mockEntwuerfe(page);
  // Liste und Suche (mit Abfrage) und Anlegen (POST).
  await page.route(/\/api\/lieder(\?.*)?$/, async (route) => {
    if (route.request().method() === "POST") {
      state.angelegt = route.request().postDataJSON();
      return route.fulfill(
        json(
          {
            lied: {
              ...mondEintrag,
              id: neuId,
              title: String(state.angelegt?.title ?? ""),
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
    }
    await state.sucheVor;
    return route.fulfill(json(liste([mondEintrag])));
  });
  for (const id of [mondId, neuId]) {
    await page.route(`**/api/lieder/${id}`, (route) =>
      route.fulfill(json(liedDetail([]))),
    );
    await page.route(`**/api/lieder/${id}/dateien/upload-session`, (route) => {
      state.sitzungen.push(id);
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
    });
  }
  await page.route(/speicher\.test\/ubertragung/, (route) =>
    route.fulfill(json({}, 201)),
  );
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(
      json({ datei: datei("00000000-0000-0000-0000-00000000c001") }),
    ),
  );
  return state;
}

const dateien = [
  mp3("Der_Mond_ist_aufgegangen_T1.mp3"),
  mp3("Der_Mond_ist_aufgegangen_B1.mp3"),
];

test("ein passender Titel bietet das Lied an, Annehmen fügt die Dateien dort hinzu", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const state = await mockAblage(page);

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles(dateien);

  await expect(
    page.getByText("Zu ‚Der Mond ist aufgegangen‘ hinzufügen?"),
  ).toBeVisible();
  await page.getByRole("button", { name: "Ja, hinzufügen" }).click();
  await expect(
    page.getByText("Die Dateien kommen zu ‚Der Mond ist aufgegangen‘."),
  ).toBeVisible();

  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  // Kein neues Lied; beide Dateien gehen zum vorhandenen.
  expect(state.angelegt).toBeNull();
  expect(state.sitzungen).toEqual([mondId, mondId]);
  expect(errors).toEqual([]);
});

test("Ablehnen legt ein neues Lied an und bietet dasselbe nicht erneut an", async ({
  page,
}) => {
  const state = await mockAblage(page);

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles(dateien);

  await page.getByRole("button", { name: "Nein, neues Lied anlegen" }).click();
  await expect(page.getByText("hinzufügen?")).toHaveCount(0);
  await expect(page.getByRole("textbox", { name: "Titel" })).toHaveValue(
    "Der Mond ist aufgegangen",
  );
  // Ohne Wartezeit-Raten: ein Tippen wird erneut gesucht, der abgelehnte
  // Treffer kommt nicht wieder.
  await page
    .getByRole("textbox", { name: "Titel" })
    .fill("Der Mond ist aufgegangen ");
  await page.waitForTimeout(600);
  await expect(page.getByText("hinzufügen?")).toHaveCount(0);

  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${neuId}`));
  expect(state.angelegt).toMatchObject({ title: "Der Mond ist aufgegangen" });
  expect(state.sitzungen).toEqual([neuId, neuId]);
});

test("eine späte Suche nach geändertem Titel bietet nichts an", async ({
  page,
}) => {
  let frei: () => void = () => {};
  const warten = new Promise<void>((weiter) => {
    frei = weiter;
  });
  const state = await mockAblage(page, warten);

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles(dateien);
  // Die Suche nach dem ersten Titel läuft und hängt, dann wird der Titel geändert.
  await page.waitForTimeout(600);
  await page.getByRole("textbox", { name: "Titel" }).fill("Ganz anderes Lied");
  frei();
  await page.waitForTimeout(300);
  await expect(page.getByText("hinzufügen?")).toHaveCount(0);

  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${neuId}`));
  expect(state.angelegt).toMatchObject({ title: "Ganz anderes Lied" });
});

test("die Ablage auf der Lied-Seite fügt Dateien diesem Lied hinzu", async ({
  page,
}) => {
  const state = await mockAblage(page);

  await page.goto(`/lied/?id=${mondId}`);
  await page
    .getByLabel("Dateien zum Lied auswählen")
    .setInputFiles([mp3("Mond_T2.mp3")]);

  // Kein Formular zum Lied und kein Angebot: das Ziel steht fest.
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveCount(0);
  await expect(
    page.getByText("Die Dateien kommen zu ‚Der Mond ist aufgegangen‘."),
  ).toBeVisible();
  await page.getByRole("button", { name: "Speichern" }).click();

  await expect.poll(() => state.sitzungen).toEqual([mondId]);
  expect(state.angelegt).toBeNull();
  // Danach beginnt ein frischer Entwurf.
  await expect(page.getByLabel("Dateien zum Lied auswählen")).toBeVisible();
  await expect(page.getByRole("button", { name: "Speichern" })).toHaveCount(0);
});
