import { expect, type Page, test } from "@playwright/test";
import {
  datei,
  editorMe,
  json,
  liedDetail,
  memberMe,
  mockSitzung,
  mondId,
  problem,
} from "./lieder-mock";

// #166: Lied-Seite des flachen Liedes — Noten lesen (auch am Handy), Noten
// hochladen (Partitur und Stimmen) und unterbrochene Übertragungen fortsetzen.

const partiturId = "00000000-0000-0000-0000-00000000b001";
const tenorId = "00000000-0000-0000-0000-00000000b002";
const sitzungsId = "00000000-0000-0000-0000-0000000000aa";
const uploadUrl = "https://speicher.test/ubertragung?sig=upload-1";
const uploadMuster = /speicher\.test\/ubertragung/;
const viewUrl = "https://speicher.test/ansicht?sig=view-1";
const downloadUrl = "https://speicher.test/laden?sig=download-1";

const partitur = datei(partiturId, {
  isPartitur: true,
  stimme: null,
  fileName: "partitur.pdf",
});
const tenor = datei(tenorId);

function sitzungsAntwort(blockBytes = 5242880) {
  return {
    uploadSessionId: sitzungsId,
    blobName: null,
    uploadUrl,
    expiresAt: "2026-09-19T12:15:00.000Z",
    maxBytes: 5242880,
    blockBytes,
  };
}

async function dateiWaehlen(page: Page, name: string, inhalt: string) {
  const wahl = page.waitForEvent("filechooser");
  await page.getByLabel("Noten auswählen").click();
  const chooser = await wahl;
  await chooser.setFiles({
    name,
    mimeType: "application/pdf",
    buffer: Buffer.from(inhalt),
  });
}

test("Mitglied liest Partitur und Stimmen-Noten", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([partitur, tenor]))),
  );
  const zugriffe: string[] = [];
  await page.route("**/api/dateien/*/access", (route) => {
    const id = route.request().url().split("/").at(-2) ?? "";
    zugriffe.push(id);
    return route.fulfill(
      json({
        dateiId: id,
        kind: "noten",
        contentType: "application/pdf",
        sizeBytes: 400000,
        fileName: "tenor1.pdf",
        createdAt: "2026-09-19T12:00:00.000Z",
        viewUrl,
        downloadUrl,
        expiresAt: "2026-09-19T12:15:00.000Z",
      }),
    );
  });

  await page.goto(`/lied/?id=${mondId}`);
  await expect(
    page.getByRole("heading", { name: "Der Mond ist aufgegangen", level: 1 }),
  ).toBeVisible();
  await expect(page.getByText("Besetzung: TTBB")).toBeVisible();
  await expect(page.getByText("Tonart: F-Dur")).toBeVisible();
  const tenorEintrag = page.getByRole("article", { name: "Noten · Tenor 1" });
  await expect(
    page.getByRole("article", { name: "Noten · Partitur" }),
  ).toBeVisible();
  await expect(tenorEintrag.getByText("Stimme · 0,4 MB · PDF")).toBeVisible();
  // Mitglieder sehen weder Upload noch Bearbeiten.
  await expect(
    page.getByRole("heading", { name: "Noten hochladen" }),
  ).toHaveCount(0);
  await expect(page.getByText("Lied bearbeiten")).toHaveCount(0);

  await tenorEintrag.getByRole("button", { name: "Noten anzeigen" }).click();
  const rahmen = tenorEintrag.locator("iframe[title^='Noten (PDF)']");
  await expect(rahmen).toBeVisible();
  await expect(rahmen).toHaveAttribute("src", viewUrl);
  await expect(
    tenorEintrag.getByRole("link", { name: "Herunterladen" }),
  ).toHaveAttribute("href", downloadUrl);
  expect(zugriffe).toEqual([tenorId]);

  // Auf dem Handy bleibt der Rahmen innerhalb der Seitenbreite.
  const breite = page.viewportSize()?.width ?? 0;
  const box = await rahmen.boundingBox();
  expect(box?.width ?? 0).toBeLessThanOrEqual(breite);
  expect(errors).toEqual([]);
});

test("Fehlt die Datei im Speicher, erklärt die Seite es", async ({ page }) => {
  await mockSitzung(page, memberMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([tenor]))),
  );
  await page.route("**/api/dateien/*/access", (route) =>
    route.fulfill(problem("Datei nicht gefunden.", 404)),
  );

  await page.goto(`/lied/?id=${mondId}`);
  await page.getByRole("button", { name: "Noten anzeigen" }).click();
  await expect(
    page.getByText("Für diese Noten liegt keine abrufbare Datei vor."),
  ).toBeVisible();
});

test("Ein Lied ohne Dateien sagt das und ein unbekanntes Lied nicht gefunden", async ({
  page,
}) => {
  await mockSitzung(page, memberMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([]))),
  );
  await page.route("**/api/lieder/unbekannt", (route) =>
    route.fulfill(problem("Das Lied wurde nicht gefunden.", 404)),
  );

  await page.goto(`/lied/?id=${mondId}`);
  await expect(page.getByText("Noch keine Dateien hochgeladen.")).toBeVisible();
  await page.goto("/lied/?id=unbekannt");
  await expect(
    page.getByRole("heading", { name: "Lied nicht gefunden" }),
  ).toBeVisible();
});

test("Redaktion lädt Partitur und zwei Stimmen hoch", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  let dateien: unknown[] = [];
  const sitzungen: unknown[] = [];
  const abschluesse: unknown[] = [];
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail(dateien))),
  );
  await page.route(
    `**/api/lieder/${mondId}/dateien/upload-session`,
    (route) => {
      expect(route.request().headers()["x-csrf-token"]).toBe("test");
      sitzungen.push(route.request().postDataJSON());
      return route.fulfill(json(sitzungsAntwort(), 201));
    },
  );
  await page.route(uploadMuster, (route) => route.fulfill(json({}, 201)));
  const neueDateien = [
    partitur,
    tenor,
    datei("00000000-0000-0000-0000-00000000b003", {
      stimme: "Bass 2",
      fileName: "bass2.pdf",
    }),
  ];
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    abschluesse.push(route.request().postDataJSON());
    const neu = neueDateien[abschluesse.length - 1];
    dateien = [...dateien, neu];
    return route.fulfill(json({ datei: neu }));
  });

  await page.goto(`/lied/?id=${mondId}`);
  await expect(
    page.getByRole("heading", { name: "Noten hochladen" }),
  ).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await page.getByLabel("Noten auswählen").click();
  const chooser = await wahl;
  await chooser.setFiles([
    {
      name: "partitur.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4 a"),
    },
    {
      name: "mond_tenor_1.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4 b"),
    },
    {
      name: "satz.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4 c"),
    },
  ]);

  // Die Stimme wird aus dem Dateinamen erkannt; Vorschläge folgen der Besetzung.
  await expect(page.getByLabel("Stimme für mond_tenor_1.pdf")).toHaveValue(
    "Tenor 1",
  );
  const vorschlaege = await page
    .locator("#datei-hochladen-stimmen option")
    .evaluateAll((optionen) =>
      optionen.map((o) => (o as HTMLOptionElement).value),
    );
  expect(vorschlaege).toEqual([
    "Tenor 1",
    "Tenor 2",
    "Bass 1",
    "Bass 2",
    "Alle",
  ]);

  await page.getByRole("button", { name: "Hochladen" }).click();
  // Ohne Stimme und ohne Partitur wird nichts übertragen; „partitur.pdf“
  // ist schon als Partitur vorgemerkt.
  await expect(
    page.getByRole("checkbox", { name: "Partitur" }).first(),
  ).toBeChecked();
  await expect(
    page.getByText(
      "Bitte für „satz.pdf“ eine Stimme angeben oder Partitur wählen.",
    ),
  ).toBeVisible();
  expect(sitzungen).toEqual([]);

  await page.getByLabel("Stimme für satz.pdf").fill("Bass 2");
  await page.getByRole("button", { name: "Hochladen" }).click();

  await expect(page.getByText("3 von 3 Dateien gespeichert.")).toBeVisible();
  expect(sitzungen).toEqual([
    {
      kind: "noten",
      isPartitur: true,
      stimme: null,
      description: null,
      contentType: "application/pdf",
      sizeBytes: 10,
      fileName: "partitur.pdf",
    },
    {
      kind: "noten",
      isPartitur: false,
      stimme: "Tenor 1",
      description: null,
      contentType: "application/pdf",
      sizeBytes: 10,
      fileName: "mond_tenor_1.pdf",
    },
    {
      kind: "noten",
      isPartitur: false,
      stimme: "Bass 2",
      description: null,
      contentType: "application/pdf",
      sizeBytes: 10,
      fileName: "satz.pdf",
    },
  ]);
  expect(abschluesse).toHaveLength(3);
  await expect(
    page.getByRole("article", { name: "Noten · Partitur" }),
  ).toBeVisible();
  await expect(
    page.getByRole("article", { name: "Noten · Tenor 1" }),
  ).toBeVisible();
  await expect(
    page.getByRole("article", { name: "Noten · Bass 2" }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

test("Unterbrochener Upload wird nach dem Neuladen fortgesetzt", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const inhalt = "%PDF-1.4 blockweise1";
  const schluessel = `arc-upload-lied-${mondId}-noten-tenor 1`;
  let phase = 1;
  let erlaubt = false;
  const blockPuts: { phase: number; index: number }[] = [];
  let blocklistenAbfragen = 0;
  let verbindungsXml = "";
  let verlaengerungen = 0;
  let abschluesse = 0;
  let dateien: unknown[] = [];

  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail(dateien))),
  );
  await page.route(`**/api/lieder/${mondId}/dateien/upload-session`, (route) =>
    route.fulfill(json(sitzungsAntwort(8), 201)),
  );
  await page.route(uploadMuster, async (route) => {
    const anfrage = route.request();
    if (anfrage.method() === "GET") {
      blocklistenAbfragen += 1;
      return route.fulfill({
        status: 200,
        contentType: "application/xml",
        body: `<BlockList><CommittedBlocks><Block><Name>MDAwMDAw</Name><Size>8</Size></Block></CommittedBlocks><UncommittedBlocks /></BlockList>`,
      });
    }
    if (anfrage.url().includes("comp=blocklist")) {
      verbindungsXml = anfrage.postData() ?? "";
      return route.fulfill(json({}, 201));
    }
    const index = Number(
      atob(new URL(anfrage.url()).searchParams.get("blockid") ?? ""),
    );
    blockPuts.push({ phase, index });
    if (index !== 0 && !erlaubt) {
      return route.fulfill(problem("Übertragung unterbrochen.", 500));
    }
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/renew", (route) => {
    verlaengerungen += 1;
    return route.fulfill(json(sitzungsAntwort(8)));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    abschluesse += 1;
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: inhalt.length,
      fileName: "noten.pdf",
    });
    dateien = [tenor];
    return route.fulfill(json({ datei: tenor }));
  });

  await page.goto(`/lied/?id=${mondId}`);
  await dateiWaehlen(page, "noten.pdf", inhalt);
  await page.getByLabel("Stimme für noten.pdf").fill("Tenor 1");
  await page.getByRole("button", { name: "Hochladen" }).click();
  await expect(page.getByText("noten.pdf · 20 B · gescheitert")).toBeVisible();
  expect(blockPuts).toEqual([
    { phase: 1, index: 0 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
  ]);
  const eintrag = JSON.parse(
    (await page.evaluate((k) => window.localStorage.getItem(k), schluessel)) ??
      "null",
  ) as {
    uploadSessionId: string;
    fileName: string;
    sizeBytes: number;
    lastModified: number;
    blockBytes: number;
  };
  expect(eintrag).toMatchObject({
    uploadSessionId: sitzungsId,
    fileName: "noten.pdf",
    sizeBytes: inhalt.length,
    blockBytes: 8,
  });

  phase = 2;
  erlaubt = true;
  await page.reload();
  await expect(
    page.getByText("Unterbrochene Übertragung: noten.pdf"),
  ).toBeVisible();
  // Dieselbe Datei erneut gewählt, setzt dort fort, wo es aufhörte.
  await page.evaluate(
    ({ name, inhalt, lastModified }) => {
      const datei = new File([inhalt], name, {
        type: "application/pdf",
        lastModified,
      });
      const eingabe =
        document.querySelector<HTMLInputElement>("input[type='file']");
      if (!eingabe) throw new Error("Eingabe fehlt");
      const transport = new DataTransfer();
      transport.items.add(datei);
      eingabe.files = transport.files;
      eingabe.dispatchEvent(new Event("change", { bubbles: true }));
    },
    { name: "noten.pdf", inhalt, lastModified: eintrag.lastModified },
  );
  await page.getByLabel("Stimme für noten.pdf").fill("Tenor 1");
  await page.getByRole("button", { name: "Hochladen" }).click();

  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();
  expect(verlaengerungen).toBe(1);
  expect(blocklistenAbfragen).toBe(1);
  expect(blockPuts).toEqual([
    { phase: 1, index: 0 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
    { phase: 2, index: 1 },
    { phase: 2, index: 2 },
  ]);
  expect(verbindungsXml).toBe(
    "<BlockList><Latest>MDAwMDAw</Latest><Latest>MDAwMDAx</Latest><Latest>MDAwMDAy</Latest></BlockList>",
  );
  expect(abschluesse).toBe(1);
  expect(
    await page.evaluate((k) => window.localStorage.getItem(k), schluessel),
  ).toBeNull();
  await expect(
    page.getByRole("article", { name: "Noten · Tenor 1" }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});

test("Eine abgelehnte Datei zeigt die Meldung des Servers", async ({
  page,
}) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([]))),
  );
  await page.route(`**/api/lieder/${mondId}/dateien/upload-session`, (route) =>
    route.fulfill(json(sitzungsAntwort(), 201)),
  );
  await page.route(uploadMuster, (route) => route.fulfill(json({}, 201)));
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(problem("Die Datei ist kein gültiges PDF.", 422)),
  );

  await page.goto(`/lied/?id=${mondId}`);
  await dateiWaehlen(page, "kaputt.pdf", "kein pdf");
  await page.getByLabel("Stimme für kaputt.pdf").fill("Bass 1");
  await page.getByRole("button", { name: "Hochladen" }).click();
  await expect(
    page.getByText("Die Datei ist kein gültiges PDF."),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Erneut übertragen" }),
  ).toBeVisible();
});

test("Redaktion ändert Angaben des Liedes", async ({ page }) => {
  await mockSitzung(page, editorMe);
  await page.route(`**/api/lieder/${mondId}`, async (route) => {
    if (route.request().method() === "PATCH") {
      expect(route.request().postDataJSON()).toMatchObject({
        occasion: "Weihnachten",
        rowVersion: 3,
      });
      return route.fulfill(
        json(liedDetail([], { occasion: "Weihnachten", rowVersion: 4 })),
      );
    }
    return route.fulfill(json(liedDetail([])));
  });

  await page.goto(`/lied/?id=${mondId}`);
  await page.getByText("Lied bearbeiten", { exact: true }).click();
  await page.getByLabel("Anlass (optional)").fill("Weihnachten");
  await page.getByRole("button", { name: "Änderungen speichern" }).click();
  await expect(page.getByText("Änderungen gespeichert.")).toBeVisible();
  await expect(page.getByText("Anlass: Weihnachten")).toBeVisible();
});
