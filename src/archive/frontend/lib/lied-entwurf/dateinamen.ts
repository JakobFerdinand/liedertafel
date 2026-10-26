// Was sich aus einem Dateinamen lesen lässt (#175): Titel, Art und Stimme.
// Rein und ohne Abhängigkeiten, damit der Entwurf sie ohne Browser prüft.

/** Die Art einer Datei im Entwurf; die Partitur ist hier eine eigene Art. */
export type DateiTyp = "partitur" | "noten" | "uebe" | "aufnahme" | "sonstiges";

export type AusDateiname = {
  titel: string;
  art: DateiTyp;
  stimme: string;
};

const stimmenNamen: Record<string, string> = {
  s: "Sopran",
  sopran: "Sopran",
  a: "Alt",
  alt: "Alt",
  t: "Tenor",
  tenor: "Tenor",
  b: "Bass",
  bass: "Bass",
};

const uebeEndungen = ["mp3", "m4a", "wav", "ogg", "weba", "mid", "midi"];
const aufnahmeEndungen = ["mp4", "webm"];

function stimmeAmEnde(
  teile: string[],
): { stimme: string; rest: string[] } | null {
  const letzter = (teile.at(-1) ?? "").toLowerCase();
  if (letzter === "alle" || letzter === "tutti") {
    return { stimme: "Alle", rest: teile.slice(0, -1) };
  }
  // "T1", "Tenor2": Kürzel oder Wort mit Nummer in einem Teil.
  const zusammen = /^([a-zäöü]+)([1-5])$/.exec(letzter);
  if (zusammen && stimmenNamen[zusammen[1]]) {
    return {
      stimme: `${stimmenNamen[zusammen[1]]} ${zusammen[2]}`,
      rest: teile.slice(0, -1),
    };
  }
  // "Tenor 1": Wort und Nummer getrennt.
  const vorletzter = (teile.at(-2) ?? "").toLowerCase();
  if (/^[1-5]$/.test(letzter) && stimmenNamen[vorletzter]) {
    return {
      stimme: `${stimmenNamen[vorletzter]} ${letzter}`,
      rest: teile.slice(0, -2),
    };
  }
  // Ausgeschriebene Stimme ohne Nummer; ein Buchstabe allein bleibt Text.
  if (letzter.length > 1 && stimmenNamen[letzter]) {
    return { stimme: stimmenNamen[letzter], rest: teile.slice(0, -1) };
  }
  return null;
}

export function ausDateiname(dateiname: string): AusDateiname {
  const endungTreffer = /\.([a-z0-9]+)$/i.exec(dateiname);
  const endung = endungTreffer?.[1].toLowerCase() ?? "";
  const ohneEndung = endungTreffer
    ? dateiname.slice(0, -endungTreffer[0].length)
    : dateiname;
  let teile = ohneEndung.split(/[\s_\-.]+/).filter(Boolean);

  // Der Export hängt an Doppelnamen Sammler-Marke wie " (1)" an (#98); sie
  // belegen keine Fassung und fallen weg, bevor eine Stimme am Ende gelesen
  // wird.
  while (teile.length > 1 && /^\(\d+\)$/.test(teile.at(-1) ?? "")) {
    teile = teile.slice(0, -1);
  }

  let stimme = "";
  const gefunden = stimmeAmEnde(teile);
  if (gefunden && gefunden.rest.length > 0) {
    stimme = gefunden.stimme;
    teile = gefunden.rest;
  } else if (
    teile.length > 1 &&
    ["partitur", "score"].includes((teile.at(-1) ?? "").toLowerCase())
  ) {
    teile = teile.slice(0, -1);
  }
  const titel = teile.join(" ") || ohneEndung;

  if (endung === "pdf") {
    return stimme
      ? { titel, art: "noten", stimme }
      : { titel, art: "partitur", stimme: "" };
  }
  if (uebeEndungen.includes(endung)) {
    return { titel, art: "uebe", stimme: stimme || "Alle" };
  }
  if (aufnahmeEndungen.includes(endung)) {
    return { titel, art: "aufnahme", stimme: "" };
  }
  return { titel, art: "sonstiges", stimme: "" };
}
