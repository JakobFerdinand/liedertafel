"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { MitgliederVerwaltung } from "@/components/mitglieder-verwaltung";
import { fetchMe, type MeResponse } from "@/lib/auth";
import { fetchVorschlaege } from "@/lib/provenanz";

// ARC-013-1: die Verwaltungsseite bündelt die Redaktionswerkzeuge; die
// "Vorschläge"-Liste trägt den offenen Vorschlagszähler in der Navigation.

export function VerwaltungStart() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [offene, setOffene] = useState<number | null>(null);

  useEffect(() => {
    let abgebrochen = false;
    (async () => {
      const antwort = await fetchMe().catch(() => null);
      if (!antwort || abgebrochen) return;
      setMe(antwort);
      const editor =
        antwort.authenticated &&
        (antwort.roles.includes("Editor") ||
          antwort.roles.includes("Administrator"));
      if (!editor) return;
      try {
        const liste = await fetchVorschlaege();
        if (!abgebrochen) setOffene(liste.proposals?.length ?? 0);
      } catch {
        // Der Zähler bleibt ohne Zahl, falls die Liste nicht erreichbar ist.
      }
    })();
    return () => {
      abgebrochen = true;
    };
  }, []);

  const werkzeuge =
    me?.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"));

  return (
    <>
      {werkzeuge && (
        <nav className="verwaltung-werkzeuge" aria-label="Verwaltungsseiten">
          <ul>
            <li>
              <Link href="/verwaltung/vorschlaege/">
                Vorschläge
                {offene !== null && offene > 0 ? ` (${offene} offen)` : ""}
              </Link>
            </li>
          </ul>
        </nav>
      )}
      <MitgliederVerwaltung />
    </>
  );
}
