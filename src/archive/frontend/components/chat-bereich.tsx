"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { ChatAntwort } from "@/components/chat-antwort";
import { fetchMe, type MeResponse } from "@/lib/auth";
import {
  type ChatNachricht,
  ladeChatVerlauf,
  ladeGespeicherteThreadId,
  speichereThreadId,
  streamChatAntwort,
  vergesseThread,
} from "@/lib/chat";

const maximaleFrageLänge = 2000;
const vorschlaege = [
  "Welche Lieder gibt es?",
  "Welche Lieder sind von Silcher?",
  "Welche Lieder handeln vom Wandern?",
];

// ARC-022 „Fragen zum Archiv“: Mitglieder fragen auf Deutsch, die Antwort
// entsteht als AG-UI-Ereignisstrom aus dem Archivbestand und nennt die
// zitierten Lieder als Quellen-Verweise.
export function ChatBereich() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [nachrichten, setNachrichten] = useState<ChatNachricht[]>([]);
  const [threadId, setThreadId] = useState<string | null>(null);
  const [eingabe, setEingabe] = useState("");
  const [schreibt, setSchreibt] = useState(false);
  const [fehler, setFehler] = useState("");
  const [wiederholbar, setWiederholbar] = useState(false);
  const [verlaufsFehler, setVerlaufsFehler] = useState("");
  const [versuch, setVersuch] = useState(0);
  const [laedtVerlauf, setLaedtVerlauf] = useState(true);
  const [abgebrochen, setAbgebrochen] = useState(false);
  const letzteFrage = useRef<string | null>(null);
  const letzteAntwort = useRef<string | null>(null);
  const laufAbbruch = useRef<AbortController | null>(null);
  const eingabeFeld = useRef<HTMLTextAreaElement | null>(null);
  const verlaufFeld = useRef<HTMLDivElement | null>(null);
  const folgtAntwort = useRef(true);

  const verlaufLaden = useCallback(async (signal?: AbortSignal) => {
    const antwort = await fetchMe(signal);
    if (!signal?.aborted) setMe(antwort);
    if (!antwort.authenticated) return;
    const gespeichert = ladeGespeicherteThreadId();
    if (!gespeichert) return;
    try {
      const verlauf = await ladeChatVerlauf(gespeichert, signal);
      if (!signal?.aborted) {
        setThreadId(verlauf.threadId);
        speichereThreadId(verlauf.threadId);
        setNachrichten(verlauf.nachrichten);
      }
    } catch (ursache) {
      if (
        ursache instanceof Response &&
        (ursache.status === 404 || ursache.status === 403)
      ) {
        // Falscher, gelöschter oder fremder Verlauf im Speicher: stiller
        // Neuanfang statt Fehlmeldung.
        vergesseThread();
        if (!signal?.aborted) setThreadId(null);
        return;
      }
      throw ursache;
    }
  }, []);

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus; im Hintergrund wird
    // nicht nachgeladen.
    void versuch;
    setVerlaufsFehler("");
    setLaedtVerlauf(true);
    verlaufLaden(abort.signal)
      .catch(() => {
        if (!abort.signal.aborted)
          setVerlaufsFehler(
            "Das Archiv antwortet nicht. Bitte erneut versuchen.",
          );
      })
      .finally(() => {
        if (!abort.signal.aborted) setLaedtVerlauf(false);
      });
    return () => abort.abort();
  }, [verlaufLaden, versuch]);

  useEffect(() => () => laufAbbruch.current?.abort(), []);

  useEffect(() => {
    const feld = verlaufFeld.current;
    if (feld && folgtAntwort.current && nachrichten.length > 0)
      feld.scrollTop = feld.scrollHeight;
  }, [nachrichten]);

  async function stelleFrage(frage: string, wiederholen = false) {
    if (
      laufAbbruch.current ||
      !frage.trim() ||
      frage.length > maximaleFrageLänge ||
      laedtVerlauf
    )
      return;
    setFehler("");
    setWiederholbar(false);
    setAbgebrochen(false);
    if (!wiederholen) setEingabe("");
    letzteFrage.current = frage;
    const frageNachricht: ChatNachricht = {
      id: crypto.randomUUID(),
      role: "user",
      inhalt: frage,
    };
    const antwortNachricht: ChatNachricht = {
      id: crypto.randomUUID(),
      role: "assistant",
      inhalt: "",
    };
    const vorigeAntwort = letzteAntwort.current;
    setNachrichten((bisher) => {
      const bereinigt = wiederholen
        ? bisher.filter((eintrag) => eintrag.id !== vorigeAntwort)
        : bisher;
      const letzte = bereinigt.at(-1);
      // Nach einer fehlgeschlagenen Antwort steht die Frage schon im Verlauf;
      // beim Wiederholen darf sie nicht doppelt erscheinen. Der Server kennt
      // die Historie ohnehin – gesendet wird immer nur die neue Frage.
      const basis =
        letzte?.role === "user" && letzte.inhalt === frage
          ? bereinigt
          : [...bereinigt, frageNachricht];
      return [...basis, antwortNachricht];
    });
    letzteAntwort.current = antwortNachricht.id;
    folgtAntwort.current = true;
    setSchreibt(true);
    const abort = new AbortController();
    laufAbbruch.current = abort;
    let thread = threadId ?? ladeGespeicherteThreadId();
    await streamChatAntwort(
      { threadId: thread ?? crypto.randomUUID(), frage },
      {
        onStart: (aufgeloest) => {
          if (abort.signal.aborted) return;
          thread = aufgeloest;
          setThreadId(aufgeloest);
          speichereThreadId(aufgeloest);
        },
        onAssistantDelta: (text) => {
          if (abort.signal.aborted) return;
          setNachrichten((bisher) =>
            bisher.map((eintrag) =>
              eintrag.id === antwortNachricht.id
                ? { ...eintrag, inhalt: eintrag.inhalt + text }
                : eintrag,
            ),
          );
        },
        onCitations: (quellen) => {
          if (abort.signal.aborted) return;
          setNachrichten((bisher) =>
            bisher.map((eintrag) =>
              eintrag.id === antwortNachricht.id
                ? { ...eintrag, quellen }
                : eintrag,
            ),
          );
        },
        onError: (titel) => {
          if (titel === "Anmeldung erforderlich.") {
            setMe({ authenticated: false });
            return;
          }
          if (titel === "Keine Berechtigung für diesen Chatverlauf.") {
            // Fremder Verlauf: gespeicherte Thread-Id verwerfen und frisch
            // beginnen; die Frage bleibt im Eingabefeld erhalten.
            vergesseThread();
            setThreadId(null);
            setNachrichten([]);
            setEingabe(frage);
            setFehler(
              `${titel} Der bisherige Verlauf wurde verworfen; stelle deine Frage erneut.`,
            );
            return;
          }
          setFehler(titel);
          setWiederholbar(true);
        },
      },
      abort.signal,
    );
    if (laufAbbruch.current !== abort) return;
    laufAbbruch.current = null;
    setSchreibt(false);
    // Eine leere Antwortblase hat nichts zu suchen im Verlauf.
    setNachrichten((bisher) =>
      bisher.filter(
        (eintrag) =>
          eintrag.id !== antwortNachricht.id || eintrag.inhalt !== "",
      ),
    );
  }

  function absenden(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const frage = eingabe.trim();
    if (!frage || schreibt) return;
    void stelleFrage(frage);
  }

  async function erneutVersuchen() {
    const frage = letzteFrage.current;
    if (!frage || schreibt) return;
    await stelleFrage(frage, true);
  }

  function abbrechen() {
    laufAbbruch.current?.abort();
    laufAbbruch.current = null;
    setNachrichten((bisher) =>
      bisher.filter((eintrag) => eintrag.inhalt !== ""),
    );
    setSchreibt(false);
    setAbgebrochen(true);
    setWiederholbar(true);
    eingabeFeld.current?.focus();
  }

  function neuerChat() {
    if (schreibt) return;
    vergesseThread();
    setThreadId(null);
    setNachrichten([]);
    setFehler("");
    setWiederholbar(false);
    setAbgebrochen(false);
    setEingabe("");
    letzteFrage.current = null;
    letzteAntwort.current = null;
    eingabeFeld.current?.focus();
  }

  if (verlaufsFehler) {
    return (
      <div className="chat-zustand" aria-live="polite">
        <p>{verlaufsFehler}</p>
        <button type="button" onClick={() => setVersuch(versuch + 1)}>
          Erneut versuchen
        </button>
      </div>
    );
  }
  if (me === null || laedtVerlauf) {
    return (
      <div className="chat-zustand" aria-live="polite">
        <p>
          {me?.authenticated
            ? "Dein Gespräch wird geladen …"
            : "Anmeldung wird geprüft …"}
        </p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div className="chat-zustand">
        <p>Bitte anmelden, um das Archiv zu fragen.</p>
        <Link href="/anmelden/">Zur Anmeldung für den Chat</Link>
      </div>
    );
  }

  const zaehlerSichtbar = eingabe.length > maximaleFrageLänge - 100;

  return (
    <section className="chat-bereich" aria-label="Fragen zum Archiv">
      {nachrichten.length > 0 && (
        <div className="chat-werkzeuge">
          <span>Dein Gespräch mit dem Archiv</span>
          <button
            className="chat-neustart"
            type="button"
            onClick={neuerChat}
            disabled={schreibt}
          >
            <span aria-hidden="true">+</span> Neuer Chat
          </button>
        </div>
      )}
      <div
        className="chat-verlauf"
        ref={verlaufFeld}
        role="log"
        aria-label="Chatverlauf"
        aria-live="off"
        // biome-ignore lint/a11y/noNoninteractiveTabindex: Der scrollbare Verlauf muss per Tastatur lesbar sein.
        tabIndex={0}
        onScroll={(event) => {
          const feld = event.currentTarget;
          folgtAntwort.current =
            feld.scrollHeight - feld.scrollTop - feld.clientHeight < 64;
        }}
      >
        {nachrichten.length === 0 ? (
          <div className="chat-leer">
            <div className="chat-leer-kopf">
              <svg
                className="chat-archivzeichen"
                viewBox="0 0 48 48"
                fill="none"
                aria-hidden="true"
              >
                <path
                  d="M24 12c-5-4-13-5-19-3v29c7-2 14-1 19 3 5-4 12-5 19-3V9c-6-2-14-1-19 3Zm0 0v29M11 17h7m-7 6h7m-7 6h7m12-12h7m-7 6h7m-7 6h7"
                  stroke="currentColor"
                  strokeWidth="1.7"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
              <h2>Was möchtest du wissen?</h2>
            </div>
            <section
              className="chat-vorschlaege"
              aria-label="Vorgeschlagene Fragen"
            >
              {vorschlaege.map((frage) => (
                <button
                  key={frage}
                  type="button"
                  onClick={() => void stelleFrage(frage)}
                >
                  {frage}
                  <span aria-hidden="true">↗</span>
                </button>
              ))}
            </section>
          </div>
        ) : (
          nachrichten.map((nachricht) => (
            <article
              key={nachricht.id}
              className={
                nachricht.role === "user"
                  ? "chat-nachricht chat-frage"
                  : "chat-nachricht chat-antwort"
              }
            >
              <div className="chat-sprecher">
                {nachricht.role === "user" ? "Du" : "Archiv"}
              </div>
              {nachricht.role === "user" ? (
                <p>{nachricht.inhalt}</p>
              ) : nachricht.inhalt ? (
                <ChatAntwort
                  inhalt={nachricht.inhalt}
                  quellen={nachricht.quellen}
                />
              ) : (
                <p className="chat-sucht">
                  <span aria-hidden="true" />
                  Das Archiv wird durchsucht …
                </p>
              )}
              {nachricht.quellen && nachricht.quellen.length > 0 && (
                <div className="chat-quellen-bereich">
                  <p className="chat-quellen-titel">Im Archiv nachlesen</p>
                  <ul className="chat-quellen">
                    {nachricht.quellen.map((quelle, index) => (
                      <li key={quelle.id}>
                        <Link
                          href={`/lied/?id=${encodeURIComponent(quelle.id)}`}
                          aria-label={`Quelle: ${quelle.label}`}
                        >
                          <span
                            className="chat-quellen-nummer"
                            aria-hidden="true"
                          >
                            {index + 1}
                          </span>
                          {quelle.label}
                          <span aria-hidden="true">↗</span>
                        </Link>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </article>
          ))
        )}
      </div>
      <output className="chat-status">
        {schreibt
          ? "Antwort wird geschrieben …"
          : abgebrochen
            ? "Antwort abgebrochen. Du kannst die Frage erneut stellen oder weiterschreiben."
            : nachrichten.length > 0 && !fehler
              ? "Du kannst an dieses Gespräch anknüpfen."
              : ""}
      </output>
      {(fehler || wiederholbar) && (
        <div className="chat-fehler">
          {fehler && <p role="alert">{fehler}</p>}
          {wiederholbar && (
            <button type="button" onClick={() => void erneutVersuchen()}>
              Erneut versuchen
            </button>
          )}
        </div>
      )}
      <form className="chat-formular" onSubmit={absenden}>
        <label htmlFor="chat-frage">Frage stellen</label>
        <div className="chat-komponist">
          <textarea
            ref={eingabeFeld}
            id="chat-frage"
            name="frage"
            rows={3}
            maxLength={maximaleFrageLänge}
            placeholder="Was möchtest du über unsere Lieder wissen?"
            value={eingabe}
            onChange={(event) => setEingabe(event.target.value)}
            aria-describedby={
              zaehlerSichtbar ? "chat-tastatur chat-zaehler" : "chat-tastatur"
            }
            onKeyDown={(event) => {
              if (
                event.key === "Enter" &&
                (event.ctrlKey || event.metaKey) &&
                !event.nativeEvent.isComposing
              ) {
                event.preventDefault();
                event.currentTarget.form?.requestSubmit();
              }
            }}
          />
          {zaehlerSichtbar && (
            <p className="feld-hinweis" id="chat-zaehler">
              Noch {maximaleFrageLänge - eingabe.length} Zeichen übrig.
            </p>
          )}
          <div className="chat-aktionen">
            <span id="chat-tastatur">Strg / ⌘ + Enter zum Senden</span>
            {schreibt ? (
              <button
                key="abbrechen"
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  abbrechen();
                }}
              >
                <span aria-hidden="true">■</span> Abbrechen
              </button>
            ) : (
              <button
                key="absenden"
                type="submit"
                disabled={eingabe.trim() === ""}
              >
                Absenden <span aria-hidden="true">↑</span>
              </button>
            )}
          </div>
        </div>
        <p className="chat-speicher-hinweis">
          Dein Gespräch wird beim nächsten Besuch fortgesetzt. Mit „Neuer Chat“
          beginnst du von vorn.
        </p>
      </form>
    </section>
  );
}
