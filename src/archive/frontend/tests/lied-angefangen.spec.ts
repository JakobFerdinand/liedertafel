import { expect, type Page, test } from "@playwright/test";
import {
  datei,
  type EntwurfSpeicher,
  editorMe,
  json,
  type Lesung,
  liedDetail,
  liste,
  mockEntwuerfe,
  mockSitzung,
  mondEintrag,
  mondId,
} from "./lieder-mock";

// #178: "Angefangen". Das Backend hält den Entwurf und die Lesungen; der
// Browser stellt daraus den Zustand wieder her. Der Server wird hier
// nachgebildet (mockEntwuerfe), die Logik dahinter prüfen die xUnit-Tests.

const sitzungsId = "00000000-0000-0000-0000-0000000000bb";

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

const ave: Lesung["felder"] = [
  { feld: "title", wert: "Ave Maria", sicher: true },
  { feld: "composer", wert: "Anton Bruckner", sicher: true },
  { feld: "lyricist", wert: "Anna Beispiel", sicher: true },
  { feld: "musicalKey", wert: "G-Dur", sicher: false },
  { feld: "lyrics", wert: "Ave Maria\ngratia plena", sicher: true },
];

type Anfrage = { entwurfId: string; anfrageId: number; dateiId: string };

async function mockUmgebung(page: Page, gehalten: Promise<void>) {
  const state: { angelegt: Record<string, unknown> | null } = {
    angelegt: null,
  };
  await mockSitzung(page, editorMe);
  const speicher = await mockEntwuerfe(page);
  await page.route("**/api/admin/members", (route) =>
    route.fulfill(json({ members: [] })),
  );
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
  // Das Lesen: der Browser fragt, die Antwort kommt nie (der Tab geht zu)
  // oder erst, wenn der Test sie freigibt.
  const anfragen: Anfrage[] = [];
  await page.route("**/api/lied-lesungen", async (route) => {
    const text = route.request().postDataBuffer()?.toString("latin1") ?? "";
    const wert = (name: string) =>
      new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)`).exec(text)?.[1] ?? "";
    const anfrage = {
      entwurfId: wert("entwurfId"),
      anfrageId: Number(wert("anfrageId")),
      dateiId: wert("dateiId"),
    };
    anfragen.push(anfrage);
    await gehalten;
    return route.fulfill(
      json({
        entwurfId: anfrage.entwurfId,
        anfrageId: anfrage.anfrageId,
        felder: ave,
      }),
    );
  });
  return { state, speicher, anfragen };
}

async function ablegen(page: Page) {
  await page.goto("/lieder/");
  await page.getByText("Neues Lied", { exact: true }).click();
  await page
    .getByLabel("Dateien auswählen")
    .setInputFiles([pdf("Ave_Maria.pdf"), mp3("Ave_Maria_T1.mp3")]);
  await expect(page.getByText("Wird gelesen …")).toBeVisible();
}

function lesungFertig(
  speicher: EntwurfSpeicher,
  anfrage: Anfrage,
  extra: Partial<Lesung> = {},
) {
  speicher.lesungSetzen(anfrage.entwurfId, anfrage.dateiId, {
    anfrageId: anfrage.anfrageId,
    status: "gelesen",
    meldung: "",
    felder: ave,
    ...extra,
  });
}

test("Tab mitten im Lesen verlassen, über Angefangen fortsetzen: Formular samt Ergebnis, Bearbeitungen und Dateien", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const { state, speicher, anfragen } = await mockUmgebung(
    page,
    new Promise<void>(() => {}),
  );

  await ablegen(page);
  await page.getByRole("textbox", { name: "Komponist" }).fill("Eigener Name");
  await page.getByRole("textbox", { name: "Texter" }).fill("Leer");
  await page.getByRole("textbox", { name: "Texter" }).fill("");
  const anfrage = anfragen[0];
  await expect
    .poll(() => speicher.entwuerfe.get(anfrage.entwurfId)?.lied.composer)
    .toBe("Eigener Name");

  // Der Server liest zu Ende, während niemand mehr zusieht.
  lesungFertig(speicher, anfrage);
  await page.goto("/verwaltung/");
  await expect(page.getByRole("heading", { name: "Angefangen" })).toBeVisible();
  await page.getByRole("link", { name: "Ave Maria fortsetzen" }).click();

  // Wiederhergestellt aus dem Backend: Ergebnis, Handarbeit (auch das Leeren) und Dateien.
  await expect(page.getByRole("textbox", { name: "Tonart" })).toHaveValue(
    "G-Dur",
  );
  await expect(page.getByRole("textbox", { name: "Liedtext" })).toHaveValue(
    "Ave Maria\ngratia plena",
  );
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveValue(
    "Eigener Name",
  );
  await expect(page.getByRole("textbox", { name: "Texter" })).toHaveValue("");
  await expect(page.locator('[data-vorschlag="unsicher"]')).toHaveCount(1);
  await expect(
    page.locator(".lied-ablegen-name", { hasText: "Ave_Maria_T1.mp3" }),
  ).toBeVisible();
  await expect(page.getByLabel("Stimme von Ave_Maria_T1.mp3")).toHaveValue(
    "Tenor 1",
  );

  // Die Bytes liegen nur im Browser: gespeichert wird, wenn die Dateien wieder da sind.
  await expect(page.getByText(/liegt nicht mehr im Browser/)).toHaveCount(2);
  await expect(page.getByRole("button", { name: "Speichern" })).toBeDisabled();
  await page
    .getByLabel("Dateien auswählen")
    .setInputFiles([pdf("Ave_Maria.pdf"), mp3("Ave_Maria_T1.mp3")]);
  await expect(page.getByText(/liegt nicht mehr im Browser/)).toHaveCount(0);
  await page.getByRole("button", { name: "Speichern" }).click();

  await expect(page).toHaveURL(new RegExp(`/lied/\\?id=${mondId}`));
  expect(state.angelegt).toMatchObject({
    title: "Ave Maria",
    composer: "Eigener Name",
    lyricist: "",
    musicalKey: "G-Dur",
  });
  // Das Lied gibt es, der angefangene Eintrag ist erledigt.
  await expect.poll(() => speicher.verworfen).toContain(anfrage.entwurfId);
  // Dieselben Dateien lesen sich nicht noch einmal.
  expect(anfragen).toHaveLength(1);
  expect(errors).toEqual([]);
});

test("Ein Lesen, das beim Fortsetzen noch läuft, kommt per Nachfrage an", async ({
  page,
}) => {
  const { speicher, anfragen } = await mockUmgebung(
    page,
    new Promise<void>(() => {}),
  );
  await ablegen(page);
  await page.getByRole("textbox", { name: "Komponist" }).fill("Eigener Name");
  const anfrage = anfragen[0];
  await expect
    .poll(() => speicher.entwuerfe.get(anfrage.entwurfId)?.lied.composer)
    .toBe("Eigener Name");
  lesungFertig(speicher, anfrage, { status: "liest", felder: [] });

  await page.goto(`/lieder/?entwurf=${anfrage.entwurfId}`);
  await expect(page.getByText("Wird gelesen …")).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Tonart" })).toHaveValue("");

  lesungFertig(speicher, anfrage);
  await expect(page.getByRole("textbox", { name: "Tonart" })).toHaveValue(
    "G-Dur",
    { timeout: 15_000 },
  );
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveValue(
    "Eigener Name",
  );
  await expect(page.getByText("Gelesen")).toBeVisible();
});

test("Verwerfen aus Angefangen: der Eintrag ist weg und spätere Ergebnisse ändern nichts", async ({
  page,
}) => {
  const { speicher, anfragen } = await mockUmgebung(
    page,
    new Promise<void>(() => {}),
  );
  await ablegen(page);
  const anfrage = anfragen[0];
  await expect
    .poll(() => speicher.entwuerfe.get(anfrage.entwurfId)?.dateien.length)
    .toBe(2);

  await page.goto("/verwaltung/");
  await page.getByRole("button", { name: "Ave Maria verwerfen" }).click();
  await expect(page.getByRole("heading", { name: "Angefangen" })).toHaveCount(
    0,
  );
  expect(speicher.verworfen).toEqual([anfrage.entwurfId]);

  // Das Lesen endet erst jetzt: es gibt nichts mehr, was es ändern könnte.
  lesungFertig(speicher, anfrage);
  await page.goto(`/lieder/?entwurf=${anfrage.entwurfId}`);
  await expect(
    page.getByText("Der angefangene Eintrag wurde nicht gefunden."),
  ).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Titel" })).toHaveCount(0);
});

test("Verwerfen im Formular, während gelesen wird: die späte Antwort ändert nichts", async ({
  page,
}) => {
  let frei: () => void = () => {};
  const gehalten = new Promise<void>((weiter) => {
    frei = weiter;
  });
  const { speicher, anfragen } = await mockUmgebung(page, gehalten);
  await ablegen(page);
  const anfrage = anfragen[0];

  await page.getByRole("button", { name: "Entwurf verwerfen" }).click();
  await expect(page.getByRole("textbox", { name: "Titel" })).toHaveCount(0);
  await expect.poll(() => speicher.verworfen).toEqual([anfrage.entwurfId]);

  frei();
  // Ein frischer, leerer Entwurf: nichts aus dem verworfenen kommt an.
  await expect(
    page.getByText("Dateien hier ablegen oder auswählen"),
  ).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Titel" })).toHaveCount(0);
  await expect(page.getByRole("textbox", { name: "Komponist" })).toHaveCount(0);
});
