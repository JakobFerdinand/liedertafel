"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { AuthStatus } from "@/components/auth-status";

// Die Zugänglichen Namen der Links wählt die Browser-Prüfung aus
// (shell.spec.ts, chat.spec.ts) und bleiben deshalb unverändert.
const eintraege: Array<{ href: string; text: string }> = [
  { href: "/", text: "Archiv" },
  { href: "/archiv/", text: "Mitgliederbereich" },
  { href: "/lieder/", text: "Liederkatalog" },
  { href: "/fragen/", text: "Archiv fragen" },
  { href: "/verwaltung/", text: "Verwaltung" },
  { href: "/system/status/", text: "Systemstatus" },
];

// "/" zählt nur auf der Startseite als aktuell, Abschnitte mit ihren
// Unterseiten (z. B. /lied/ gehört nicht dazu — eigene Route).
function istAktiv(pfad: string, ziel: string): boolean {
  const hier = pfad.replace(/\/+$/, "");
  const dort = ziel.replace(/\/+$/, "");
  if (dort === "") return hier === "";
  return hier === dort || hier.startsWith(`${dort}/`);
}

// Kleine Client-Insel: Aufgeklapptes Menü unter 1100 px, herausgeputzte
// Reihe darüber; die aktuelle Seite erhält aria-current="page" (statisch
// je Route vorgerendert, wie beim Arbeitsplatz-Panel).
export function HauptNavigation() {
  const pathname = usePathname();
  const [offen, setOffen] = useState(false);
  const umschalter = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    // Jede Navigation schließt das Menü wieder.
    void pathname;
    setOffen(false);
  }, [pathname]);

  function umschalten() {
    setOffen((offen) => !offen);
  }

  function beiTaste(event: React.KeyboardEvent) {
    if (event.key === "Escape" && offen) {
      event.preventDefault();
      setOffen(false);
      umschalter.current?.focus();
    }
  }

  return (
    <>
      <button
        ref={umschalter}
        type="button"
        className="nav-umschalter"
        aria-expanded={offen}
        aria-controls="haupt-nav"
        onClick={umschalten}
        onKeyDown={beiTaste}
      >
        Menü <span aria-hidden="true">{offen ? "−" : "+"}</span>
      </button>
      <nav
        id="haupt-nav"
        aria-label="Hauptnavigation"
        className={`haupt-nav${offen ? " nav-offen" : ""}`}
        onKeyDown={beiTaste}
      >
        <div className="nav-inhalt">
          {eintraege.map((eintrag) => (
            <Link
              key={eintrag.href}
              href={eintrag.href}
              aria-current={
                istAktiv(pathname, eintrag.href) ? "page" : undefined
              }
            >
              {eintrag.text}
            </Link>
          ))}
          <AuthStatus />
        </div>
      </nav>
    </>
  );
}
