import { expect, type Page, test } from "@playwright/test";
import {
  datei,
  editorMe,
  json,
  liedDetail,
  memberMe,
  mockSitzung,
  mondId,
} from "./lieder-mock";

// #167: Übe-Dateien, Aufnahmen und Sonstiges auf der Lied-Seite — Ton und MIDI
// (mit Tempo) abspielen, auch am Handy, und die drei Arten hochladen.

const mp3Id = "00000000-0000-0000-0000-00000000c001";
const midiId = "00000000-0000-0000-0000-00000000c002";
const aufnahmeId = "00000000-0000-0000-0000-00000000c003";
const sonstigesId = "00000000-0000-0000-0000-00000000c004";
const auftrittId = "00000000-0000-0000-0000-00000000e001";
const sitzungsId = "00000000-0000-0000-0000-0000000000bb";
const uploadMuster = /speicher\.test\/ubertragung/;

const mp3 = datei(mp3Id, {
  kind: "uebe",
  stimme: "Tenor 1",
  fileName: "tenor1.mp3",
  contentType: "audio/mpeg",
  sizeBytes: 3145728,
});
const midi = datei(midiId, {
  kind: "uebe",
  stimme: "Alle",
  fileName: "alle.mid",
  contentType: "audio/midi",
  sizeBytes: 600,
});
const aufnahme = datei(aufnahmeId, {
  kind: "aufnahme",
  stimme: null,
  fileName: "konzert.mp3",
  contentType: "audio/mpeg",
  sizeBytes: 2097152,
  recordedAtEvent: {
    id: auftrittId,
    title: "Frühjahrskonzert",
    dateDisplay: "Mai 2026",
  },
});
const sonstiges = datei(sonstigesId, {
  kind: "sonstiges",
  stimme: null,
  fileName: "liedtext.txt",
  contentType: "text/plain",
  sizeBytes: 2048,
});

/** Erzeugt eine gültige MIDI-Datei (8 Sekunden bei 120 BPM) mit einer Notenspur. */
function midiBytes() {
  const division = 480;
  const tempoSpur = Buffer.from([
    0x4d, 0x54, 0x72, 0x6b, 0, 0, 0, 11, 0x00, 0xff, 0x51, 0x03, 0x07, 0xa1,
    0x20, 0x00, 0xff, 0x2f, 0x00,
  ]);
  const ereignisse: number[] = [];
  for (let index = 0; index < 16; index += 1) {
    ereignisse.push(0x00, 0x90, index % 2 ? 76 : 72, 90);
    // 480 Ticks als variable Länge: 0x83 0x60.
    ereignisse.push(0x83, 0x60, 0x80, index % 2 ? 76 : 72, 0);
  }
  ereignisse.push(0x00, 0xff, 0x2f, 0x00);
  const spurKopf = Buffer.alloc(8);
  spurKopf.write("MTrk", 0);
  spurKopf.writeUInt32BE(ereignisse.length, 4);
  const kopf = Buffer.alloc(14);
  kopf.write("MThd", 0);
  kopf.writeUInt32BE(6, 4);
  kopf.writeUInt16BE(1, 8);
  kopf.writeUInt16BE(2, 10);
  kopf.writeUInt16BE(division, 12);
  return Buffer.concat([kopf, tempoSpur, spurKopf, Buffer.from(ereignisse)]);
}

function wavBytes(sekunden = 2) {
  const sampleRate = 8000;
  const anzahl = sampleRate * sekunden;
  const kopf = Buffer.alloc(44);
  kopf.write("RIFF", 0);
  kopf.writeUInt32LE(36 + anzahl * 2, 4);
  kopf.write("WAVE", 8);
  kopf.write("fmt ", 12);
  kopf.writeUInt32LE(16, 16);
  kopf.writeUInt16LE(1, 20);
  kopf.writeUInt16LE(1, 22);
  kopf.writeUInt32LE(sampleRate, 24);
  kopf.writeUInt32LE(sampleRate * 2, 28);
  kopf.writeUInt16LE(2, 32);
  kopf.writeUInt16LE(16, 34);
  kopf.write("data", 36);
  kopf.writeUInt32LE(anzahl * 2, 40);
  return Buffer.concat([kopf, Buffer.alloc(anzahl * 2)]);
}

function zugriffFuer(id: string, contentType: string, nummer: number) {
  return {
    dateiId: id,
    kind: "uebe",
    contentType,
    sizeBytes: 1000,
    fileName: null,
    createdAt: "2026-09-19T12:00:00.000Z",
    viewUrl: `https://speicher.test/spiel-${nummer}?ticket=ansicht`,
    downloadUrl: `https://speicher.test/spiel-${nummer}?ticket=laden`,
    expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
  };
}

async function liedMitSpielern(page: Page) {
  const zugriffe = new Map([
    [mp3Id, zugriffFuer(mp3Id, "audio/wav", 1)],
    [midiId, zugriffFuer(midiId, "audio/midi", 2)],
  ]);
  await mockSitzung(page, memberMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([mp3, midi, aufnahme, sonstiges]))),
  );
  await page.route("**/api/dateien/*/access", (route) => {
    const id = route.request().url().split("/").at(-2) ?? "";
    return route.fulfill(
      json(zugriffe.get(id) ?? zugriffFuer(id, "text/plain", 9)),
    );
  });
  await page.route("**/speicher.test/spiel-1*", (route) =>
    route.fulfill({ status: 200, contentType: "audio/wav", body: wavBytes() }),
  );
  await page.route("**/speicher.test/spiel-2*", (route) =>
    route.fulfill({
      status: 200,
      contentType: "audio/midi",
      body: midiBytes(),
    }),
  );
}

async function lesePosition(page: Page) {
  const wert = await page
    .getByRole("region", { name: "MIDI-Spieler · Alle" })
    .getByLabel("Position (Alle)")
    .inputValue();
  return Number(wert);
}

test("Mitglied hört die Übe-Datei und ändert das MIDI-Tempo", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await liedMitSpielern(page);

  await page.goto(`/lied/?id=${mondId}`);
  const tenor = page.getByRole("article", { name: "Übe-Datei · Tenor 1" });
  const alle = page.getByRole("article", { name: "Übe-Datei · Alle" });
  await expect(tenor).toBeVisible();
  await expect(alle).toBeVisible();

  await tenor.getByRole("button", { name: "Anhören" }).click();
  const audio = tenor.getByRole("region", { name: "Audio-Spieler · Tenor 1" });
  await expect(audio).toBeVisible();
  await expect(
    tenor.getByRole("link", { name: "Herunterladen" }),
  ).toHaveAttribute("href", /spiel-1\?ticket=laden/);

  await alle.getByRole("button", { name: "Anhören" }).click();
  const spieler = alle.getByRole("region", { name: "MIDI-Spieler · Alle" });
  await expect(spieler.getByText("0:00 / 0:08")).toBeVisible();
  await spieler.getByRole("button", { name: "Alle (MIDI) abspielen" }).click();
  await expect(
    spieler.getByRole("button", { name: "Alle (MIDI) pausieren" }),
  ).toBeVisible();
  await expect
    .poll(() => lesePosition(page), { timeout: 10_000 })
    .toBeGreaterThan(0.5);

  await spieler.getByLabel("Tempo (Alle)").fill("150");
  await expect(spieler.getByText("150 %")).toBeVisible();
  await expect(
    spieler.getByRole("button", { name: "Alle (MIDI) pausieren" }),
  ).toBeVisible();
  const messung1 = await lesePosition(page);
  await page.waitForTimeout(1500);
  const messung2 = await lesePosition(page);
  expect(messung2 - messung1).toBeGreaterThan(1.0);
  expect(errors).toEqual([]);
});

test("Aufnahme nennt den Auftritt, Sonstiges lässt sich herunterladen", async ({
  page,
}) => {
  await liedMitSpielern(page);

  await page.goto(`/lied/?id=${mondId}`);
  const eintrag = page.getByRole("article", { name: /^Aufnahme · / });
  await expect(
    eintrag.getByRole("link", { name: "Frühjahrskonzert" }),
  ).toHaveAttribute("href", `/auftritt/?id=${auftrittId}`);
  await expect(eintrag.getByText("(Mai 2026)")).toBeVisible();
  await expect(eintrag.getByRole("button", { name: "Anhören" })).toBeVisible();

  const text = page.getByRole("article", { name: "liedtext.txt" });
  await expect(
    text.getByRole("button", { name: "Herunterladen" }),
  ).toBeVisible();
});

test("Redaktion fügt Übe-Dateien mit Stimmenvorschlägen, Aufnahme und Sonstiges hinzu", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/lieder/${mondId}`, (route) =>
    route.fulfill(json(liedDetail([]))),
  );
  await page.route("**/api/events", (route) =>
    route.fulfill(
      json({
        years: [],
        total: 1,
        events: [
          {
            id: auftrittId,
            kind: "concert",
            title: "Frühjahrskonzert",
            dateDisplay: "Mai 2026",
            published: true,
          },
        ],
      }),
    ),
  );
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
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(json({ datei: mp3 })),
  );

  await page.goto(`/lied/?id=${mondId}`);
  await page.getByLabel("Art der Datei").selectOption("uebe");
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
  const wahl = page.waitForEvent("filechooser");
  await page.getByLabel("Übe-Dateien auswählen").click();
  const chooser = await wahl;
  await chooser.setFiles([
    { name: "tenor_1.mp3", mimeType: "audio/mpeg", buffer: Buffer.from("ID3") },
    {
      name: "klavier.mid",
      mimeType: "audio/midi",
      buffer: Buffer.from("MThd"),
    },
    { name: "solo.mp3", mimeType: "audio/mpeg", buffer: Buffer.from("ID3") },
  ]);
  await expect(page.getByLabel("Stimme für tenor_1.mp3")).toHaveValue(
    "Tenor 1",
  );
  await page.getByLabel("Stimme für klavier.mid").fill("Alle");
  await page.getByLabel("Stimme für solo.mp3").fill("Solo Bariton");
  await page.getByRole("button", { name: "Hochladen" }).click();
  await expect(page.getByText("3 von 3 Dateien gespeichert.")).toBeVisible();
  expect(sitzungen.map((s) => [s.kind, s.stimme, s.contentType])).toEqual([
    ["uebe", "Tenor 1", "audio/mpeg"],
    ["uebe", "Alle", "audio/midi"],
    ["uebe", "Solo Bariton", "audio/mpeg"],
  ]);

  sitzungen.length = 0;
  await page.getByLabel("Art der Datei").selectOption("aufnahme");
  await expect(page.getByLabel("Stimme für konzert.mp3")).toHaveCount(0);
  await page.getByLabel("Auftritt (optional)").selectOption(auftrittId);
  let wahl2 = page.waitForEvent("filechooser");
  await page.getByLabel("Aufnahmen auswählen").click();
  await (await wahl2).setFiles({
    name: "konzert.mp3",
    mimeType: "audio/mpeg",
    buffer: Buffer.from("ID3"),
  });
  await page.getByRole("button", { name: "Hochladen" }).click();
  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();
  await page.getByLabel("Auftritt (optional)").selectOption("");
  wahl2 = page.waitForEvent("filechooser");
  await page.getByLabel("Aufnahmen auswählen").click();
  await (await wahl2).setFiles({
    name: "probe.mp3",
    mimeType: "audio/mpeg",
    buffer: Buffer.from("ID3"),
  });
  await page.getByRole("button", { name: "Hochladen" }).click();
  await expect(page.getByText("2 von 2 Dateien gespeichert.")).toBeVisible();
  expect(sitzungen.map((s) => s.recordedAtEventId ?? null)).toEqual([
    auftrittId,
    null,
  ]);

  sitzungen.length = 0;
  await page.getByLabel("Art der Datei").selectOption("sonstiges");
  const wahl3 = page.waitForEvent("filechooser");
  await page.getByLabel("Sonstiges auswählen").click();
  await (await wahl3).setFiles({
    name: "liedtext.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("Text"),
  });
  await page.getByRole("button", { name: "Hochladen" }).click();
  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();
  expect(sitzungen.map((s) => [s.kind, s.stimme, s.contentType])).toEqual([
    ["sonstiges", null, "text/plain"],
  ]);
  expect(errors).toEqual([]);
});
