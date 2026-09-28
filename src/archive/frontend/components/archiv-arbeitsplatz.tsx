"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
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
  const mitChat = [
    "/lieder",
    "/lied",
    "/auftritte",
    "/auftritt",
    "/programm",
  ].includes(pfad);
  const [breit, setBreit] = useState(false);
  const [offen, setOffen] = useState(false);
  const vorherPfad = useRef(pfad);
  const sichtbar = grossansicht || (mitChat && offen);

  useEffect(() => {
    const media = window.matchMedia("(min-width: 1200px)");
    const aktualisieren = () => setBreit(media.matches);
    aktualisieren();
    media.addEventListener("change", aktualisieren);
    return () => media.removeEventListener("change", aktualisieren);
  }, []);

  useEffect(() => {
    // Der Bestand hat die volle Breite. Nur wer aus der ausdrücklichen
    // Chatansicht kommt, führt das Gespräch auf breiten Seiten daneben weiter.
    if (grossansicht) setOffen(false);
    else if (vorherPfad.current === "/fragen" && breit) setOffen(true);
    vorherPfad.current = pfad;
  }, [grossansicht, breit, pfad]);

  useEffect(() => {
    if (!breit) setOffen(false);
  }, [breit]);

  function minimieren() {
    setOffen(false);
    // Das sichtbare Öffnen-Werkzeug bleibt auch unter der Menüschwelle
    // erreichbar; nach dem Umschalten ist es wieder im DOM.
    requestAnimationFrame(() =>
      document
        .querySelector<HTMLButtonElement>(".archiv-chat-oeffnen")
        ?.focus(),
    );
  }

  function oeffnenChat() {
    setOffen(true);
    requestAnimationFrame(() =>
      document.getElementById("archiv-chat")?.focus(),
    );
  }

  return (
    <main
      id="inhalt"
      className={`archiv-arbeitsplatz${grossansicht ? " archiv-grossansicht" : ""}${sichtbar ? " archiv-chat-offen" : ""}`}
    >
      <div className="archiv-seiteninhalt" hidden={grossansicht}>
        {mitChat && !sichtbar && (
          <button
            type="button"
            className="archiv-chat-oeffnen knopf-leise"
            aria-controls="archiv-chat"
            onClick={oeffnenChat}
          >
            Chat öffnen
          </button>
        )}
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
                {startseite ? "Das Chorarchiv" : "Archiv fragen"}
              </h1>
            ) : (
              <h2 id="fragen-titel">Fragen zum Archiv</h2>
            )}
            {grossansicht && !startseite && (
              <p>Frag nach Liedern, Fassungen oder Auftritten.</p>
            )}
          </div>
          {startseite && (
            <>
              <nav className="start-bestand" aria-label="Im Chorarchiv">
                <Link href="/lieder/" className="start-bestand-haupt">
                  Liederkatalog öffnen
                </Link>
                <Link href="/auftritte/">Auftritte</Link>
                <Link href="/programm/">Programme</Link>
              </nav>
              <p>Frag nach Liedern, Fassungen oder Auftritten.</p>
            </>
          )}
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
