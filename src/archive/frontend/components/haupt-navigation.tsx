"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { AuthStatus } from "@/components/auth-status";

// Die Zugänglichen Namen der Links wählt die Browser-Prüfung aus
// (shell.spec.ts, chat.spec.ts) und bleiben deshalb unverändert. Die
// Wortmarke in der Kopfleiste bleibt die einzige Verknüpfung zur
// Startseite; im Menü laufen die Inhaltseiten vor dem Konto mit
// Mitglieder- und Verwaltungsaufgaben, getrennt durch einen stillen
// Haarriss (.nav-trenner).
const inhaltsseiten: Array<{ href: string; text: string }> = [
  { href: "/lieder/", text: "Liederkatalog" },
  { href: "/auftritte/", text: "Auftritte" },
  { href: "/fragen/", text: "Archiv fragen" },
];
const verwaltungsaufgaben: Array<{ href: string; text: string }> = [
  { href: "/archiv/", text: "Mitgliederbereich" },
  { href: "/verwaltung/", text: "Verwaltung" },
  { href: "/system/status/", text: "Systemstatus" },
];

// Abschnitte mit ihren Unterseiten gelten als aktuell; /lied/ gehört
// nicht dazu — eigene Route.
function istAktiv(pfad: string, ziel: string): boolean {
  const hier = pfad.replace(/\/+$/, "");
  const dort = ziel.replace(/\/+$/, "");
  return hier === dort || hier.startsWith(`${dort}/`);
}

// Kleine Client-Insel: Aufgeklapptes Menü unter 1100 px, herausgeputzte
// Reihe darüber; die aktuelle Seite erhält aria-current="page" (statisch
// je Route vorgerendert, wie beim Arbeitsplatz-Panel). Zwei Gruppen
// ohne Überschriften — Inhalt vor Verwaltung — und die Anmeldung ganz
// zum Schluss; der Tastatur-Fokus läuft in genau dieser Reihenfolge.
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
          <div className="nav-gruppe">
            {inhaltsseiten.map((eintrag) => (
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
          </div>
          <div className="nav-trenner" aria-hidden="true" />
          <div className="nav-gruppe">
            {verwaltungsaufgaben.map((eintrag) => (
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
          </div>
          <AuthStatus />
        </div>
      </nav>
    </>
  );
}
