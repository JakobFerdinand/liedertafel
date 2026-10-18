"use client";

import Link from "next/link";
import { MitgliederVerwaltung } from "@/components/mitglieder-verwaltung";

// Die Verwaltungsseite bündelt die Redaktionswerkzeuge: Verweise auf die
// Werkzeuge (Papierkorb, #168) und die Mitgliederverwaltung.

export function VerwaltungStart() {
  return (
    <>
      <nav className="verwaltung-werkzeuge" aria-label="Werkzeuge">
        <ul>
          <li>
            <Link href="/verwaltung/papierkorb/">Papierkorb</Link>
          </li>
        </ul>
      </nav>
      <MitgliederVerwaltung />
    </>
  );
}
