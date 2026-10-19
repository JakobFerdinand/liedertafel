import assert from "node:assert/strict";
import { test } from "node:test";
import {
  alsGespeichert,
  type Gespeichert,
  type GespeicherteDatei,
  type GespeicherteLesung,
  wiederhergestellt,
} from "./gespeichert.ts";
import {
  anfangszustand,
  type Ereignis,
  kannSpeichern,
  reduziere,
  type Zustand,
} from "./reducer.ts";

// Reine Folgen: das Wiederherstellen eines gespeicherten Entwurfs ist der
// Anfangszustand desselben Reducers (#178).

const E = "entwurf-1";

function lauf(start: Zustand, ...ereignisse: Ereignis[]): Zustand {
  return ereignisse.reduce(reduziere, start);
}

function sammeln(zustand: Zustand) {
  assert.equal(zustand.phase, "sammeln");
  return zustand as Extract<Zustand, { phase: "sammeln" }>;
}

const datei = (extra: Partial<GespeicherteDatei> = {}): GespeicherteDatei => ({
  id: "d1",
  name: "Ave_Maria.pdf",
  sizeBytes: 200,
  art: "partitur",
  stimme: "",
  artBearbeitet: false,
  stimmeBearbeitet: false,
  lesen: { status: "liest", anfrageId: 1, meldung: "" },
  lesung: null,
  ...extra,
});

const entwurf = (
  dateien: GespeicherteDatei[] = [datei()],
  extra: Partial<Gespeichert> = {},
): Gespeichert => ({
  entwurfId: E,
  lied: {
    title: "Ave Maria",
    composer: "",
    arranger: "",
    lyricist: "",
    voiceConfiguration: "",
    musicalKey: "",
    language: "",
    lyrics: "",
  },
  bearbeitet: [],
  vorschlaege: {},
  dateien,
  ...extra,
});

const laeuftNoch: GespeicherteLesung = {
  anfrageId: 1,
  status: "liest",
  meldung: "",
  felder: [],
};

const gelesen = (
  felder: GespeicherteLesung["felder"],
  anfrageId = 1,
): GespeicherteLesung => ({
  anfrageId,
  status: "gelesen",
  meldung: "",
  felder,
});

test("der gespeicherte Entwurf ist der Anfangszustand: Felder, Bearbeitungen und Dateien", () => {
  const { zustand } = wiederhergestellt(
    entwurf(
      [datei({ stimmeBearbeitet: true, stimme: "Tenor 1", art: "uebe" })],
      {
        lied: { ...entwurf().lied, composer: "Bruckner" },
        bearbeitet: ["composer"],
        vorschlaege: { title: "unsicher" },
      },
    ),
  );
  const z = sammeln(zustand);
  assert.equal(z.entwurfId, E);
  assert.equal(z.lied.composer, "Bruckner");
  assert.equal(z.bearbeitet.composer, true);
  assert.equal(z.bearbeitet.title, false);
  assert.equal(z.vorschlaege.title, "unsicher");
  assert.equal(z.dateien[0].art, "uebe");
  assert.equal(z.dateien[0].bearbeitet.stimme, true);
  assert.equal(z.dateien[0].upload.status, "wartend");
});

test("wiederhergestellte Dateien haben ihren Inhalt nicht, Speichern wartet darauf", () => {
  const { zustand } = wiederhergestellt(
    entwurf([datei({ lesen: { status: "keine", anfrageId: 0, meldung: "" } })]),
  );
  const z = sammeln(zustand);
  assert.equal(z.dateien[0].inhalt, "fehlt");
  assert.equal(kannSpeichern(z), false);
  const da = lauf(z, {
    type: "datei-nachgereicht",
    entwurfId: E,
    dateiId: "d1",
  });
  assert.equal(sammeln(da).dateien[0].inhalt, "vorhanden");
  assert.equal(kannSpeichern(da), true);
});

test("eine fehlende Datei lässt sich entfernen", () => {
  const { zustand } = wiederhergestellt(entwurf());
  const z = sammeln(
    lauf(zustand, { type: "datei-entfernt", entwurfId: E, dateiId: "d1" }),
  );
  assert.equal(z.dateien.length, 0);
});

test("ein Lesen, das während der Abwesenheit fertig wurde, kommt als Ereignis und füllt nur Unberührtes", () => {
  const g = entwurf(
    [
      datei({
        lesung: gelesen([
          { feld: "composer", wert: "Anton Bruckner", sicher: true },
          { feld: "language", wert: "Latein", sicher: false },
        ]),
      }),
    ],
    { bearbeitet: ["composer"] },
  );
  const { zustand, ereignisse, laeuft } = wiederhergestellt(g);
  assert.deepEqual(laeuft, []);
  const z = sammeln(lauf(zustand, ...ereignisse));
  // Die Handarbeit (auch das Leeren) bleibt, Unberührtes wird Vorschlag.
  assert.equal(z.lied.composer, "");
  assert.equal(z.lied.language, "Latein");
  assert.equal(z.vorschlaege.language, "unsicher");
  assert.equal(z.dateien[0].lesen.status, "gelesen");
});

test("ein Lesen, das noch läuft, bleibt 'liest' und wird zur Abfrage gemeldet", () => {
  const { zustand, ereignisse, laeuft } = wiederhergestellt(
    entwurf([datei({ lesung: laeuftNoch })]),
  );
  assert.deepEqual(laeuft, ["d1"]);
  assert.deepEqual(ereignisse, []);
  assert.equal(sammeln(zustand).dateien[0].lesen.status, "liest");
});

test("ein späteres Ergebnis nach dem Wiederherstellen geht durch denselben Reducer", () => {
  const { zustand } = wiederhergestellt(
    entwurf([datei({ lesung: laeuftNoch })]),
  );
  const spaet: Ereignis = {
    type: "lesen-ergebnis",
    entwurfId: E,
    dateiId: "d1",
    anfrageId: 1,
    felder: [{ feld: "composer", wert: "Bruckner", sicher: true }],
  };
  const z = sammeln(lauf(zustand, spaet));
  assert.equal(z.lied.composer, "Bruckner");
  // Dasselbe Ergebnis noch einmal ändert nichts.
  assert.equal(lauf(z, spaet), z);
});

test("ein Lesen ohne Spur auf dem Server gilt als unterbrochen und lässt sich wiederholen", () => {
  const { zustand, ereignisse, laeuft } = wiederhergestellt(entwurf());
  assert.deepEqual(laeuft, []);
  const z = sammeln(lauf(zustand, ...ereignisse));
  assert.equal(z.dateien[0].lesen.status, "fehlgeschlagen");
});

test("ein Lesen, das der Server schon neuer kennt, wird angefordert und beantwortet", () => {
  const g = entwurf([
    datei({
      lesen: { status: "keine", anfrageId: 0, meldung: "" },
      lesung: gelesen(
        [{ feld: "composer", wert: "Bruckner", sicher: true }],
        2,
      ),
    }),
  ]);
  const { zustand, ereignisse } = wiederhergestellt(g);
  const z = sammeln(lauf(zustand, ...ereignisse));
  assert.equal(z.lied.composer, "Bruckner");
  assert.deepEqual(z.dateien[0].lesen, { status: "gelesen", anfrageId: 2 });
});

test("ein Lesen, das der Server als nicht verfügbar kennt, lässt das Formular manuell", () => {
  const g = entwurf([
    datei({
      lesung: {
        anfrageId: 1,
        status: "nicht-verfuegbar",
        meldung: "Budget erreicht",
        felder: [],
      },
    }),
  ]);
  const { zustand, ereignisse } = wiederhergestellt(g);
  const z = sammeln(lauf(zustand, ...ereignisse));
  assert.equal(z.dateien[0].lesen.status, "nicht-verfuegbar");
  assert.deepEqual(z.ki, {
    status: "nicht-verfuegbar",
    meldung: "Budget erreicht",
  });
});

test("ein bereits angewendetes Lesen wird nicht noch einmal angewendet", () => {
  const g = entwurf([
    datei({
      lesen: { status: "gelesen", anfrageId: 1, meldung: "" },
      lesung: gelesen([{ feld: "composer", wert: "Bruckner", sicher: true }]),
    }),
  ]);
  const { zustand, ereignisse } = wiederhergestellt(g);
  assert.deepEqual(ereignisse, []);
  assert.equal(sammeln(zustand).lied.composer, "");
});

test("ein verworfener, wiederhergestellter Entwurf ignoriert späte Ergebnisse", () => {
  const { zustand } = wiederhergestellt(
    entwurf([datei({ lesung: laeuftNoch })]),
  );
  const verworfen = lauf(zustand, { type: "entwurf-verworfen", entwurfId: E });
  const spaet = lauf(verworfen, {
    type: "lesen-ergebnis",
    entwurfId: E,
    dateiId: "d1",
    anfrageId: 1,
    felder: [{ feld: "composer", wert: "Bruckner", sicher: true }],
  });
  assert.equal(spaet, verworfen);
  assert.equal(spaet.phase, "verworfen");
});

test("der Abzug eines Entwurfs speichert Felder, Bearbeitungen und Dateien, aber keine Herkunft", () => {
  const z = lauf(
    anfangszustand(E),
    {
      type: "dateien-abgelegt",
      entwurfId: E,
      dateien: [{ id: "d1", name: "Ave_Maria_T1.mp3", sizeBytes: 5 }],
    },
    { type: "lesen-angefordert", entwurfId: E, dateiId: "d1", anfrageId: 1 },
    {
      type: "lesen-ergebnis",
      entwurfId: E,
      dateiId: "d1",
      anfrageId: 1,
      felder: [{ feld: "composer", wert: "Bruckner", sicher: false }],
    },
    { type: "lied-feld-geaendert", entwurfId: E, feld: "lyrics", wert: "" },
  );
  const g = alsGespeichert(z);
  assert.ok(g);
  assert.equal(g.lied.composer, "Bruckner");
  assert.deepEqual(g.bearbeitet, ["lyrics"]);
  assert.equal(g.dateien[0].art, "uebe");
  assert.equal(g.dateien[0].stimme, "Tenor 1");
  assert.deepEqual(g.dateien[0].lesen, {
    status: "gelesen",
    anfrageId: 1,
    meldung: "",
  });
  // Der Abzug und die Wiederherstellung sind umkehrbar.
  const zurueck = sammeln(wiederhergestellt(g).zustand);
  assert.deepEqual(zurueck.lied, sammeln(z).lied);
  assert.deepEqual(zurueck.bearbeitet, sammeln(z).bearbeitet);
  assert.deepEqual(zurueck.vorschlaege, sammeln(z).vorschlaege);
});

test("nur ein Entwurf in Bearbeitung wird abgezogen", () => {
  assert.equal(alsGespeichert(anfangszustand(E)), null);
  assert.equal(
    alsGespeichert(
      lauf(anfangszustand(E), { type: "entwurf-verworfen", entwurfId: E }),
    ),
    null,
  );
});
