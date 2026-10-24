"use client";

import Link from "next/link";
import { AngefangenListe } from "@/components/angefangen-liste";
import { MitgliederVerwaltung } from "@/components/mitglieder-verwaltung";

// Die Verwaltungsseite bündelt die Redaktionswerkzeuge: Verweise auf die
// Werkzeuge (Papierkorb, #168), die angefangenen Lieder (#178) und die
// Mitgliederverwaltung.

export function VerwaltungStart() {
  return (
    <>
      <nav className="verwaltung-werkzeuge" aria-label="Werkzeuge">
        <ul>
          <li>
            <Link href="/verwaltung/papierkorb/">Papierkorb</Link>
          </li>
          {/* ARC-043 (#150): der KI-Kostenüberblick der Verwaltung. */}
          <li>
            <Link href="/verwaltung/ki-budget/">KI-Kosten</Link>
          </li>
        </ul>
      </nav>
      <AngefangenListe />
      <MitgliederVerwaltung />
    </>
  );
}
