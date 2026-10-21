"use client";

// Aufnahmen einzelner Lieder, die diesen Auftritt nennen (#160). Sie stehen
// auch in der Historie des Liedes; abgespielt und heruntergeladen wird auf
// der Liedseite, zu der jede Zeile führt.

import Link from "next/link";
import { type AuftrittLiedAufnahme, liedUrl } from "@/lib/events";

export function AuftrittLiedAufnahmen({
  aufnahmen,
}: {
  aufnahmen: AuftrittLiedAufnahme[];
}) {
  if (aufnahmen.length === 0) return null;
  return (
    <section
      className="auftritt-abschnitt auftritt-lied-aufnahmen"
      aria-labelledby="auftritt-lied-aufnahmen-titel"
    >
      <h3 id="auftritt-lied-aufnahmen-titel">Aufnahmen einzelner Lieder</h3>
      <ul className="programm-liste">
        {aufnahmen.map((aufnahme) => (
          <li className="programm-eintrag" key={aufnahme.id}>
            <p className="programm-titel">
              <Link href={liedUrl(aufnahme.liedId)}>{aufnahme.liedTitle}</Link>
            </p>
            <p className="noten-info">
              {aufnahme.description ?? aufnahme.fileName ?? "Aufnahme"}
            </p>
          </li>
        ))}
      </ul>
    </section>
  );
}
