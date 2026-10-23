// Der Sammel-Import (#143): ein Ordner (Drive-Export) wird zu einem Plan von
// Vorschlägen — ein Unterordner je Lied, die Dateien werden wie bei der Ablage
// eines einzelnen Liedes (#159) gelesen und gespeichert.
//
// Die Regeln kommen aus der Inventur (#98,
// tests/archive/fixtures/drive-inventur/mapping.json): leere Ordner
// importieren nichts, Verknüpfungen auf denselben Ordner zählen einmal,
// ungelöste Doppel bleiben ungerichtet nebeneinander, und Kandidaten wie
// .xls und .capx werden nie automatisch importiert. Klassifiziert wird mit
// demselben Datennamen-Leser wie bei der Ablage (lied-entwurf/dateinamen).
//
// Rein und ohne Netz: der Plan entscheidet nichts über Uploads oder Lieder.

import { ausDateiname } from "./lied-entwurf/dateinamen.ts";

/** Eine Datei des Falls: Name, Pfad relativ zur Exportansicht und Größe. */
export type SammelDateienEingabe = {
  name: string;
  pfad: string;
  sizeBytes: number;
};

/** Eine Datei im Plan, mit dem, was aus ihrem Namen lesbar ist. */
export type SammelPlanDatei = {
  name: string;
  /** Der Pfad des Datei, ohne den gewählten Wurzelordner. */
  pfad: string;
  sizeBytes: number;
  art: ReturnType<typeof ausDateiname>["art"];
  stimme: string;
  /** true: der Name nennt keine Stimme; der Wert bleibt editorisch zu prüfen. */
  stimmeUnbekannt: boolean;
};

/** Ein vorgeschlagener Lied-Eintrag: Ordner, Titel und seine Dateien. */
export type SammelGruppe = {
  /** Der Ordnername; "" für lose Dateien an der Wurzel des gewählten Ordners. */
  ordner: string;
  /** Der Pfad des Ordners nach dem Wegfallen des gewählten Wurzelordners. */
  ordnerKey: string;
  /** Vom Ordnerinhalt bestimmte Entwurf-Id: dasselbe Angebot, dieselbe Id. */
  entwurfId: string;
  titel: string;
  dateien: SammelPlanDatei[];
};

/** Ein Plan: die Vorschläge und das, was der Import nicht automatisch anfasst. */
export type SammelPlan = {
  gruppen: SammelGruppe[];
  /** Dateien für die separate Prüfung (.xls, .capx): nie automatisch importiert. */
  separatPruefen: { name: string; ordner: string }[];
  /** Verknüpfungen, deren Ziel nicht im Export liegt. */
  verknuepfungen: { name: string; ziel: string }[];
};

/** Dateien, die für die separate Prüfung reserviert sind (#98). */
const separatGeprueft = new Set(["xls", "xlsx", "capx"]);

/**
 * Unterordner mit vorangestellter Nummer gliedern Materialien ("1. Noten",
 * "2. Media", "3. Notierprogrammdaten" der Inventur), keine Lieder: sie
 * sammeln, statt den Ordner zu teilen.
 */
function materialsOrdner(name: string): boolean {
  return /^\d{1,2}\.\s/.test(name);
}

/** Ansichts-Anhänge wie .gitkeep oder .DS_Store gehören nicht in den Bestand. */
function anhangDatei(name: string): boolean {
  return name.startsWith(".");
}

/** Eine Verknüpfung einer Drive-Exportansicht: "Verknüpfung - Ziel.url". */
function verknuepfungsZiel(name: string): string | null {
  if (!/\.(url|webloc)$/i.test(name)) return null;
  const ohneEndung = name.replace(/\.(url|webloc)$/i, "");
  return ohneEndung.replace(/^Verknüpfung - ?/, "");
}

function teile(pfad: string): string[] {
  return pfad.split("/").filter(Boolean);
}

/** Ein Inhaltsschlüssel als UUID; dasselbe Angebot, dieselbe Id. */
function idAusHash(hex: string): string {
  const teil = hex.slice(0, 32).padEnd(32, "0");
  return [
    teil.slice(0, 8),
    teil.slice(8, 12),
    `4${teil.slice(13, 16)}`,
    `8${teil.slice(17, 20)}`,
    teil.slice(20, 32),
  ].join("-");
}

async function inhaltId(
  ordner: string,
  dateien: SammelDateienEingabe[],
): Promise<string> {
  const liste = dateien
    .map((d) => `${d.name}\u0000${d.sizeBytes}`)
    .sort()
    .join("\n");
  const quelle = new TextEncoder().encode(`${ordner}\u0000${liste}`);
  const abdruck = new Uint8Array(await crypto.subtle.digest("SHA-256", quelle));
  const hex = Array.from(abdruck)
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
  return idAusHash(hex);
}

/** Der Sammel-Plan für die anliegenden Dateien. */
export async function gruppiereImport(
  dateien: SammelDateienEingabe[],
): Promise<SammelPlan> {
  const produkt = dateien.filter((d) => !anhangDatei(d.name));
  const istSeparat = (d: SammelDateienEingabe) =>
    separatGeprueft.has((d.name.split(".").at(-1) ?? "").toLowerCase());
  const istVerknuepfung = (d: SammelDateienEingabe) =>
    verknuepfungsZiel(d.name) !== null;
  const separat = produkt.filter(istSeparat);
  const verknuepfungDateien = produkt.filter(istVerknuepfung);
  const material = produkt.filter((d) => !istSeparat(d) && !istVerknuepfung(d));

  // Gliederung: der gewählte Wurzelordner fällt weg, bis sich die Unterordner
  // teilen oder die Unterordner Materialien gliedern ("1. Noten"). Sämtliche
  // Dateien — auch die separat zu prüfenden und die Verknüpfungen — tragen
  // zur Struktur bei.
  const aufgeschluesselt = produkt.map((d) => ({
    datei: d,
    stufen: teile(d.pfad),
  }));
  while (true) {
    const ersteStufe = new Set(aufgeschluesselt.map((d) => d.stufen[0] ?? ""));
    // Mehrere Ordner auf einer Stufe: hier trennen sich die Lieder.
    if (ersteStufe.size > 1) break;
    // Nur noch lose Dateien: die Wurzel ist der Vorschlag selbst.
    if (aufgeschluesselt.every((d) => d.stufen.length <= 1)) break;
    // Unterordner sind die zweiten Stufen, die noch tiefer gehen.
    const unterordner = new Set(
      aufgeschluesselt
        .filter((d) => d.stufen.length >= 3)
        .map((d) => d.stufen[1]),
    );
    if (unterordner.size === 0) {
      // Unter der Wurzel liegen nur lose Dateien: der gewählte Ordner
      // selbst ist der Vorschlag und trägt seinen Namen.
      break;
    }
    // Teilen soll die Wurzel, wenn mehrere echte Lied-Unterordner liegen.
    const teilen =
      unterordner.size >= 2 &&
      !Array.from(unterordner).every((n) => materialsOrdner(n));
    // Ein Unterordner gliedert nur dann weiter, wenn er selbst Unterordner trägt.
    const einesTiefer =
      unterordner.size === 1 &&
      !materialsOrdner(Array.from(unterordner)[0] ?? "");
    if (!teilen && !einesTiefer) break;
    for (const d of aufgeschluesselt) d.stufen = d.stufen.slice(1);
  }

  const gruppenNachOrdnern = new Map<string, SammelDateienEingabe[]>();
  for (const { datei, stufen } of aufgeschluesselt) {
    if (!material.includes(datei)) continue;
    // Lose Dateien nach dem Wegfallen der Wurzel teilen einen Vorschlag
    // ohne Ordnername (wie die Ablage eines einzelnen Liedes).
    const ordner = stufen.length <= 1 ? "" : (stufen[0] ?? "");
    const liste = gruppenNachOrdnern.get(ordner) ?? [];
    liste.push(datei);
    gruppenNachOrdnern.set(ordner, liste);
  }

  const gruppen: SammelGruppe[] = [];
  for (const [ordner, liste] of gruppenNachOrdnern) {
    if (liste.length === 0) continue;
    const titel =
      ordner !== ""
        ? ordner
        : ausDateiname(liste[0].name).titel || liste[0].name;
    gruppen.push({
      ordner,
      ordnerKey: ordner,
      entwurfId: await inhaltId(ordner, liste),
      titel,
      dateien: liste.map((d) => {
        const gelesen = ausDateiname(d.name);
        // Stimme unbekannt: derselbe Name mit PDF-Endung ließe keine Stimme
        // lesen - der Rückfall "Alle" ist der Fall ohne erratenen Wert.
        const stimmprobe = ausDateiname(
          d.name.replace(/\.(mid|midi)$/i, ".pdf"),
        );
        const nenntStimme =
          stimmprobe.art === "noten" && stimmprobe.stimme !== "";
        return {
          name: d.name,
          pfad:
            aufgeschluesselt.find((a) => a.datei === d)?.stufen.join("/") ??
            d.pfad,
          sizeBytes: d.sizeBytes,
          art: gelesen.art,
          stimme: gelesen.stimme,
          stimmeUnbekannt:
            gelesen.art === "uebe" && /\.(mid|midi)$/i.test(d.name)
              ? !nenntStimme
              : false,
        };
      }),
    });
  }

  // Die Verknüpfung "Verknüpfung - Sample A.url" zählt zum Ordner "Sample A"
  // nur einmal; ohne Ziel im Export bleibt sie ungelöst gemeldet.
  const verknuepft = verknuepfungDateien.flatMap((d) => {
    const ziel = verknuepfungsZiel(d.name);
    return ziel === null ? [] : [{ name: d.name, ziel }];
  });
  const ungelöst = verknuepft.filter(
    (v) => !gruppen.some((g) => g.ordner === v.ziel),
  );

  gruppen.sort((a, b) =>
    a.ordner === b.ordner
      ? a.titel.localeCompare(b.titel, "de")
      : a.ordner.localeCompare(b.ordner, "de"),
  );

  return {
    gruppen,
    separatPruefen: separat.map((d) => ({
      name: d.name,
      ordner:
        aufgeschluesselt
          .find((a) => a.datei === d)
          ?.stufen.slice(0, -1)
          .at(-1) ?? "",
    })),
    verknuepfungen: ungelöst,
  };
}
