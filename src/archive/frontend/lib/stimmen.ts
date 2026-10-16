import { liesUploadSitzung } from "@/lib/assets";

const stimmKuerzel: Record<string, string> = {
  s: "Sopran",
  a: "Alt",
  t: "Tenor",
  b: "Bass",
};

/**
 * Stimmen zur Besetzung: TTBB ergibt Tenor 1, Tenor 2, Bass 1, Bass 2 und
 * "Alle". Mehrfach vorkommende Stimmen werden nummeriert; freier Text bleibt
 * zusätzlich möglich.
 */
export function stimmenFuerBesetzung(besetzung: string | null): string[] {
  const namen: string[] = [];
  for (const rohteil of (besetzung ?? "").split(/[\s,/+]+/)) {
    const teil = rohteil.trim();
    if (!teil) continue;
    if (/^[satb]{2,8}$/i.test(teil)) {
      for (const kuerzel of teil.toLowerCase()) {
        namen.push(stimmKuerzel[kuerzel]);
      }
    } else {
      namen.push(stimmKuerzel[teil.toLowerCase()] ?? teil);
    }
  }
  const anzahl = new Map<string, number>();
  for (const name of namen) anzahl.set(name, (anzahl.get(name) ?? 0) + 1);
  const gezaehlt = new Map<string, number>();
  const stimmen = namen.map((name) => {
    if ((anzahl.get(name) ?? 0) < 2) return name;
    const nummer = (gezaehlt.get(name) ?? 0) + 1;
    gezaehlt.set(name, nummer);
    return `${name} ${nummer}`;
  });
  return [...stimmen, "Alle"];
}

/** Stimme aus dem Dateinamen ("lied_tenor_1.pdf" ergibt "Tenor 1"), sonst leer. */
export function stimmeAusName(dateiName: string, stimmen: string[]): string {
  const normal = dateiName
    .toLowerCase()
    .replace(/\.[a-z0-9]+$/, "")
    .replace(/[^a-zäöüß0-9]+/g, " ")
    .trim();
  const treffer = stimmen
    .filter((stimme) => stimme !== "Alle")
    .find((stimme) =>
      new RegExp(`(^| )${stimme.toLowerCase()}( |$)`).test(normal),
    );
  return treffer ?? "";
}

/** Unterbrochene Übertragungen dieses Liedes aus dem lokalen Speicher. */
export function unterbrocheneUebertragungen(
  liedId: string,
): { schluessel: string; fileName: string }[] {
  const praefix = "arc-upload-";
  const liedPraefix = `${praefix}lied-${liedId}-`;
  const eintraege: { schluessel: string; fileName: string }[] = [];
  try {
    for (let i = 0; i < window.localStorage.length; i += 1) {
      const name = window.localStorage.key(i);
      if (!name?.startsWith(liedPraefix)) continue;
      const schluessel = name.slice(praefix.length);
      const eintrag = liesUploadSitzung(schluessel);
      if (eintrag) eintraege.push({ schluessel, fileName: eintrag.fileName });
    }
  } catch {
    // Ohne lokalen Speicher entfällt nur das Fortsetzen nach dem Neuladen.
  }
  return eintraege;
}
