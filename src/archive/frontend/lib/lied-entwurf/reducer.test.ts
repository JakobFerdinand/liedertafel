import assert from "node:assert/strict";
import { test } from "node:test";
import {
  anfangszustand,
  type Ereignis,
  fehlgeschlagene,
  kannSpeichern,
  reduziere,
  type Zustand,
} from "./reducer.ts";

// Reine Ereignisfolgen: kein React, keine Zeit, kein Netz.

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

test("abgelegte Dateien füllen Titel sowie Art und Stimme je Datei", () => {
  const z = inSammeln(lauf(anfangszustand(E), abgelegt()));
  assert.equal(z.lied.title, "Ave Maria");
  assert.deepEqual(
    z.dateien.map((d) => [d.art, d.stimme]),
    [
      ["uebe", "Tenor 1"],
      ["uebe", "Bass 1"],
      ["partitur", ""],
    ],
  );
  assert.ok(z.dateien.every((d) => d.upload.status === "wartend"));
  assert.equal(kannSpeichern(z), true);
});

test("Speichern ohne Titel oder ohne Dateien ist nicht möglich", () => {
  const leer = lauf(anfangszustand(E), abgelegt());
  const ohneTitel = lauf(leer, {
    type: "lied-feld-geaendert",
    entwurfId: E,
    feld: "title",
    wert: "  ",
  });
  assert.equal(kannSpeichern(ohneTitel), false);
  const ohneDateien = lauf(
    leer,
    ...AVE.map(
      (d): Ereignis => ({
        type: "datei-entfernt",
        entwurfId: E,
        dateiId: d.id,
      }),
    ),
  );
  assert.equal(kannSpeichern(ohneDateien), false);
});

test("eine Änderung markiert das Feld, auch das absichtliche Leeren", () => {
  const z = inSammeln(
    lauf(anfangszustand(E), abgelegt(), {
      type: "lied-feld-geaendert",
      entwurfId: E,
      feld: "title",
      wert: "",
    }),
  );
  assert.equal(z.lied.title, "");
  assert.equal(z.bearbeitet.title, true);
});

test("weitere Dateien überschreiben einen bearbeiteten Titel nicht", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt([AVE[0]]),
      {
        type: "lied-feld-geaendert",
        entwurfId: E,
        feld: "title",
        wert: "Meine Fassung",
      },
      {
        type: "dateien-abgelegt",
        entwurfId: E,
        dateien: [{ id: "d9", name: "Anderes_Lied_T2.mp3", sizeBytes: 5 }],
      },
    ),
  );
  assert.equal(z.lied.title, "Meine Fassung");
  assert.equal(z.dateien.length, 2);
});

test("weitere Dateien setzen einen unberührten Titel nicht neu", () => {
  const z = inSammeln(
    lauf(anfangszustand(E), abgelegt([AVE[0]]), {
      type: "dateien-abgelegt",
      entwurfId: E,
      dateien: [{ id: "d9", name: "Anderes_Lied_T2.mp3", sizeBytes: 5 }],
    }),
  );
  assert.equal(z.lied.title, "Ave Maria");
});

test("Art und Stimme einer Datei sind änderbar und danach markiert", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt(),
      {
        type: "datei-feld-geaendert",
        entwurfId: E,
        dateiId: "d1",
        feld: "stimme",
        wert: "Tenor 2",
      },
      {
        type: "datei-feld-geaendert",
        entwurfId: E,
        dateiId: "d1",
        feld: "art",
        wert: "noten",
      },
    ),
  );
  assert.deepEqual(
    [z.dateien[0].art, z.dateien[0].stimme, z.dateien[0].bearbeitet],
    ["noten", "Tenor 2", { art: true, stimme: true }],
  );
  assert.deepEqual(z.dateien[1].bearbeitet, { art: false, stimme: false });
});

function hochladend(): Zustand {
  return lauf(
    anfangszustand(E),
    abgelegt(),
    {
      type: "lied-feld-geaendert",
      entwurfId: E,
      feld: "title",
      wert: "Ave Maria (Chor)",
    },
    { type: "speichern-angefordert", entwurfId: E, anfrageId: 1 },
    { type: "lied-angelegt", entwurfId: E, anfrageId: 1, liedId: "lied-9" },
  );
}

test("drop, bearbeiten, speichern, hochladen: alle Dateien erfolgreich", () => {
  let z = hochladend();
  assert.equal(z.phase, "speichern");
  for (const [i, d] of AVE.entries()) {
    z = lauf(
      z,
      { type: "upload-gestartet", entwurfId: E, dateiId: d.id, versuch: 1 },
      {
        type: "upload-fortschritt",
        entwurfId: E,
        dateiId: d.id,
        versuch: 1,
        bytes: 50,
        gesamt: d.sizeBytes,
      },
    );
    assert.equal(z.phase, "speichern");
    z = reduziere(z, {
      type: "upload-erfolgreich",
      entwurfId: E,
      dateiId: d.id,
      versuch: 1,
      serverDateiId: `s${i}`,
    });
  }
  assert.equal(z.phase, "gespeichert");
  assert.equal(
    (z as Extract<Zustand, { phase: "gespeichert" }>).liedId,
    "lied-9",
  );
  assert.deepEqual(fehlgeschlagene(z), []);
});

test("Fortschritt wird je Datei geführt", () => {
  const z = lauf(
    hochladend(),
    { type: "upload-gestartet", entwurfId: E, dateiId: "d1", versuch: 1 },
    {
      type: "upload-fortschritt",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      bytes: 40,
      gesamt: 100,
    },
  );
  assert.deepEqual(
    (z as Extract<Zustand, { phase: "speichern" }>).dateien[0].upload,
    { status: "laedt", versuch: 1, bytes: 40, gesamt: 100 },
  );
});

test("eine fehlgeschlagene Datei bleibt sichtbar, die anderen sind gespeichert", () => {
  let z = hochladend();
  for (const d of AVE) {
    z = reduziere(z, {
      type: "upload-gestartet",
      entwurfId: E,
      dateiId: d.id,
      versuch: 1,
    });
  }
  z = lauf(
    z,
    {
      type: "upload-fehlgeschlagen",
      entwurfId: E,
      dateiId: "d2",
      versuch: 1,
      meldung: "Netz weg",
    },
    {
      type: "upload-erfolgreich",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      serverDateiId: "s1",
    },
    {
      type: "upload-erfolgreich",
      entwurfId: E,
      dateiId: "d3",
      versuch: 1,
      serverDateiId: "s3",
    },
  );
  assert.equal(z.phase, "gespeichert");
  assert.deepEqual(
    fehlgeschlagene(z).map((d) => [d.id, d.meldung]),
    [["d2", "Netz weg"]],
  );
});

test("scheitert das Anlegen des Liedes, bleibt der Entwurf erhalten", () => {
  const z = inSammeln(
    lauf(
      anfangszustand(E),
      abgelegt(),
      { type: "speichern-angefordert", entwurfId: E, anfrageId: 1 },
      {
        type: "lied-anlegen-fehlgeschlagen",
        entwurfId: E,
        anfrageId: 1,
        meldung: "Server sagt nein",
      },
    ),
  );
  assert.equal(z.fehler, "Server sagt nein");
  assert.equal(z.lied.title, "Ave Maria");
  assert.equal(z.dateien.length, 3);
  assert.equal(kannSpeichern(z), true);
});

test("während des Speicherns sind Änderungen und zweites Speichern wirkungslos", () => {
  const z = hochladend();
  assert.equal(
    reduziere(z, {
      type: "lied-feld-geaendert",
      entwurfId: E,
      feld: "title",
      wert: "Anders",
    }),
    z,
  );
  assert.equal(
    reduziere(z, { type: "speichern-angefordert", entwurfId: E, anfrageId: 2 }),
    z,
  );
});

test("Ergebnisse eines verworfenen Entwurfs ändern nichts", () => {
  const verworfen = reduziere(hochladend(), {
    type: "entwurf-verworfen",
    entwurfId: E,
  });
  assert.equal(verworfen.phase, "verworfen");
  for (const spaet of [
    { type: "lied-angelegt", entwurfId: E, anfrageId: 1, liedId: "x" },
    { type: "upload-gestartet", entwurfId: E, dateiId: "d1", versuch: 1 },
    {
      type: "upload-erfolgreich",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      serverDateiId: "s",
    },
    {
      type: "upload-fehlgeschlagen",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      meldung: "m",
    },
  ] satisfies Ereignis[]) {
    assert.equal(reduziere(verworfen, spaet), verworfen);
  }
});

test("Ergebnisse eines ersetzten Entwurfs betreffen den neuen nicht", () => {
  const neu = reduziere(hochladend(), {
    type: "entwurf-begonnen",
    entwurfId: "entwurf-2",
  });
  assert.deepEqual(neu, anfangszustand("entwurf-2"));
  const nachher = reduziere(neu, {
    type: "upload-erfolgreich",
    entwurfId: E,
    dateiId: "d1",
    versuch: 1,
    serverDateiId: "s",
  });
  assert.equal(nachher, neu);
  assert.equal(
    reduziere(neu, abgelegt()),
    neu,
    "Ablegen für den alten Entwurf wird ignoriert",
  );
});

test("ein überholter Versuch wird ignoriert", () => {
  let z = hochladend();
  z = lauf(
    z,
    { type: "upload-gestartet", entwurfId: E, dateiId: "d1", versuch: 1 },
    {
      type: "upload-fehlgeschlagen",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      meldung: "zuerst",
    },
    { type: "upload-gestartet", entwurfId: E, dateiId: "d1", versuch: 2 },
  );
  const vorher = z;
  for (const alt of [
    {
      type: "upload-erfolgreich",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      serverDateiId: "alt",
    },
    {
      type: "upload-fehlgeschlagen",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      meldung: "alt",
    },
    {
      type: "upload-fortschritt",
      entwurfId: E,
      dateiId: "d1",
      versuch: 1,
      bytes: 1,
      gesamt: 2,
    },
  ] satisfies Ereignis[]) {
    assert.equal(reduziere(z, alt), vorher);
  }
});

test("ein Ergebnis einer überholten Speicheranfrage wird ignoriert", () => {
  const z = lauf(anfangszustand(E), abgelegt(), {
    type: "speichern-angefordert",
    entwurfId: E,
    anfrageId: 2,
  });
  assert.equal(
    reduziere(z, {
      type: "lied-angelegt",
      entwurfId: E,
      anfrageId: 1,
      liedId: "alt",
    }),
    z,
  );
  assert.equal(
    reduziere(z, {
      type: "lied-anlegen-fehlgeschlagen",
      entwurfId: E,
      anfrageId: 1,
      meldung: "alt",
    }),
    z,
  );
});
