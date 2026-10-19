import { expect, type Page, test } from "@playwright/test";
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

// #177: Das Formular füllt sich aus dem Lesen der PDF. Das Lesen ist hier der
// Server-Antwort nachgebildet (POST /api/lied-lesungen, das geskriptete Modell
// prüft das Backend in tests/archive/backend/LiedLesenApiTests.cs); die Tests
// beweisen die asynchrone Zustellung, Vorrang der Handarbeit und Abbruch.

const sitzungsId = "00000000-0000-0000-0000-0000000000bb";

type Lesung = {
  entwurfId: string;
  anfrageId: number;
  felder: { feld: string; wert: string; sicher: boolean }[];
};

const pdf = (name: string) => ({
  name,
  mimeType: "application/pdf",
  buffer: Buffer.from("%PDF-1.4"),
});
const mp3 = (name: string) => ({
  name,
  mimeType: "audio/mpeg",
  buffer: Buffer.from("ID3"),
});

async function mockAnlegen(page: Page) {
  const state: { angelegt: Record<string, unknown> | null } = {
    angelegt: null,
  };
  await mockSitzung(page, editorMe);
  await page.route("**/api/lieder", (route) => {
    if (route.request().method() !== "POST") {
      return route.fulfill(json(liste([mondEintrag])));
    }
    state.angelegt = route.request().postDataJSON();
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
  await page.route(`**/api/lieder/${mondId}/dateien/upload-session`, (route) =>
    route.fulfill(
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
    ),
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
  return state;
}

/** Die Anfrage des Lesens: Entwurf und Anfrage stehen im Formular, die Antwort kommt, wann der Test will. */
async function mockLesen(
  page: Page,
  antwort: (anfrage: { entwurfId: string; anfrageId: number }) => {
    status: number;
    body: unknown;
  },
  freigabe?: Promise<void>,
) {
  const anfragen: { entwurfId: string; anfrageId: number }[] = [];
  await page.route("**/api/lied-lesungen", async (route) => {
    const text = route.request().postDataBuffer()?.toString("latin1") ?? "";
    const wert = (name: string) =>
      new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)`).exec(text)?.[1] ?? "";
    const anfrage = {
      entwurfId: wert("entwurfId"),
      anfrageId: Number(wert("anfrageId")),
    };
    anfragen.push(anfrage);
    await freigabe;
    const { status, body } = antwort(anfrage);
    return route.fulfill(json(body, status));
  });
  return anfragen;
}

const ave = (a: { entwurfId: string; anfrageId: number }): Lesung => ({
  ...a,
  felder: [
    { feld: "title", wert: "Ave Maria", sicher: true },
    { feld: "composer", wert: "Anton Bruckner", sicher: true },
    { feld: "arranger", wert: "Franz Bearbeiter", sicher: true },
    { feld: "lyricist", wert: "Anna Beispiel", sicher: true },
    { feld: "voiceConfiguration", wert: "Männerchor", sicher: true },
    { feld: "musicalKey", wert: "G-Dur", sicher: false },
    { feld: "language", wert: "Latein", sicher: true },
    { feld: "lyrics", wert: "Ave Maria\ngratia plena", sicher: true },
  ],
});

test("eine Text-PDF und drei Übe-Dateien füllen das Formular, Speichern ist ein Klick", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const state = await mockAnlegen(page);
  await mockLesen(page, (a) => ({ status: 200, body: ave(a) }));

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page
    .getByLabel("Dateien auswählen")
    .setInputFiles([
      mp3("Ave_Maria_T1.mp3"),
      mp3("Ave_Maria_T2.mp3"),
      mp3("Ave_Maria_B1.mp3"),
      pdf("Ave_Maria.pdf"),
    ]);

  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveValue(
    "Anton Bruckner",
  );
  await expect(page.getByRole("textbox", { name: "Bearbeiter" })).toHaveValue(
    "Franz Bearbeiter",
  );
  await expect(page.getByRole("textbox", { name: "Texter" })).toHaveValue(
    "Anna Beispiel",
  );
  await expect(page.getByRole("textbox", { name: "Besetzung" })).toHaveValue(
    "Männerchor",
  );
  await expect(page.getByRole("textbox", { name: "Tonart" })).toHaveValue(
    "G-Dur",
  );
  await expect(page.getByRole("textbox", { name: "Sprache" })).toHaveValue(
    "Latein",
  );
  await expect(page.getByRole("textbox", { name: "Liedtext" })).toHaveValue(
    "Ave Maria\ngratia plena",
  );
  // Die unsichere Tonart ist als solche markiert, die anderen als Vorschlag.
  await expect(page.locator('[data-vorschlag="unsicher"]')).toHaveCount(1);
  await expect(page.locator('[data-vorschlag="sicher"]')).toHaveCount(8 - 1);

  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  // Vorschläge sind gewöhnliche Daten; nichts über ihre Herkunft wird gesendet.
  expect(state.angelegt).toEqual({
    title: "Ave Maria",
    composer: "Anton Bruckner",
    arranger: "Franz Bearbeiter",
    lyricist: "Anna Beispiel",
    voiceConfiguration: "Männerchor",
    musicalKey: "G-Dur",
    language: "Latein",
    lyrics: "Ave Maria\ngratia plena",
    occasion: "",
  });
  expect(errors).toEqual([]);
});

test("eine späte Antwort ändert nichts, was die Redaktion angefasst oder geleert hat", async ({
  page,
}) => {
  await mockAnlegen(page);
  let frei: () => void = () => {};
  const warten = new Promise<void>((weiter) => {
    frei = weiter;
  });
  await mockLesen(page, (a) => ({ status: 200, body: ave(a) }), warten);

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles([pdf("Scan.pdf")]);
  await expect(page.getByText("Wird gelesen …")).toBeVisible();

  await page.getByRole("textbox", { name: "Komponist" }).fill("Eigener Name");
  await page.getByRole("textbox", { name: "Texter" }).fill("Leer");
  await page.getByRole("textbox", { name: "Texter" }).fill("");
  frei();

  await expect(page.getByRole("textbox", { name: "Tonart" })).toHaveValue(
    "G-Dur",
  );
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveValue(
    "Eigener Name",
  );
  await expect(page.getByRole("textbox", { name: "Texter" })).toHaveValue("");
});

test("Speichern bricht ein laufendes Lesen ab und das Lied entsteht ohne dessen Vorschläge", async ({
  page,
}) => {
  const state = await mockAnlegen(page);
  await mockLesen(
    page,
    (a) => ({ status: 200, body: ave(a) }),
    new Promise<void>(() => {}),
  );
  const abgebrochen = page.waitForEvent("requestfailed", (r) =>
    r.url().includes("/api/lied-lesungen"),
  );

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles([pdf("Mond.pdf")]);
  await expect(page.getByText("Wird gelesen …")).toBeVisible();
  await page.getByRole("button", { name: "Speichern" }).click();

  await abgebrochen;
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  expect(state.angelegt).toMatchObject({ title: "Mond", composer: "" });
});

test("ein gescheitertes Lesen lässt sich wiederholen, die neue Anfrage hat eine neue Nummer", async ({
  page,
}) => {
  await mockAnlegen(page);
  const anfragen = await mockLesen(page, (a) =>
    a.anfrageId === 1
      ? { status: 502, body: { title: "Das Lesen ist gescheitert." } }
      : { status: 200, body: ave(a) },
  );

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles([pdf("Mond.pdf")]);
  await expect(page.locator("p[role=alert]")).toContainText(
    "Das Lesen ist gescheitert.",
  );
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveValue(
    "",
  );

  await page.getByRole("button", { name: "Mond.pdf erneut lesen" }).click();
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveValue(
    "Anton Bruckner",
  );
  expect(anfragen.map((a) => a.anfrageId)).toEqual([1, 2]);
  expect(new Set(anfragen.map((a) => a.entwurfId)).size).toBe(1);
});

test("ist das Budget erreicht, bleibt das Formular von Hand ausfüllbar", async ({
  page,
}) => {
  const state = await mockAnlegen(page);
  await mockLesen(page, () => ({
    status: 429,
    body: { title: "Das Monatsbudget der KI ist erreicht." },
  }));

  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page.getByLabel("Dateien auswählen").setInputFiles([pdf("Mond.pdf")]);
  await expect(
    page.getByText("Das Monatsbudget der KI ist erreicht."),
  ).toBeVisible();
  // Kein Fehleralarm und keine Wiederholung: die KI steht nicht zur Verfügung.
  await expect(page.locator("p[role=alert]")).toHaveCount(0);

  await page.getByRole("textbox", { name: "Komponist" }).fill("Von Hand");
  await page.getByRole("button", { name: "Speichern" }).click();
  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  expect(state.angelegt).toMatchObject({ title: "Mond", composer: "Von Hand" });
});
