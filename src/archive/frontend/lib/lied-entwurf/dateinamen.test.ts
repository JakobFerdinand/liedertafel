import assert from "node:assert/strict";
import { test } from "node:test";
import { ausDateiname } from "./dateinamen.ts";

test("Ave_Maria_T1.mp3 ist die Übe-Datei Tenor 1 zu 'Ave Maria'", () => {
  assert.deepEqual(ausDateiname("Ave_Maria_T1.mp3"), {
    titel: "Ave Maria",
    art: "uebe",
    stimme: "Tenor 1",
  });
});

test("Kürzel, ausgeschriebene Stimmen und Nummern werden erkannt", () => {
  const faelle: [string, string][] = [
    ["Ave_Maria_B2.mid", "Bass 2"],
    ["Ave Maria - Bass 1.mp3", "Bass 1"],
    ["ave-maria-tenor2.wav", "Tenor 2"],
    ["Ave_Maria_Sopran.mp3", "Sopran"],
    ["Ave_Maria_alt.mp3", "Alt"],
    ["Ave_Maria_Alle.mp3", "Alle"],
  ];
  for (const [name, stimme] of faelle) {
    const ergebnis = ausDateiname(name);
    assert.equal(ergebnis.stimme, stimme, name);
    assert.equal(ergebnis.titel.toLowerCase(), "ave maria", name);
  }
});

test("Übe-Datei ohne Stimme gilt für Alle", () => {
  assert.deepEqual(ausDateiname("Ave Maria.mp3"), {
    titel: "Ave Maria",
    art: "uebe",
    stimme: "Alle",
  });
});

test("ein PDF ohne Stimme ist die Partitur, mit Stimme sind es Noten", () => {
  assert.deepEqual(ausDateiname("Ave_Maria.pdf"), {
    titel: "Ave Maria",
    art: "partitur",
    stimme: "",
  });
  assert.deepEqual(ausDateiname("Ave_Maria_Partitur.pdf"), {
    titel: "Ave Maria",
    art: "partitur",
    stimme: "",
  });
  assert.deepEqual(ausDateiname("Ave_Maria_T2.pdf"), {
    titel: "Ave Maria",
    art: "noten",
    stimme: "Tenor 2",
  });
});

test("Video ist eine Aufnahme, anderes Sonstiges, beide ohne Stimme", () => {
  assert.deepEqual(ausDateiname("Ave_Maria_Konzert.mp4"), {
    titel: "Ave Maria Konzert",
    art: "aufnahme",
    stimme: "",
  });
  assert.deepEqual(ausDateiname("Liedtext.txt"), {
    titel: "Liedtext",
    art: "sonstiges",
    stimme: "",
  });
});

test("ein einzelner Buchstabe ohne Nummer ist keine Stimme", () => {
  const ergebnis = ausDateiname("Ave_Maria_a.mp3");
  assert.equal(ergebnis.stimme, "Alle");
  assert.equal(ergebnis.titel, "Ave Maria a");
});

test("besteht der Name nur aus einer Stimme, bleibt er der Titel", () => {
  assert.equal(ausDateiname("T1.mp3").titel, "T1");
});

test("die Doppelmarke des Exports fällt weg, die Stimme steht", () => {
  // #98: gleiche Namen im Drive-Bestand, Export als " (1)"; die Marke belegt
  // keine Fassung.
  const ergebnis = ausDateiname("Sample C B1 (1).pdf");
  assert.deepEqual(ergebnis, {
    titel: "Sample C",
    art: "noten",
    stimme: "Bass 1",
  });
});
