"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { ChatBereich } from "@/components/chat-bereich";

// Ein einziger Chat im dauerhaften Layout: auch Entwurf und laufende Antwort
// bleiben beim Öffnen eines Lieds oder beim Wechsel in die Großansicht erhalten.
export function ArchivArbeitsplatz({
  children,
}: {
  children: React.ReactNode;
}) {
  const pathname = usePathname();
  // Die Startseite und /fragen/ zeigen denselben Chat in der Großansicht.
  const pfad = pathname.replace(/\/$/, "");
  const grossansicht = pfad === "" || pfad === "/fragen";
  const startseite = pfad === "";
  const [breit, setBreit] = useState(false);
  const [offen, setOffen] = useState<boolean | null>(null);
  const sichtbar = grossansicht || (offen ?? breit);

  useEffect(() => {
    const media = window.matchMedia("(min-width: 1100px)");
    const aktualisieren = () => setBreit(media.matches);
    aktualisieren();
    media.addEventListener("change", aktualisieren);
    return () => media.removeEventListener("change", aktualisieren);
  }, []);

  useEffect(() => {
    // Minimiert bleibt der Chat zu, bis eine Chatansicht (/ oder /fragen/)
    // ihn wieder hervorholt; erst sie stellt die Voreinstellung für
    // folgende Inhaltsseiten wieder her. Auch ein Wechsel des Breitpunkts
    // stellt die Voreinstellung wieder her. Der gemountete Chat behält
    // dabei Entwurf und laufende Antwort.
    if (grossansicht) setOffen(null);
  }, [grossansicht]);

  useEffect(() => {
    void breit;
    setOffen(null);
  }, [breit]);

  function minimieren() {
    setOffen(false);
    // Minimieren darf den Fokus nicht ins Leere fallen lassen: der
    // Navigationslink zum Chat ist der Weg zurück.
    if (breit)
      document
        .querySelector<HTMLAnchorElement>('#haupt-nav a[href="/fragen/"]')
        ?.focus();
  }

  return (
    <main
      id="inhalt"
      className={`archiv-arbeitsplatz${grossansicht ? " archiv-grossansicht" : ""}${sichtbar ? " archiv-chat-offen" : ""}`}
    >
      <div className="archiv-seiteninhalt" hidden={grossansicht}>
        {children}
      </div>
      <section
        id="archiv-chat"
        className="chat-seite archiv-chat-panel"
        aria-labelledby="fragen-titel"
        hidden={!sichtbar}
        tabIndex={-1}
        onKeyDown={(event) => {
          if (event.key === "Escape" && !grossansicht) {
            event.preventDefault();
            minimieren();
          }
        }}
        onClickCapture={(event) => {
          // Auch Links zur aktuellen Seite (andere Lied-ID oder Anmeldung)
          // müssen mobil den Seiteninhalt zeigen. Modifizierte Klicks nicht.
          if (
            !breit &&
            !event.ctrlKey &&
            !event.metaKey &&
            !event.shiftKey &&
            !event.altKey &&
            event.target instanceof Element &&
            event.target.closest('a[href^="/"]')
          )
            setOffen(false);
        }}
      >
        <div className="archiv-chat-kopf">
          <div className="chat-einleitung">
            {grossansicht ? (
              <h1 id="fragen-titel">
                {startseite
                  ? "Was wir singen, bleibt bei uns."
                  : "Fragen zum Archiv"}
              </h1>
            ) : (
              <h2 id="fragen-titel">Fragen zum Archiv</h2>
            )}
            <p>
              Entdecke unsere Lieder und ihre Urheber – mit Antworten aus dem
              Archiv.
            </p>
          </div>
          {!grossansicht && (
            <div className="archiv-chat-ansicht">
              <Link href="/fragen/">Großansicht</Link>
              <button type="button" onClick={minimieren}>
                Chat minimieren <span aria-hidden="true">−</span>
              </button>
            </div>
          )}
        </div>
        <ChatBereich />
      </section>
    </main>
  );
}
