/**
 * Abhängigkeitsfreier Leser für Standard-MIDI-Dateien (SMF). Alle Noten werden
 * auf eine Sekunden-Zeitbasis übertragen, damit der Player Tempowechsel nicht
 * selbst nachrechnen muss.
 */

/** Geworfener Fehler für nicht lesbare oder nicht unterstützte MIDI-Dateien. */
export class MidiFehler extends Error {}

export type MidiNote = {
  /** Start in Sekunden auf der Zeitbasis der Datei (geschriebenes Tempo). */
  start: number;
  /** Klingdauer in Sekunden auf derselben Zeitbasis; nie negativ. */
  dauer: number;
  /** MIDI-Tonnummer 0–127. */
  ton: number;
  /** Anschlagstärke 0–1 (Velocity/127). */
  staerke: number;
};

export type MidiStueck = {
  /** Alle Noten aufsteigend nach Start sortiert. */
  noten: MidiNote[];
  /** Stücklänge in Sekunden (Ende der letzten Note, mindestens > 0 wenn Noten existieren). */
  dauer: number;
};

/** Voreingestelltes Tempo vor dem ersten Set-Tempo-Ereignis: 120 BPM. */
const standardTempoMikro = 500000;

/** Obergrenze für die Abspiellänge; beschädigte Dateien bleiben so begrenzt. */
const maxDauerSekunden = 4 * 60 * 60;

type RohNote = {
  ton: number;
  startTick: number;
  endTick: number;
  staerke: number;
};

type OffeneNote = {
  ton: number;
  startTick: number;
  staerke: number;
};

type TempoWechsel = {
  tick: number;
  mikroProViertel: number;
};

type TempoAbschnitt = {
  abTick: number;
  mikroProViertel: number;
  startSekunden: number;
};

type LesePosition = {
  bytes: Uint8Array;
  position: number;
};

function pruefeKennung(
  bytes: Uint8Array,
  offset: number,
  kennung: string,
): boolean {
  for (let index = 0; index < kennung.length; index += 1) {
    if (bytes[offset + index] !== kennung.charCodeAt(index)) return false;
  }
  return true;
}

function leseByte(pos: LesePosition): number {
  if (pos.position >= pos.bytes.length) {
    throw new MidiFehler("Unerwartetes Dateiende im MIDI-Ereignis.");
  }
  const byte = pos.bytes[pos.position];
  pos.position += 1;
  return byte;
}

function leseLaufzeit(pos: LesePosition): number {
  let wert = 0;
  for (let schritt = 0; schritt < 4; schritt += 1) {
    const byte = leseByte(pos);
    wert = wert * 128 + (byte & 0x7f);
    if ((byte & 0x80) === 0) return wert;
  }
  throw new MidiFehler("Laufzeitwert ist länger als vier Bytes.");
}

function verarbeiteMeta(
  pos: LesePosition,
  tick: number,
  tempoWechsel: TempoWechsel[],
): void {
  const typ = leseByte(pos);
  const laenge = leseLaufzeit(pos);
  if (laenge > pos.bytes.length - pos.position) {
    throw new MidiFehler("Meta-Ereignis reicht über das Spurende hinaus.");
  }
  if (typ === 0x51) {
    if (laenge !== 3) {
      throw new MidiFehler("Set-Tempo-Ereignis mit ungültiger Länge.");
    }
    const mikroProViertel =
      (pos.bytes[pos.position] << 16) |
      (pos.bytes[pos.position + 1] << 8) |
      pos.bytes[pos.position + 2];
    if (mikroProViertel === 0) {
      throw new MidiFehler("Set-Tempo-Ereignis mit Tempo null.");
    }
    tempoWechsel.push({ tick, mikroProViertel });
  }
  pos.position += laenge;
}

function liesSpur(
  daten: Uint8Array,
  tempoWechsel: TempoWechsel[],
  rohNoten: RohNote[],
): void {
  const pos: LesePosition = { bytes: daten, position: 0 };
  let tick = 0;
  // Laufender Status gilt nur für Kanalereignisse; Meta/SysEx setzen ihn zurück.
  let laufenderStatus = 0;
  // Stapel je (Kanal, Tonhöhe), damit Wiederanschläge eigene Noten bekommen.
  const aktiv = new Map<string, OffeneNote[]>();

  while (pos.position < daten.length) {
    tick += leseLaufzeit(pos);
    // Erst nachsehen: Ohne laufenden Status wird das Statusbyte verbraucht,
    // mit laufendem Status bleibt das Datenbyte für die Auswertung stehen.
    let status = daten[pos.position];
    if (status < 0x80) {
      if (laufenderStatus === 0) {
        throw new MidiFehler("Datenbyte ohne laufenden Status.");
      }
      status = laufenderStatus;
    } else {
      pos.position += 1;
      if (status === 0xff) {
        laufenderStatus = 0;
        verarbeiteMeta(pos, tick, tempoWechsel);
        continue;
      }
      if (status === 0xf0 || status === 0xf7) {
        laufenderStatus = 0;
        const laenge = leseLaufzeit(pos);
        if (laenge > daten.length - pos.position) {
          throw new MidiFehler(
            "Systemexklusives Ereignis reicht über das Spurende hinaus.",
          );
        }
        pos.position += laenge;
        continue;
      }
      if (status >= 0xf1) {
        throw new MidiFehler("Unbekanntes MIDI-Ereignis in der Spur.");
      }
      laufenderStatus = status;
    }

    const kanal = status & 0x0f;
    const art = status & 0xf0;
    const datenBytes = art === 0xc0 || art === 0xd0 ? 1 : 2;
    if (daten.length - pos.position < datenBytes) {
      throw new MidiFehler("Kanalereignis reicht über das Spurende hinaus.");
    }
    const erstes = daten[pos.position];
    const zweites = datenBytes === 2 ? daten[pos.position + 1] : 0;
    if (erstes > 0x7f || zweites > 0x7f) {
      throw new MidiFehler("Ungültiges Datenbyte im Kanalereignis.");
    }
    pos.position += datenBytes;
    const schluessel = `${kanal}:${erstes}`;

    if (art === 0x90 && zweites > 0) {
      const stapel = aktiv.get(schluessel) ?? [];
      stapel.push({
        ton: erstes,
        startTick: tick,
        staerke: Math.min(1, zweites / 127),
      });
      aktiv.set(schluessel, stapel);
    } else if (art === 0x90 || art === 0x80) {
      // Note-an mit Anschlag null zählt als Note-aus; ältester Anschlag zuerst.
      const beginn = aktiv.get(schluessel)?.shift();
      if (beginn) {
        rohNoten.push({
          ton: beginn.ton,
          startTick: beginn.startTick,
          endTick: tick,
          staerke: beginn.staerke,
        });
      }
    }
  }

  // Ohne Gegenstück gebliebene Anschläge laufen bis zum Spurende.
  for (const stapel of aktiv.values()) {
    for (const beginn of stapel) {
      rohNoten.push({
        ton: beginn.ton,
        startTick: beginn.startTick,
        endTick: tick,
        staerke: beginn.staerke,
      });
    }
  }
}

function erstelleZeitbasis(
  abschnitte: TempoAbschnitt[],
  division: number,
): (tick: number) => number {
  return (tick) => {
    let index = abschnitte.length - 1;
    while (index >= 0 && abschnitte[index].abTick > tick) {
      index -= 1;
    }
    if (index < 0) {
      return (tick * standardTempoMikro) / division / 1000000;
    }
    const abschnitt = abschnitte[index];
    return (
      abschnitt.startSekunden +
      ((tick - abschnitt.abTick) * abschnitt.mikroProViertel) /
        division /
        1000000
    );
  };
}

/** Liest eine Standard-MIDI-Datei (SMF) und überträgt die Noten auf eine Sekunden-Zeitbasis. */
export function liesMidi(puffer: ArrayBuffer): MidiStueck {
  const bytes = new Uint8Array(puffer);
  const ansicht = new DataView(puffer);
  if (bytes.length < 14 || !pruefeKennung(bytes, 0, "MThd")) {
    throw new MidiFehler("Die Datei beginnt nicht mit einem MIDI-Kopf.");
  }
  if (ansicht.getUint32(4, false) !== 6) {
    throw new MidiFehler("Unerwartete Länge im MIDI-Kopf.");
  }
  const format = ansicht.getUint16(8, false);
  if (format !== 0 && format !== 1) {
    throw new MidiFehler("Nicht unterstütztes MIDI-Format.");
  }
  const spurAnzahl = ansicht.getUint16(10, false);
  if (format === 0 && spurAnzahl !== 1) {
    throw new MidiFehler("Format 0 erlaubt nur eine Spur.");
  }
  const division = ansicht.getUint16(12, false);
  if ((division & 0x8000) !== 0) {
    throw new MidiFehler("Nicht unterstützte Zeitbasis (SMPTE).");
  }
  if (division === 0) {
    throw new MidiFehler("Ungültige Zeiteinteilung von null.");
  }

  const tempoWechsel: TempoWechsel[] = [];
  const rohNoten: RohNote[] = [];
  let position = 14;
  while (position < bytes.length) {
    if (bytes.length - position < 8) {
      throw new MidiFehler("Abgebrochener Block am Dateiende.");
    }
    const laenge = ansicht.getUint32(position + 4, false);
    if (laenge > bytes.length - position - 8) {
      throw new MidiFehler("Blocklänge überschreitet die Dateigröße.");
    }
    if (pruefeKennung(bytes, position, "MTrk")) {
      liesSpur(
        bytes.subarray(position + 8, position + 8 + laenge),
        tempoWechsel,
        rohNoten,
      );
    }
    // Fremde Blöcke enthalten keine Noten und werden nur übersprungen.
    position += 8 + laenge;
  }

  // Tempo-Abschnitte chronologisch aufbauen; bei gleichem Tick gewinnt der letzte.
  tempoWechsel.sort((a, b) => a.tick - b.tick);
  const abschnitte: TempoAbschnitt[] = [];
  let mikroProViertel = standardTempoMikro;
  let startSekunden = 0;
  let basisTick = 0;
  for (const wechsel of tempoWechsel) {
    startSekunden +=
      ((wechsel.tick - basisTick) * mikroProViertel) / division / 1000000;
    basisTick = wechsel.tick;
    mikroProViertel = wechsel.mikroProViertel;
    abschnitte.push({
      abTick: basisTick,
      mikroProViertel,
      startSekunden,
    });
  }
  const tickZuSekunden = erstelleZeitbasis(abschnitte, division);

  const noten: MidiNote[] = rohNoten
    .map((roh) => {
      const start = tickZuSekunden(roh.startTick);
      const ende = tickZuSekunden(roh.endTick);
      return {
        start,
        dauer: Math.max(0, ende - start),
        ton: roh.ton,
        staerke: roh.staerke,
      };
    })
    .sort((a, b) => a.start - b.start);

  let dauer = 0;
  for (const note of noten) {
    dauer = Math.max(dauer, note.start + note.dauer);
  }
  if (dauer > maxDauerSekunden) {
    throw new MidiFehler(
      "Die MIDI-Datei ergibt eine Abspiellänge über vier Stunden.",
    );
  }
  return { noten, dauer };
}
