"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
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
  const letzteFrage = useRef<string | null>(null);
  const laufAbbruch = useRef<AbortController | null>(null);

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
    verlaufLaden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setVerlaufsFehler(
          "Das Archiv antwortet nicht. Bitte erneut versuchen.",
        );
    });
    return () => abort.abort();
  }, [verlaufLaden, versuch]);

  async function stelleFrage(frage: string) {
    setFehler("");
    setWiederholbar(false);
    setEingabe("");
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
    setNachrichten((bisher) => {
      const letzte = bisher.at(-1);
      // Nach einer fehlgeschlagenen Antwort steht die Frage schon im Verlauf;
      // beim Wiederholen darf sie nicht doppelt erscheinen. Der Server kennt
      // die Historie ohnehin – gesendet wird immer nur die neue Frage.
      const basis =
        letzte?.role === "user" && letzte.inhalt === frage
          ? bisher
          : [...bisher, frageNachricht];
      return [...basis, antwortNachricht];
    });
    setSchreibt(true);
    const abort = new AbortController();
    laufAbbruch.current = abort;
    let thread = threadId ?? ladeGespeicherteThreadId();
    await streamChatAntwort(
      { threadId: thread ?? crypto.randomUUID(), frage },
      {
        onStart: (aufgeloest) => {
          thread = aufgeloest;
          setThreadId(aufgeloest);
          speichereThreadId(aufgeloest);
        },
        onAssistantDelta: (text) => {
          setNachrichten((bisher) =>
            bisher.map((eintrag) =>
              eintrag.id === antwortNachricht.id
                ? { ...eintrag, inhalt: eintrag.inhalt + text }
                : eintrag,
            ),
          );
        },
        onCitations: (quellen) => {
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
    laufAbbruch.current = null;
    if (!abort.signal.aborted) setSchreibt(false);
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
    await stelleFrage(frage);
  }

  function abbrechen() {
    laufAbbruch.current?.abort();
    setSchreibt(false);
  }

  function neuerChat() {
    if (schreibt) return;
    vergesseThread();
    setThreadId(null);
    setNachrichten([]);
    setFehler("");
    setWiederholbar(false);
    setEingabe("");
    letzteFrage.current = null;
  }

  if (verlaufsFehler) {
    return (
      <div aria-live="polite">
        <p>{verlaufsFehler}</p>
        <button type="button" onClick={() => setVersuch(versuch + 1)}>
          Erneut versuchen
        </button>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p>Anmeldung wird geprüft …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um das Archiv zu fragen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }

  const zaehlerSichtbar = eingabe.length > maximaleFrageLänge - 100;

  return (
    <section className="chat-bereich" aria-labelledby="chat-titel">
      <div className="chat-verlauf" role="log" aria-label="Chatverlauf">
        {nachrichten.length === 0 ? (
          <p className="chat-leer">
            Stelle deine erste Frage an das Archiv – etwa nach einem Lied, einem
            Urheber oder einer Fassung.
          </p>
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
              <p>
                {nachricht.inhalt ||
                  (schreibt
                    ? "Antwort erscheint hier …"
                    : "Die Antwort blieb aus.")}
              </p>
              {nachricht.quellen && nachricht.quellen.length > 0 && (
                <ul className="chat-quellen">
                  {nachricht.quellen.map((quelle) => (
                    <li key={quelle.id}>
                      <Link href={`/lied/?id=${encodeURIComponent(quelle.id)}`}>
                        Quelle: {quelle.label}
                      </Link>
                    </li>
                  ))}
                </ul>
              )}
            </article>
          ))
        )}
        <p className="chat-status" aria-live="polite">
          {schreibt ? "Antwort wird geschrieben …" : ""}
        </p>
        {fehler && (
          <output aria-live="polite" className="feld-fehler">
            {fehler}
          </output>
        )}
        {wiederholbar && (
          <div>
            <button type="button" onClick={() => void erneutVersuchen()}>
              Erneut versuchen
            </button>
          </div>
        )}
      </div>
      <form onSubmit={absenden}>
        <label htmlFor="chat-frage">Frage stellen</label>
        <textarea
          id="chat-frage"
          name="frage"
          rows={3}
          maxLength={maximaleFrageLänge}
          placeholder="Zum Beispiel: Wer komponierte „Das Wandern ist des Müllers Lust“?"
          value={eingabe}
          onChange={(event) => setEingabe(event.target.value)}
          disabled={schreibt}
        />
        {zaehlerSichtbar && (
          <p className="feld-hinweis" id="chat-zaehler">
            Noch {maximaleFrageLänge - eingabe.length} Zeichen übrig.
          </p>
        )}
        <div className="chat-aktionen">
          <button type="submit" disabled={schreibt || eingabe.trim() === ""}>
            Absenden
          </button>
          {schreibt && (
            <button type="button" onClick={abbrechen}>
              Abbrechen
            </button>
          )}
          {nachrichten.length > 0 && !schreibt && (
            <button type="button" onClick={neuerChat}>
              Neuer Chat
            </button>
          )}
        </div>
      </form>
    </section>
  );
}
