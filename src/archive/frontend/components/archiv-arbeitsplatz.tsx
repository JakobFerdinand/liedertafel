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
  const grossansicht = pathname.replace(/\/$/, "") === "/fragen";
  const [breit, setBreit] = useState(false);
  const [offen, setOffen] = useState<boolean | null>(null);
  const oeffner = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLElement>(null);
  const sichtbar = grossansicht || (offen ?? breit);

  useEffect(() => {
    const media = window.matchMedia("(min-width: 1100px)");
    const aktualisieren = () => setBreit(media.matches);
    aktualisieren();
    media.addEventListener("change", aktualisieren);
    return () => media.removeEventListener("change", aktualisieren);
  }, []);

  useEffect(() => {
    // Auf kleinen Bildschirmen führt Navigation zurück zum Seiteninhalt.
    void pathname;
    if (!breit) setOffen(null);
  }, [pathname, breit]);

  function minimieren() {
    setOffen(false);
    oeffner.current?.focus();
  }

  return (
    <>
      {!grossansicht && (
        <div className="archiv-chat-leiste">
          <button
            ref={oeffner}
            type="button"
            aria-expanded={sichtbar}
            aria-controls="archiv-chat"
            onClick={() => {
              if (sichtbar) minimieren();
              else {
                setOffen(true);
                requestAnimationFrame(() => {
                  panel.current?.focus();
                  panel.current?.scrollIntoView({ block: "start" });
                });
              }
            }}
          >
            <span aria-hidden="true">✧</span> Archiv fragen
            <span aria-hidden="true">{sichtbar ? "−" : "+"}</span>
          </button>
          <span>Lieder finden. Zusammenhänge entdecken.</span>
        </div>
      )}
      <main
        id="inhalt"
        className={`archiv-arbeitsplatz${grossansicht ? " archiv-grossansicht" : ""}${sichtbar ? " archiv-chat-offen" : ""}`}
      >
        <div className="archiv-seiteninhalt" hidden={grossansicht}>
          {children}
        </div>
        <section
          ref={panel}
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
            // Auch ein Quellenwechsel zwischen zwei Lied-IDs soll mobil das
            // Lied zeigen. Modifizierte Klicks öffnen nur einen anderen Tab.
            if (
              !breit &&
              !event.ctrlKey &&
              !event.metaKey &&
              !event.shiftKey &&
              event.target instanceof Element &&
              event.target.closest('a[href^="/lied/"]')
            )
              setOffen(false);
          }}
        >
          <div className="archiv-chat-kopf">
            <div className="chat-einleitung">
              {grossansicht ? (
                <h1 id="fragen-titel">Fragen zum Archiv</h1>
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
    </>
  );
}
