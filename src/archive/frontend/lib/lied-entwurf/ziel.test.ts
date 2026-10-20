import assert from "node:assert/strict";
import { test } from "node:test";
import {
  anfangszustand,
  type Ereignis,
  hochgeladene,
  kannSpeichern,
  reduziere,
  type Zustand,
} from "./reducer.ts";

// Zuordnung zu einem vorhandenen Lied (#179): reine Ereignisfolgen.

const E = "entwurf-1";
const AVE = [
  { id: "d1", name: "Ave_Maria_T1.mp3", sizeBytes: 100 },
  { id: "d2", name: "Ave_Maria_B1.mp3", sizeBytes: 100 },
  { id: "d3", name: "Ave_Maria.pdf", sizeBytes: 200 },
];

function lauf(start: Zustand, ...ereignisse: Ereignis[]): Zustand {
  return ereignisse.reduce(reduziere, start);
}

const abgelegt = (dateien = AVE): Ereignis => ({
  type: "dateien-abgelegt",
  entwurfId: E,
  dateien,
});

function inSammeln(zustand: Zustand) {
  assert.equal(zustand.phase, "sammeln");
  return zustand as Extract<Zustand, { phase: "sammeln" }>;
}

const gefunden = (titel = "Ave Maria", entwurfId = E): Ereignis => ({
  type: "treffer-gefunden",
  entwurfId,
  titel,
  liedId: "lied-7",
  liedTitel: "Ave Maria",
});
const angenommen: Ereignis = { type: "treffer-angenommen", entwurfId: E };
const abgelehnt: Ereignis = { type: "treffer-abgelehnt", entwurfId: E };

test("ein Treffer wird angeboten, Annehmen wählt das vorhandene Lied", () => {
  const angeboten = inSammeln(lauf(anfangszustand(E), abgelegt(), gefunden()));
  assert.deepEqual(angeboten.ziel, {
    status: "angeboten",
    liedId: "lied-7",
    titel: "Ave Maria",
  });
  const z = inSammeln(lauf(angeboten, angenommen));
  assert.deepEqual(z.ziel, {
    status: "vorhanden",
    liedId: "lied-7",
    titel: "Ave Maria",
    fest: false,
  });
});

test("angenommen und gespeichert: kein neues Lied, die Dateien gehen zum Treffer", () => {
  const z = lauf(
    anfangszustand(E),
    abgelegt(),
    gefunden(),
    angenommen,
    { type: "speichern-angefordert", entwurfId: E, anfrageId: 1 },
    { type: "lied-angelegt", entwurfId: E, anfrageId: 1, liedId: "lied-7" },
    ...AVE.flatMap((d): Ereignis[] => [
      { type: "upload-gestartet", entwurfId: E, dateiId: d.id, versuch: 1 },
      {
        type: "upload-erfolgreich",
        entwurfId: E,
        dateiId: d.id,
        versuch: 1,
        serverDateiId: `s-${d.id}`,
      },
    ]),
  );
  assert.equal(z.phase, "gespeichert");
  assert.equal(z.phase === "gespeichert" && z.liedId, "lied-7");
  assert.equal(hochgeladene(z).length, 3);
});

test("abgelehnt: ein neues Lied entsteht, derselbe Treffer wird nicht erneut angeboten", () => {
  const z = inSammeln(
    lauf(anfangszustand(E), abgelegt(), gefunden(), abgelehnt, gefunden()),
  );
  assert.deepEqual(z.ziel, { status: "abgelehnt", liedId: "lied-7" });
  assert.equal(kannSpeichern(z), true);
});

test("ein Treffer für einen überholten Titel wird nicht angeboten", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt(),
      { type: "lied-feld-geaendert", entwurfId: E, feld: "title", wert: "Neu" },
      gefunden("Ave Maria"),
    ),
  );
  assert.deepEqual(z.ziel, { status: "neu" });
});

test("wer den Titel nach dem Angebot ändert, nimmt es zurück", () => {
  const z = inSammeln(
    lauf(anfangszustand(E), abgelegt(), gefunden(), {
      type: "lied-feld-geaendert",
      entwurfId: E,
      feld: "title",
      wert: "Ave Maria II",
    }),
  );
  assert.deepEqual(z.ziel, { status: "neu" });
});

test("ein spätes Lesen wechselt das gewählte Lied nie", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt([AVE[2]]),
      { type: "lesen-angefordert", entwurfId: E, dateiId: "d3", anfrageId: 1 },
      gefunden(),
      angenommen,
      {
        type: "lesen-ergebnis",
        entwurfId: E,
        dateiId: "d3",
        anfrageId: 1,
        felder: [{ feld: "title", wert: "Anderes Lied", sicher: true }],
      },
      gefunden("Anderes Lied"),
    ),
  );
  assert.equal(z.ziel.status, "vorhanden");
  assert.equal(z.ziel.status === "vorhanden" && z.ziel.liedId, "lied-7");
});

test("ein Treffer eines verworfenen oder anderen Entwurfs ändert nichts", () => {
  const offen = lauf(anfangszustand(E), abgelegt());
  assert.equal(reduziere(offen, gefunden("Ave Maria", "anderer")), offen);
  const weg = lauf(offen, { type: "entwurf-verworfen", entwurfId: E });
  assert.equal(reduziere(weg, gefunden()), weg);
  assert.equal(reduziere(weg, angenommen), weg);
});

test("Annehmen ohne Angebot ändert nichts", () => {
  const offen = lauf(anfangszustand(E), abgelegt());
  assert.equal(reduziere(offen, angenommen), offen);
});

test("ein fest gewähltes Lied (Lied-Seite) braucht keinen Titel und bleibt fest", () => {
  const fest = {
    status: "vorhanden",
    liedId: "lied-7",
    titel: "Ave Maria",
    fest: true,
  } as const;
  const z = inSammeln(
    lauf(anfangszustand(E, fest), abgelegt([AVE[0]]), gefunden("x"), abgelehnt),
  );
  assert.deepEqual(z.ziel, fest);
  assert.equal(kannSpeichern(z), true);
  assert.equal(
    kannSpeichern(
      lauf(z, { type: "datei-entfernt", entwurfId: E, dateiId: "d1" }),
    ),
    false,
  );
});
