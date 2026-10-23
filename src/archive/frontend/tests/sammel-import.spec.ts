import * as nodeFs from "node:fs";
import * as nodeOs from "node:os";
import * as nodePath from "node:path";
import { expect, type Page, test } from "@playwright/test";
import { gruppiereImport } from "../lib/sammel-import";
import type { EntwurfSpeicher } from "./lieder-mock";
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

// #143 Sammel-Import: ein Ordner (Drive-Export) wird zu Vorschlägen — ein
// Unterordner je Lied —, die Redaktion speichert jeden mit denselben Rechten
// wie bei der Ablage eines einzelnen Liedes; noch einmal fallen ist keine
// Verdoppelung, der laufende Stand wird fortgesetzt.

const sitzungsId = "00000000-0000-0000-0000-0000000000bb";

/** Ein kleiner Ordner im Stil der Inventur (#98): zwei Unternehmens-Lieder. */
async function fallsammlung(): Promise<string> {
  const wurzel = nodeFs.mkdtempSync(nodePath.join(nodeOs.tmpdir(), "sammel-"));
  const erster = nodePath.join(wurzel, "Sammlung", "Lied Eins");
  nodeFs.mkdirSync(erster, { recursive: true });
  nodeFs.writeFileSync(
    nodePath.join(erster, "Lied Eins Partitur.pdf"),
    "%PDF-1.4",
  );
  nodeFs.writeFileSync(
    nodePath.join(erster, "Lied Eins Bass 1.pdf"),
    "%PDF-1.4",
  );
  const zweiter = nodePath.join(wurzel, "Sammlung", "Lied Zwei");
  nodeFs.mkdirSync(zweiter, { recursive: true });
  nodeFs.writeFileSync(nodePath.join(zweiter, "Lied Zwei.mp3"), "ID3");
  return wurzel;
}

async function mockUmgebung(page: Page) {
  await mockSitzung(page, editorMe);
  const entwuerfe = await mockEntwuerfe(page);
  await page.route("**/api/admin/members", (route) =>
    route.fulfill(json({ members: [] })),
  );
  await page.route("**/api/lieder", (route) => {
    if (route.request().method() !== "POST")
      return route.fulfill(json(liste([mondEintrag])));
    const body = route.request().postDataJSON() as { title?: string };
    const titel = body.title ?? "";
    const neueId =
      titel === "Lied Zwei" ? "00000000-0000-0000-0000-00000000a003" : mondId;
    return route.fulfill(
      json(
        {
          lied: {
            ...mondEintrag,
            id: neueId,
            title: titel,
            lyrics: null,
            composer: null,
            lyricist: null,
            musicalKey: null,
            occasion: null,
            voiceConfiguration: null,
            createdAt: "2026-10-20T12:00:00.000Z",
            updatedAt: "2026-10-20T12:00:00.000Z",
            rowVersion: 1,
            dateien: [],
          },
        },
        201,
      ),
    );
  });
  await page.route("**/api/lieder/*/dateien/upload-session", (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: sitzungsId,
          blobName: null,
          uploadUrl: "https://speicher.test/ubertragung?sig=1",
          expiresAt: "2026-10-20T12:15:00.000Z",
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
  await page.route("**/api/lieder/*", (route) =>
    route.fulfill(json(liedDetail([]))),
  );
  // Das Lesen kommt sofort und ohne Vorschläge zurück.
  await page.route("**/api/lied-lesungen", (route) => {
    const text = route.request().postDataBuffer()?.toString("latin1") ?? "";
    const wert = (name: string) =>
      new RegExp(`name="${name}"\\r\\n\\r\\n([^\\r]*)`).exec(text)?.[1] ?? "";
    return route.fulfill(
      json({
        entwurfId: wert("entwurfId"),
        anfrageId: Number(wert("anfrageId")),
        felder: [],
      }),
    );
  });
  return entwuerfe;
}

async function oeffneSammelkarte(page: Page) {
  await page.getByRole("heading", { name: "Ordner importieren" }).click();
}

async function ladeOrdner(page: Page, wurzel: string, offen = false) {
  if (offen) await oeffneSammelkarte(page);
  await page
    .getByLabel("Ordner wählen")
    .setInputFiles(nodePath.join(wurzel, "Sammlung"));
}

function vorschlaege(page: Page) {
  return page.getByRole("list", { name: "Vorschläge" }).getByRole("listitem");
}

test("Ein Ordner wird zu Vorschlägen, ein Unterordner je Lied", async ({
  page,
}) => {
  const fehler: string[] = [];
  page.on("pageerror", (error) => fehler.push(error.message));
  await mockUmgebung(page);

  const wurzel = await fallsammlung();
  await page.goto("/lieder/");
  await ladeOrdner(page, wurzel, true);
  const eintraege = vorschlaege(page);
  await expect(eintraege).toHaveCount(2);
  await expect(eintraege.first()).toContainText("Lied Eins");
  await expect(eintraege.nth(1)).toContainText("Lied Zwei");
  expect(fehler).toEqual([]);
});

test("Ein Vorschlag speichert das Lied mit seinen Dateien", async ({
  page,
}) => {
  const angelegt: { title?: string }[] = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/lieder"))
      angelegt.push(request.postDataJSON() as { title?: string });
  });
  await mockUmgebung(page);
  const wurzel = await fallsammlung();
  await page.goto("/lieder/");
  await ladeOrdner(page, wurzel, true);
  const eintraege = vorschlaege(page);
  await eintraege.first().getByRole("button", { name: "Bearbeiten" }).click();
  await expect(page.getByLabel("Titel")).toHaveValue("Lied Eins");
  await eintraege.first().getByRole("button", { name: "Speichern" }).click();
  await expect
    .poll(async () => eintraege.first().textContent(), { timeout: 15_000 })
    .toContain("Gespeichert");
  expect(angelegt).toHaveLength(1);
  expect(angelegt[0]?.title).toBe("Lied Eins");
});

test("Unterbrochene Ansätze führen fort, ohne zu verdoppeln", async ({
  page,
}) => {
  const angelegt: { title?: string }[] = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/lieder"))
      angelegt.push(request.postDataJSON() as { title?: string });
  });

  const wurzel = await fallsammlung();
  // Der Ordner, wie der Import ihn ordnet; die id des ersten Vorschlags ist
  // vom Inhalt bestimmt, also bastelt der Test den unterbrochenen Stand dort.
  const dateien = nodeFs
    .readdirSync(nodePath.join(wurzel, "Sammlung", "Lied Eins"))
    .map((name) => ({
      name,
      pfad: `Sammlung/Lied Eins/${name}`.replace("Sammlung/", ""),
      sizeBytes: "%PDF-1.4".length,
    }));
  const einplan = await gruppiereImport(dateien);
  const entwurfId = einplan.gruppen[0].entwurfId;
  const speicher = await mockUmgebung(page);
  speicher.entwuerfe.set(entwurfId, {
    entwurfId,
    lied: {
      title: "Lied Eins",
      composer: "Inzwischen eingetragener Komponist",
      arranger: "",
      lyricist: "",
      voiceConfiguration: "",
      musicalKey: "",
      language: "",
      lyrics: "",
    },
    bearbeitet: [],
    vorschlaege: {},
    dateien: [
      {
        id: "00000000-0000-0000-0000-00000000d001",
        name: "Lied Eins Partitur.pdf",
        sizeBytes: "%PDF-1.4".length,
        art: "partitur",
        stimme: "",
        artBearbeitet: false,
        stimmeBearbeitet: false,
        lesen: { status: "keine", anfrageId: 0, meldung: "" },
      },
    ],
  } as unknown as Parameters<EntwurfSpeicher["entwuerfe"]["set"]>[1]);

  await page.goto("/lieder/");
  await ladeOrdner(page, wurzel, true);
  const eintraege = vorschlaege(page);
  await expect(eintraege.first()).toBeVisible();
  await eintraege.first().getByRole("button", { name: "Fortsetzen" }).click();
  await expect(page.getByLabel("Komponist")).toHaveValue(
    "Inzwischen eingetragener Komponist",
  );
  // Nicht angefasst: nichts wurde gespeichert, noch weniger angelegt.
  expect(angelegt).toHaveLength(0);
});

test("Noch einmal fallen und speichern verdoppelt kein Lied", async ({
  page,
}) => {
  const angelegt: { title?: string }[] = [];
  page.on("request", (request) => {
    if (request.method() === "POST" && request.url().endsWith("/api/lieder"))
      angelegt.push(request.postDataJSON() as { title?: string });
  });
  await mockUmgebung(page);
  const wurzel = await fallsammlung();
  await page.goto("/lieder/");
  await ladeOrdner(page, wurzel, true);
  const eintraege = vorschlaege(page);
  await eintraege.first().getByRole("button", { name: "Bearbeiten" }).click();
  await eintraege.first().getByRole("button", { name: "Speichern" }).click();
  await expect
    .poll(async () => eintraege.first().textContent(), { timeout: 15_000 })
    .toContain("Gespeichert");
  // Noch einmal fallen: derselbe Ordner läuft fort und legt kein zweites Lied an.
  await ladeOrdner(page, wurzel);
  await expect(eintraege).toHaveCount(2);
  await expect.poll(async () => angelegt.length, { timeout: 15_000 }).toBe(1);
});
