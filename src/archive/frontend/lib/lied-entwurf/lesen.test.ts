import assert from "node:assert/strict";
import { test } from "node:test";
import {
  anfangszustand,
  type Ereignis,
  kannSpeichern,
  reduziere,
  type Zustand,
} from "./reducer.ts";

// #177: Vorschläge aus dem Lesen füllen nur unberührte Felder. Reine
// Ereignisfolgen: kein React, keine Zeit, kein Netz.

const E = "entwurf-1";
const PDF = "d3";
const AVE = [
  { id: "d1", name: "Ave_Maria_T1.mp3", sizeBytes: 100 },
  { id: "d2", name: "Ave_Maria_B1.mp3", sizeBytes: 100 },
  { id: PDF, name: "Ave_Maria.pdf", sizeBytes: 200 },
];

function lauf(start: Zustand, ...ereignisse: Ereignis[]): Zustand {
  return ereignisse.reduce(reduziere, start);
}

function inSammeln(zustand: Zustand) {
  assert.equal(zustand.phase, "sammeln");
  return zustand as Extract<Zustand, { phase: "sammeln" }>;
}

const abgelegt = (dateien = AVE): Ereignis => ({
  type: "dateien-abgelegt",
  entwurfId: E,
  dateien,
});

const liest = (anfrageId = 1, dateiId = PDF): Ereignis => ({
  type: "lesen-angefordert",
  entwurfId: E,
  dateiId,
  anfrageId,
});

type Vorschlag = { feld: string; wert: string; sicher: boolean };

const ergebnis = (
  felder: Vorschlag[],
  anfrageId = 1,
  dateiId = PDF,
): Ereignis =>
  ({
    type: "lesen-ergebnis",
    entwurfId: E,
    dateiId,
    anfrageId,
    felder,
  }) as Ereignis;

const bearbeitet = (feld: string, wert: string): Ereignis =>
  ({ type: "lied-feld-geaendert", entwurfId: E, feld, wert }) as Ereignis;

test("ein Lesen füllt die leeren Felder und markiert sie als Vorschlag", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt(),
      liest(),
      ergebnis([
        { feld: "title", wert: "Ave Maria (Bruckner)", sicher: true },
        { feld: "composer", wert: "Anton Bruckner", sicher: true },
        { feld: "musicalKey", wert: "d-Moll", sicher: false },
      ]),
    ),
  );
  assert.equal(z.lied.title, "Ave Maria (Bruckner)");
  assert.equal(z.lied.composer, "Anton Bruckner");
  assert.equal(z.lied.musicalKey, "d-Moll");
  assert.deepEqual(z.vorschlaege, {
    title: "sicher",
    composer: "sicher",
    musicalKey: "unsicher",
  });
  assert.equal(z.dateien.find((d) => d.id === PDF)?.lesen.status, "gelesen");
  assert.equal(z.bearbeitet.composer, false);
});

test("eine Änderung nimmt die Markierung, ein späterer Vorschlag ändert das Feld nicht", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt(),
      liest(),
      ergebnis([{ feld: "composer", wert: "Anton Bruckner", sicher: true }]),
      bearbeitet("composer", "Bruckner, A."),
    ),
  );
  assert.equal(z.lied.composer, "Bruckner, A.");
  assert.equal(z.vorschlaege.composer, undefined);
});

test("bearbeiten, dann spätes Lesen: die Änderung bleibt, auch das absichtliche Leeren", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt(),
      liest(),
      bearbeitet("composer", "Eigener Name"),
      bearbeitet("lyricist", ""),
      bearbeitet("title", ""),
      ergebnis([
        { feld: "composer", wert: "Anton Bruckner", sicher: true },
        { feld: "lyricist", wert: "Jemand", sicher: true },
        { feld: "title", wert: "Anderer Titel", sicher: true },
        { feld: "language", wert: "Latein", sicher: true },
      ]),
    ),
  );
  assert.equal(z.lied.composer, "Eigener Name");
  assert.equal(z.lied.lyricist, "");
  assert.equal(z.lied.title, "");
  assert.equal(z.lied.language, "Latein");
  assert.deepEqual(z.vorschlaege, { language: "sicher" });
});

test("ein Vorschlag überschreibt kein früheres Lesen", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt([...AVE, { id: "d4", name: "Zweit.pdf", sizeBytes: 1 }]),
      liest(1, PDF),
      liest(1, "d4"),
      ergebnis([{ feld: "composer", wert: "Erster", sicher: true }]),
      ergebnis([{ feld: "composer", wert: "Zweiter", sicher: true }], 1, "d4"),
    ),
  );
  assert.equal(z.lied.composer, "Erster");
});

test("ein verworfener Entwurf ignoriert späte Lesungen", () => {
  const verworfen = lauf(anfangszustand(E), abgelegt(), liest(), {
    type: "entwurf-verworfen",
    entwurfId: E,
  });
  const danach = reduziere(
    verworfen,
    ergebnis([{ feld: "composer", wert: "X", sicher: true }]),
  );
  assert.equal(danach, verworfen);
});

test("ein Ergebnis eines anderen Entwurfs ändert nichts", () => {
  const vorher = lauf(anfangszustand(E), abgelegt(), liest());
  const danach = reduziere(vorher, {
    ...(ergebnis([{ feld: "composer", wert: "X", sicher: true }]) as object),
    entwurfId: "anderer",
  } as Ereignis);
  assert.equal(danach, vorher);
});

test("ein überholtes Lesen wird ignoriert, das neue gilt", () => {
  const nachWiederholen = lauf(
    anfangszustand(E),
    abgelegt(),
    liest(1),
    {
      type: "lesen-fehlgeschlagen",
      entwurfId: E,
      dateiId: PDF,
      anfrageId: 1,
      meldung: "Zeit",
      verfuegbar: true,
    },
    liest(2),
  );
  const spaet = reduziere(
    nachWiederholen,
    ergebnis([{ feld: "composer", wert: "Alt", sicher: true }], 1),
  );
  assert.equal(spaet, nachWiederholen);
  const z = inSammeln(
    reduziere(
      spaet,
      ergebnis([{ feld: "composer", wert: "Neu", sicher: true }], 2),
    ),
  );
  assert.equal(z.lied.composer, "Neu");
});

test("nicht verfügbar lässt von Hand weiter", () => {
  const z = inSammeln(
    lauf(anfangszustand(E), abgelegt(), liest(), {
      type: "lesen-fehlgeschlagen",
      entwurfId: E,
      dateiId: PDF,
      anfrageId: 1,
      meldung: "Das Monatsbudget ist erreicht.",
      verfuegbar: false,
    }),
  );
  assert.deepEqual(z.dateien.find((d) => d.id === PDF)?.lesen, {
    status: "nicht-verfuegbar",
    anfrageId: 1,
    meldung: "Das Monatsbudget ist erreicht.",
  });
  assert.equal(kannSpeichern(z), true);
});

test("Vorschläge ändern ein Lied nicht mehr, das schon gespeichert wird", () => {
  const speichert = lauf(anfangszustand(E), abgelegt(), liest(), {
    type: "speichern-angefordert",
    entwurfId: E,
    anfrageId: 1,
  });
  const danach = reduziere(
    speichert,
    ergebnis([{ feld: "composer", wert: "X", sicher: true }]),
  );
  assert.equal(danach, speichert);
});
