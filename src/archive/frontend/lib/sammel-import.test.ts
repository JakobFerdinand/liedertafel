import assert from "node:assert/strict";
import { test } from "node:test";
import { gruppiereImport } from "./sammel-import.ts";

// #143 Sammel-Import: ein Ordner (Drive-Eport) wird zu einem Plan von
// Vorschlägen — ein Unterordner je Lied. Die Regeln kommen aus
// tests/archive/fixtures/drive-inventur/mapping.json: leere Ordner bleiben
// leer, Verknüpfungen zählen einmal, ungelöste Doppel jelent bleiben, und
// Kandidaten wie .xls/.capx werden nie automatisch importiert.

function datei(pfad: string, sizeBytes = 100) {
  const name = pfad.split("/").at(-1) ?? pfad;
  return { name, pfad, sizeBytes };
}

test("Jeder Unterordner wird ein Vorschlag mit seinen Dateien", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Sample A/Sample A Partitur.pdf"),
    datei("Liederheft/Sample A/Sample A Bass 1.pdf"),
    datei("Liederheft/Sample B/PT - Sample B.pdf"),
  ]);
  assert.deepEqual(
    plan.gruppen.map((g) => g.ordner),
    ["Sample A", "Sample B"],
  );
  const gruppeA = plan.gruppen[0];
  assert.equal(gruppeA.titel, "Sample A");
  assert.equal(gruppeA.dateien.length, 2);
  // Der Vorschlag für die Partitur kommt aus dem Dateinamen.
  const partitur = gruppeA.dateien.find((d) => d.art === "partitur");
  assert.ok(partitur);
  const stimme = gruppeA.dateien.find(
    (d) => d.art === "noten" && d.stimme === "Bass 1",
  );
  assert.ok(stimme);
});

test("Leere Ordner importieren nichts (fixtureAnhang .gitkeep)", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Sample B/1. Noten/PT - Sample B.pdf"),
    // Leere Ordner der Inventur tragen den .gitkeep-Platzhalter.
    datei("Liederheft/Sample B/2. Media/.gitkeep", 0),
    datei("Liederheft/Sample B/3. Notierprogrammdaten/.gitkeep", 0),
  ]);
  assert.deepEqual(
    plan.gruppen.map((g) => g.ordner),
    ["Sample B"],
  );
});

test("Material-Unterordner wie „1. Noten“ gliedern nicht, sie sammeln", async () => {
  const plan = await gruppiereImport([
    datei("Sample B/1. Noten/PT - Sample B.pdf"),
    datei("Sample B/2. Media/Sample B.mp3"),
  ]);
  assert.deepEqual(
    plan.gruppen.map((g) => g.ordner),
    ["Sample B"],
  );
  assert.equal(plan.gruppen[0].dateien.length, 2);
});

test(".xls und .capx werden separat geprüft, nie automatisch importiert", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Sample A/Sample A Verzeichnis.xls"),
    datei("Liederheft/Sample C/Sample C 3.capx"),
    datei("Liederheft/Sample C/Sample C Partitur.pdf"),
  ]);
  // Sample C entsteht nur mit dem PDF, das .xls bleibt ohne Vorschlag.
  assert.deepEqual(
    plan.gruppen.map((g) => g.ordner),
    ["Sample C"],
  );
  assert.equal(plan.gruppen[0].dateien.length, 1);
  assert.deepEqual(
    plan.separatPruefen.map((p) => p.name),
    ["Sample A Verzeichnis.xls", "Sample C 3.capx"],
  );
});

test("Eine Verknüpfung auf einen vorhandenen Ordner importiert ihn genau einmal", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Sample A/Sample A Partitur.pdf"),
    // Drive-Eport als .url-Verknüpfung auf denselben Ordner.
    datei("Liederheft/Verknüpfung - Sample A.url", 400),
  ]);
  assert.deepEqual(
    plan.gruppen.map((g) => g.ordner),
    ["Sample A"],
  );
  assert.deepEqual(plan.verknuepfungen, []);
});

test("Eine Verknüpfung ohne Ziel im Export bleibt ungelöst gemeldet", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Verknüpfung - Fehlt.url", 300),
  ]);
  assert.equal(plan.gruppen.length, 0);
  assert.deepEqual(
    plan.verknuepfungen.map((v) => v.name),
    ["Verknüpfung - Fehlt.url"],
  );
});

test("Ungelöste Doppel bleiben ungerichtete nebeneinander", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Sample C/Sample C B1.pdf", 21000),
    datei("Liederheft/Sample C/Sample C B1 (1).pdf", 316000),
  ]);
  const gruppe = plan.gruppen[0];
  assert.equal(gruppe.ordner, "Sample C");
  const bass = gruppe.dateien.filter((d) => d.stimme === "Bass 1");
  assert.equal(bass.length, 2);
  assert.deepEqual(
    bass.map((d) => d.name),
    ["Sample C B1.pdf", "Sample C B1 (1).pdf"],
  );
});

test("MIDI ist eine Übe-Datei; ihr Stimmenwert wird nie erraten", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Midi-Beispiel/Beispiel Uebefassung.mid"),
    datei("Liederheft/Midi-Beispiel/Beispiel Tenor 1.mid"),
  ]);
  const gruppe = plan.gruppen[0];
  for (const d of gruppe.dateien) assert.equal(d.art, "uebe");
  const unbekannt = gruppe.dateien.find(
    (d) => d.name === "Beispiel Uebefassung.mid",
  );
  assert.ok(unbekannt);
  assert.equal(unbekannt?.stimmeUnbekannt, true);
  const bekannt = gruppe.dateien.find((d) => d.name === "Beispiel Tenor 1.mid");
  assert.equal(bekannt?.stimmeUnbekannt, false);
});

test("Lose Dateien im gewählten Ordner bilden einen eigenen Vorschlag", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Ave Maria.pdf"),
    datei("Liederheft/Ave Maria.mp3"),
  ]);
  assert.equal(plan.gruppen.length, 1);
  assert.equal(plan.gruppen[0].ordner, "Liederheft");
  assert.equal(plan.gruppen[0].dateien.length, 2);
});

test("Die Entwurf-Ids sind aus dem Ordnerinhalt bestimmt: gleiches Angebot, gleiches Id", async () => {
  const eins = await gruppiereImport([
    datei("Liederheft/Sample A/Sample A Partitur.pdf"),
    datei("Liederheft/Sample A/Sample A Bass 1.pdf"),
  ]);
  const zwei = await gruppiereImport([
    datei("Liederheft/Sample A/Sample A Partitur.pdf"),
    datei("Liederheft/Sample A/Sample A Bass 1.pdf"),
  ]);
  const anders = await gruppiereImport([
    datei("Liederheft/Sample A/Sample A Partitur.pdf"),
  ]);
  assert.notEqual(
    eins.gruppen[0].entwurfId,
    "00000000-0000-0000-0000-000000000000",
  );
  assert.equal(eins.gruppen[0].entwurfId, zwei.gruppen[0].entwurfId);
  assert.notEqual(eins.gruppen[0].entwurfId, anders.gruppen[0].entwurfId);
  for (const gruppe of [...eins.gruppen, ...anders.gruppen])
    assert.match(gruppe.entwurfId, /^[0-9a-f-]{36}$/);
});

test("Ein Ordner nur mit separat zu prüfenden Dateien importiert nichts", async () => {
  const plan = await gruppiereImport([
    datei("Liederheft/Nur Xlsx/Katalog.xlsx"),
    datei("Liederheft/Nur Xlsx/Notation.capx"),
  ]);
  assert.equal(plan.gruppen.length, 0);
});
