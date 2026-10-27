import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  anfangszustand,
  artAusDatei,
  type Ereignis,
  kannSpeichern,
  pruefeWerte,
  reduziere,
  type Zustand,
} from "./reducer.ts";

// Der Entwurf eines Auftritts aus abgelegten Dateien (#131). Die Tests gehen
// durch `reduziere` wie der Hook: Ereignisse hinein, Zustand heraus.

const ID = "a1111111-1111-4111-8111-111111111111";
const SCAN = "d0000000-0000-4000-8000-000000000001";
const VIDEO = "d0000000-0000-4000-8000-000000000002";

function lauf(ereignisse: Ereignis[], start?: Zustand): Zustand {
  return ereignisse.reduce(reduziere, start ?? anfangszustand(ID));
}

const scanAbgelegt: Ereignis = {
  type: "dateien-abgelegt",
  dateien: [
    { id: SCAN, name: "programm.jpg", sizeBytes: 1000, art: "programm" },
  ],
};

const lesung = (
  extra: Partial<Extract<Ereignis, { type: "lesen-fertig" }>> = {},
): Ereignis => ({
  type: "lesen-fertig",
  dateiId: SCAN,
  anfrageId: 1,
  felder: [
    { feld: "title", wert: "Frühjahrskonzert", sicher: true },
    { feld: "jahr", wert: "1951", sicher: true },
    { feld: "monat", wert: "5", sicher: false },
    { feld: "venue", wert: "Gasthaus zur Post", sicher: true },
  ],
  programm: [
    {
      text: "Ave Maria",
      sicher: true,
      lied: { id: "L1", titel: "Ave Maria", sicher: true },
      kandidaten: [],
    },
    {
      text: "Der Mond ist aufgegangn",
      sicher: true,
      lied: { id: "L2", titel: "Der Mond ist aufgegangen", sicher: false },
      kandidaten: [{ id: "L2", titel: "Der Mond ist aufgegangen" }],
    },
    {
      text: "Waldfahrt",
      sicher: true,
      lied: null,
      kandidaten: [],
    },
  ],
  ...extra,
});

describe("Dateiarten", () => {
  it("ordnet Scan und Foto dem Programm zu, Video und Ton der Aufnahme", () => {
    assert.equal(artAusDatei("programm.pdf", "application/pdf"), "programm");
    assert.equal(artAusDatei("IMG_0001.JPG", ""), "programm");
    assert.equal(artAusDatei("foto.webp", "image/webp"), "programm");
    assert.equal(artAusDatei("konzert.mp4", "video/mp4"), "aufnahme");
    assert.equal(artAusDatei("konzert.MOV", ""), "aufnahme");
    assert.equal(artAusDatei("mitschnitt.mp3", "audio/mpeg"), "aufnahme");
    assert.equal(artAusDatei("protokoll.docx", ""), "sonstiges");
  });
});

describe("Lesen eines Programms", () => {
  it("füllt leere Felder und Programmzeilen und markiert sie als Vorschlag", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      lesung(),
    ]);
    assert.equal(z.werte.title, "Frühjahrskonzert");
    assert.equal(z.werte.jahr, "1951");
    assert.equal(z.werte.monat, "5");
    assert.equal(z.werte.venue, "Gasthaus zur Post");
    assert.deepEqual(z.vorschlaege, {
      title: "sicher",
      jahr: "sicher",
      monat: "unsicher",
      venue: "sicher",
    });
    assert.deepEqual(
      z.programm.map((p) => p.text),
      ["Ave Maria", "Der Mond ist aufgegangn", "Waldfahrt"],
    );
    assert.equal(z.dateien[0].lesen.status, "gelesen");
  });

  it("lässt ein Feld in Ruhe, das die Redaktion angefasst hat", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "feld-geaendert", feld: "title", wert: "Mein Titel" },
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      lesung(),
    ]);
    assert.equal(z.werte.title, "Mein Titel");
    assert.equal(z.vorschlaege.title, undefined);
    assert.equal(z.werte.jahr, "1951");
  });

  it("überschreibt kein Feld, das schon einen Wert hat", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "feld-geaendert", feld: "venue", wert: "" },
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      lesung(),
    ]);
    // Ein von Hand geleertes Feld bleibt leer: es ist angefasst.
    assert.equal(z.werte.venue, "");
  });

  it("nimmt den Vorschlag zurück, sobald das Feld bearbeitet wird", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      lesung(),
      { type: "feld-geaendert", feld: "title", wert: "Anderer Titel" },
    ]);
    assert.equal(z.vorschlaege.title, undefined);
    assert.equal(z.bearbeitet.title, true);
  });

  it("ignoriert eine überholte Antwort", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 2 },
      lesung({ anfrageId: 1 }),
    ]);
    assert.equal(z.werte.title, "");
    assert.equal(z.programm.length, 0);
    assert.deepEqual(z.dateien[0].lesen, { status: "liest", anfrageId: 2 });
  });

  it("ignoriert eine Antwort für eine entfernte Datei", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      { type: "datei-entfernt", dateiId: SCAN },
      lesung(),
    ]);
    assert.equal(z.werte.title, "");
    assert.equal(z.programm.length, 0);
  });

  it("hängt die Zeilen eines zweiten Scans an die des ersten", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      lesung(),
      {
        type: "dateien-abgelegt",
        dateien: [
          { id: VIDEO, name: "seite2.jpg", sizeBytes: 5, art: "programm" },
        ],
      },
      { type: "lesen-beginnt", dateiId: VIDEO, anfrageId: 2 },
      {
        type: "lesen-fertig",
        dateiId: VIDEO,
        anfrageId: 2,
        felder: [{ feld: "title", wert: "Anderer", sicher: true }],
        programm: [
          { text: "Stille Nacht", sicher: true, lied: null, kandidaten: [] },
        ],
      },
    ]);
    assert.equal(z.programm.length, 4);
    assert.equal(z.programm[3].text, "Stille Nacht");
    assert.equal(z.werte.title, "Frühjahrskonzert");
  });

  it("merkt sich, dass die KI nicht zur Verfügung steht, und lässt Speichern zu", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      {
        type: "lesen-gescheitert",
        dateiId: SCAN,
        anfrageId: 1,
        meldung: "Das Monatsbudget der KI ist erreicht.",
        verfuegbar: false,
      },
      { type: "feld-geaendert", feld: "title", wert: "Konzert" },
    ]);
    assert.equal(z.ki.status, "nicht-verfuegbar");
    assert.equal(z.dateien[0].lesen.status, "nicht-verfuegbar");
    assert.equal(kannSpeichern(z), true);
  });

  it("lässt nach einem Fehler erneutes Lesen zu", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      {
        type: "lesen-gescheitert",
        dateiId: SCAN,
        anfrageId: 1,
        meldung: "Das Lesen ist gescheitert.",
        verfuegbar: true,
      },
    ]);
    assert.deepEqual(z.dateien[0].lesen, {
      status: "fehlgeschlagen",
      anfrageId: 1,
      meldung: "Das Lesen ist gescheitert.",
    });
    assert.equal(z.ki.status, "verfuegbar");
  });
});

describe("Programmzeilen", () => {
  const gelesen = () =>
    lauf([
      scanAbgelegt,
      { type: "lesen-beginnt", dateiId: SCAN, anfrageId: 1 },
      lesung(),
    ]);

  it("kann erst gespeichert werden, wenn jede Zeile ein Lied hat", () => {
    const z = gelesen();
    assert.equal(kannSpeichern(z), false);
    const [, , offen] = z.programm;
    const ohneOffene = reduziere(z, {
      type: "zeile-entfernt",
      zeileId: offen.id,
    });
    assert.equal(kannSpeichern(ohneOffene), true);
    const gewaehlt = reduziere(z, {
      type: "zeile-gewaehlt",
      zeileId: offen.id,
      lied: { id: "L3", titel: "Waldfahrt" },
    });
    assert.equal(kannSpeichern(gewaehlt), true);
    assert.equal(gewaehlt.programm[2].lied?.id, "L3");
    assert.equal(gewaehlt.programm[2].sicher, true);
  });

  it("macht einen unsicheren Vorschlag mit Übernehmen sicher", () => {
    const z = gelesen();
    const unsicher = z.programm[1];
    assert.equal(unsicher.sicher, false);
    const angenommen = reduziere(z, {
      type: "zeile-angenommen",
      zeileId: unsicher.id,
    });
    assert.equal(angenommen.programm[1].sicher, true);
    assert.equal(angenommen.programm[1].lied?.id, "L2");
  });

  it("ersetzt ein Lied durch die Wahl der Redaktion", () => {
    const z = gelesen();
    const gewaehlt = reduziere(z, {
      type: "zeile-gewaehlt",
      zeileId: z.programm[0].id,
      lied: { id: "L9", titel: "Anderes" },
    });
    assert.equal(gewaehlt.programm[0].lied?.id, "L9");
    assert.equal(gewaehlt.programm[0].sicher, true);
  });

  it("fügt Lieder von Hand hinzu und verschiebt Zeilen", () => {
    const z = reduziere(gelesen(), {
      type: "zeile-hinzugefuegt",
      zeileId: "neu-1",
      lied: { id: "L7", titel: "Stille Nacht" },
    });
    assert.equal(z.programm.at(-1)?.lied?.titel, "Stille Nacht");
    const hoch = reduziere(z, {
      type: "zeile-verschoben",
      zeileId: "neu-1",
      richtung: -1,
    });
    assert.equal(hoch.programm[2].id, "neu-1");
    const ganzHoch = reduziere(
      reduziere(hoch, {
        type: "zeile-verschoben",
        zeileId: "neu-1",
        richtung: -1,
      }),
      { type: "zeile-verschoben", zeileId: "neu-1", richtung: -1 },
    );
    assert.equal(ganzHoch.programm[0].id, "neu-1");
    const amAnfang = reduziere(ganzHoch, {
      type: "zeile-verschoben",
      zeileId: "neu-1",
      richtung: -1,
    });
    assert.equal(amAnfang, ganzHoch);
  });
});

describe("Prüfen", () => {
  const werte = {
    title: "Konzert",
    kind: "concert",
    jahr: "",
    monat: "",
    tag: "",
    unsicher: false,
    venue: "",
  };

  it("verlangt einen Titel", () => {
    assert.equal(
      pruefeWerte({ ...werte, title: " " }).titel,
      "Der Titel ist erforderlich.",
    );
    assert.equal(pruefeWerte(werte).titel, undefined);
  });

  it("lässt ein Datum nur aus einem Stück von außen nach innen zu", () => {
    assert.equal(
      pruefeWerte({ ...werte, monat: "5" }).datum,
      "Das Datum ist unvollständig.",
    );
    assert.equal(
      pruefeWerte({ ...werte, jahr: "1951", tag: "3" }).datum,
      "Das Datum ist unvollständig.",
    );
    assert.equal(
      pruefeWerte({ ...werte, jahr: "1700" }).datum,
      "Das Jahr liegt außerhalb des möglichen Bereichs.",
    );
    assert.equal(
      pruefeWerte({ ...werte, jahr: "1951", monat: "2", tag: "30" }).datum,
      "Das Datum passt nicht zum angegebenen Monat.",
    );
    assert.equal(
      pruefeWerte({ ...werte, jahr: "1951", monat: "2", tag: "28" }).datum,
      undefined,
    );
    assert.equal(pruefeWerte({ ...werte, jahr: "1951" }).datum, undefined);
  });

  it("verhindert Speichern ohne Titel, ohne Datei-Inhalt und während des Lesens", () => {
    const ohneTitel = lauf([scanAbgelegt]);
    assert.equal(kannSpeichern(ohneTitel), false);
    const mitTitel = reduziere(ohneTitel, {
      type: "feld-geaendert",
      feld: "title",
      wert: "Konzert",
    });
    assert.equal(kannSpeichern(mitTitel), true);
    const liest = reduziere(mitTitel, {
      type: "lesen-beginnt",
      dateiId: SCAN,
      anfrageId: 1,
    });
    assert.equal(kannSpeichern(liest), false);
    const fehlt = reduziere(mitTitel, {
      type: "datei-inhalt-fehlt",
      dateiId: SCAN,
    });
    assert.equal(kannSpeichern(fehlt), false);
  });
});

describe("Dateien", () => {
  it("lässt sich ohne Scan speichern, wenn nur ein Video da ist", () => {
    const z = lauf([
      {
        type: "dateien-abgelegt",
        dateien: [
          { id: VIDEO, name: "konzert.mp4", sizeBytes: 9, art: "aufnahme" },
        ],
      },
      { type: "feld-geaendert", feld: "title", wert: "Sommerkonzert" },
    ]);
    assert.equal(z.dateien[0].lesen.status, "keine");
    assert.equal(kannSpeichern(z), true);
  });

  it("ändert die Art einer Datei und entfernt sie wieder", () => {
    const z = lauf([
      scanAbgelegt,
      { type: "datei-art", dateiId: SCAN, art: "sonstiges" },
    ]);
    assert.equal(z.dateien[0].art, "sonstiges");
    assert.equal(
      reduziere(z, { type: "datei-entfernt", dateiId: SCAN }).dateien.length,
      0,
    );
  });
});

describe("Speichern", () => {
  const bereit = () =>
    lauf([
      scanAbgelegt,
      { type: "feld-geaendert", feld: "title", wert: "Konzert" },
    ]);

  it("merkt sich den angelegten Auftritt und jeden Upload", () => {
    const z = lauf(
      [
        { type: "speichern-beginnt" },
        { type: "auftritt-angelegt", auftrittId: "E1" },
        { type: "programm-gespeichert" },
        { type: "upload", dateiId: SCAN, upload: { status: "hochgeladen" } },
      ],
      bereit(),
    );
    assert.equal(z.phase, "speichern");
    assert.equal(z.auftrittId, "E1");
    assert.equal(z.programmGespeichert, true);
    assert.equal(z.dateien[0].upload.status, "hochgeladen");
  });

  it("endet mit gespeichert und zeigt einen Fehler, der erneutes Versuchen erlaubt", () => {
    const fehler = lauf(
      [
        { type: "speichern-beginnt" },
        { type: "speichern-gescheitert", meldung: "Das hat nicht geklappt." },
      ],
      bereit(),
    );
    assert.equal(fehler.phase, "speichern");
    assert.equal(fehler.fehler, "Das hat nicht geklappt.");
    const nochmal = reduziere(fehler, { type: "speichern-beginnt" });
    assert.equal(nochmal.fehler, null);
    assert.equal(
      reduziere(nochmal, { type: "gespeichert" }).phase,
      "gespeichert",
    );
  });

  it("verändert nach dem Anlegen die Felder nicht mehr", () => {
    const z = lauf(
      [
        { type: "speichern-beginnt" },
        { type: "auftritt-angelegt", auftrittId: "E1" },
        { type: "feld-geaendert", feld: "title", wert: "Zu spät" },
      ],
      bereit(),
    );
    assert.equal(z.werte.title, "Konzert");
  });
});
