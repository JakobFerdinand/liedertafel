import Link from "next/link";
import type { ReactNode } from "react";
import type { ChatQuelle } from "@/lib/chat";

function quellenschluessel(text: string) {
  // Deutscher CatalogueText.Fold-Abgleich, zusätzlich ohne typografische
  // Anführungszeichen, Satzzeichen und Leerraum rund um den Liedtitel.
  return text
    .normalize("NFC")
    .toLowerCase()
    .replace(/ä/g, "ae")
    .replace(/ö/g, "oe")
    .replace(/ü/g, "ue")
    .replace(/ß/g, "ss")
    .replace(/ae/g, "a")
    .replace(/oe/g, "o")
    .replace(/ue/g, "u")
    .replace(/[^\p{L}\p{N}]/gu, "");
}

// Bewusst kleiner Text-Renderer: Absätze, Listen und Hervorhebungen, kein HTML
// und keine vom Modell erzeugten URLs. Unbelegte Quellenmarker bleiben sichtbar.
function textMitQuellen(text: string, quellen: ChatQuelle[]): ReactNode[] {
  const teile: ReactNode[] = [];
  const muster = /\[Quelle:[ \t]*|\*\*([^*\n]+)\*\*/g;
  let position = 0;
  for (
    let treffer = muster.exec(text);
    treffer !== null;
    treffer = muster.exec(text)
  ) {
    const index = treffer.index;
    teile.push(text.slice(position, index));
    if (!treffer[1]) {
      const anfang = muster.lastIndex;
      let ende = anfang;
      let klammern = 1;
      // Ein Titel darf selbst eckige Klammern enthalten, z. B. [SATB].
      while (ende < text.length && text[ende] !== "\n") {
        if (text[ende] === "[") klammern++;
        if (text[ende] === "]") klammern--;
        if (klammern === 0) break;
        ende++;
      }
      if (klammern !== 0) {
        teile.push(treffer[0]);
        position = anfang;
        continue;
      }
      const schluessel = quellenschluessel(text.slice(anfang, ende));
      const passendeQuellen = quellen.filter(
        (eintrag) =>
          schluessel !== "" && quellenschluessel(eintrag.label) === schluessel,
      );
      // Mehrdeutige oder unbekannte Marker bleiben lesbarer Originaltext.
      const quelle =
        passendeQuellen.length === 1 ? passendeQuellen[0] : undefined;
      teile.push(
        quelle ? (
          <Link
            key={index}
            className="chat-quellen-verweis"
            href={`/lied/?id=${encodeURIComponent(quelle.id)}`}
            aria-label={`Quelle öffnen: ${quelle.label}`}
            title={quelle.label}
          >
            [{quellen.indexOf(quelle) + 1}]
          </Link>
        ) : (
          text.slice(index, ende + 1)
        ),
      );
      muster.lastIndex = ende + 1;
    } else {
      teile.push(<strong key={index}>{treffer[1]}</strong>);
    }
    position = muster.lastIndex;
  }
  teile.push(text.slice(position));
  return teile;
}

export function ChatAntwort({
  inhalt,
  quellen = [],
}: {
  inhalt: string;
  quellen?: ChatQuelle[];
}) {
  const bloecke: ReactNode[] = [];
  const zeilen = inhalt.split("\n");
  let position = 0;
  while (position < zeilen.length) {
    const start = position;
    const zeile = zeilen[position];
    if (!zeile.trim()) {
      position++;
      continue;
    }
    const liste = /^\s*(?:[-*]|\d+\.)\s+/.test(zeile);
    const absatz: string[] = [];
    while (
      position < zeilen.length &&
      zeilen[position].trim() &&
      /^\s*(?:[-*]|\d+\.)\s+/.test(zeilen[position]) === liste
    ) {
      absatz.push(zeilen[position++]);
    }
    if (liste) {
      const List = /^\s*\d+\./.test(zeile) ? "ol" : "ul";
      bloecke.push(
        <List key={start}>
          {absatz.map((text, index) => (
            <li key={`${start + index}`}>
              {textMitQuellen(
                text.replace(/^\s*(?:[-*]|\d+\.)\s+/, ""),
                quellen,
              )}
            </li>
          ))}
        </List>,
      );
    } else {
      bloecke.push(
        <p key={start}>{textMitQuellen(absatz.join("\n"), quellen)}</p>,
      );
    }
  }
  return <div className="chat-antwort-text">{bloecke}</div>;
}
