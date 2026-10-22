import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { alsGespeichert, ausGespeichert, UNTERBROCHEN } from "./gespeichert.ts";
import {
  anfangszustand,
  type Ereignis,
  reduziere,
  type Zustand,
} from "./reducer.ts";

const ID = "a1111111-1111-4111-8111-111111111111";
const SCAN = "d0000000-0000-4000-8000-000000000001";
const VIDEO = "d0000000-0000-4000-8000-000000000002";

function lauf(ereignisse: Ereignis[]): Zustand {
  return ereignisse.reduce(reduziere, anfangszustand(ID));
}

const mitScan: Ereignis[] = [
  {
    type: "dateien-abgelegt",
    dateien: [
      { id: SCAN, name: "programm.jpg", sizeBytes: 1000, art: "programm" },
    ],
  },
  { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
  {
    type: "lesen-fertig",
    dateiId: SCAN,
    anfrageId: 1,
    felder: [{ feld: "title", wert: "Konzert", sicher: false }],
    programm: [
      {
        text: "Ave Maria",
        sicher: true,
        lied: null,
        kandidaten: [{ id: "L1", titel: "Ave Maria" }],
      },
    ],
  },
];

describe("Gespeicherter Entwurf", () => {
  it("hält ohne Dateien nichts fest", () => {
    assert.equal(alsGespeichert(anfangszustand(ID)), null);
  });

  it("speichert Felder, Bearbeitungsmarken, Vorschläge, Programm und Dateien ohne Bytes", () => {
    const z = lauf([
      ...mitScan,
      { type: "feld-geaendert", feld: "venue", wert: "Pfarrsaal" },
    ]);
    const g = alsGespeichert(z);
    assert.ok(g);
    assert.equal(g.version, 1);
    assert.equal(g.werte.title, "Konzert");
    assert.deepEqual(g.bearbeitet, ["venue"]);
    assert.deepEqual(g.vorschlaege, { title: "unsicher" });
    assert.equal(g.programm[0].text, "Ave Maria");
    assert.deepEqual(g.dateien[0], {
      id: SCAN,
      name: "programm.jpg",
      sizeBytes: 1000,
      art: "programm",
      lesen: { status: "gelesen", anfrageId: 1, meldung: "" },
      hochgeladen: false,
    });
    // Keine Herkunft, keine Bytes.
    assert.doesNotMatch(
      JSON.stringify(g),
      /herkunft|"inhalt"|"quelle"|"file"/i,
    );
  });

  it("stellt denselben Zustand wieder her, die Dateien aber ohne Inhalt", () => {
    const z = lauf(mitScan);
    const g = alsGespeichert(z);
    assert.ok(g);
    const zurueck = ausGespeichert(JSON.parse(JSON.stringify(g)), ID);
    assert.ok(zurueck);
    assert.equal(zurueck.entwurfId, ID);
    assert.deepEqual(zurueck.werte, z.werte);
    assert.deepEqual(zurueck.vorschlaege, z.vorschlaege);
    assert.deepEqual(zurueck.programm, z.programm);
    assert.equal(zurueck.dateien[0].inhalt, "fehlt");
    assert.equal(zurueck.dateien[0].lesen.status, "gelesen");
    assert.equal(zurueck.phase, "sammeln");
  });

  it("meldet ein unterbrochenes Lesen als fehlgeschlagen", () => {
    const z = lauf([
      mitScan[0],
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 3 },
    ]);
    const g = alsGespeichert(z);
    assert.ok(g);
    const zurueck = ausGespeichert(JSON.parse(JSON.stringify(g)), ID);
    assert.ok(zurueck);
    assert.deepEqual(zurueck.dateien[0].lesen, {
      status: "fehlgeschlagen",
      anfrageId: 3,
      meldung: UNTERBROCHEN,
    });
  });

  it("leitet beim Fortsetzen ab, dass die KI nicht zur Verfügung stand", () => {
    const z = lauf([
      mitScan[0],
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      {
        type: "lesen-gescheitert",
        dateiId: SCAN,
        anfrageId: 1,
        meldung: "Das Monatsbudget der KI ist erreicht.",
        verfuegbar: false,
      },
    ]);
    const zurueck = ausGespeichert(
      JSON.parse(JSON.stringify(alsGespeichert(z))),
      ID,
    );
    assert.ok(zurueck);
    assert.equal(zurueck.ki.status, "nicht-verfuegbar");
  });

  it("behält nach dem Anlegen nur, was noch nicht hochgeladen ist, und endet danach", () => {
    const z = lauf([
      mitScan[0],
      {
        type: "dateien-abgelegt",
        dateien: [
          { id: VIDEO, name: "konzert.mp4", sizeBytes: 9, art: "aufnahme" },
        ],
      },
      { type: "feld-geaendert", feld: "title", wert: "Konzert" },
      { type: "speichern-beginnt" },
      { type: "auftritt-angelegt", auftrittId: "E1" },
      { type: "programm-gespeichert" },
      { type: "upload", dateiId: SCAN, upload: { status: "hochgeladen" } },
      {
        type: "upload",
        dateiId: VIDEO,
        upload: { status: "fehlgeschlagen", meldung: "Netz weg" },
      },
      { type: "speichern-gescheitert", meldung: "Eine Datei fehlt noch." },
    ]);
    const g = alsGespeichert(z);
    assert.ok(g);
    assert.equal(g.auftrittId, "E1");
    assert.equal(g.programmGespeichert, true);
    assert.deepEqual(
      g.dateien.map((d) => d.id),
      [VIDEO],
    );
    const zurueck = ausGespeichert(JSON.parse(JSON.stringify(g)), ID);
    assert.ok(zurueck);
    assert.equal(zurueck.phase, "speichern");
    assert.equal(zurueck.auftrittId, "E1");
    assert.equal(zurueck.dateien[0].upload.status, "wartend");

    const fertig = reduziere(
      reduziere(z, {
        type: "upload",
        dateiId: VIDEO,
        upload: { status: "hochgeladen" },
      }),
      { type: "gespeichert" },
    );
    assert.equal(alsGespeichert(fertig), null);
  });

  it("verweigert einen Entwurf, der nicht die bekannte Form hat", () => {
    assert.equal(ausGespeichert({ version: 2 }, ID), null);
    assert.equal(ausGespeichert(null, ID), null);
    assert.equal(ausGespeichert({ version: 1, werte: "x" }, ID), null);
  });
});
