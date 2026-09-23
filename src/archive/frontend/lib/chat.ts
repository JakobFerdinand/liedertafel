import { postAuth } from "@/lib/auth";

export type ChatQuelle = { id: string; label: string };

// Nur vom Server gelieferte Quellen werden verlinkt. Weder Modelltext noch
// Quellenbezeichnungen dürfen eine URL vorgeben.
function leseQuellen(wert: unknown): ChatQuelle[] {
  if (!Array.isArray(wert)) return [];
  return wert.filter(
    (quelle): quelle is ChatQuelle =>
      quelle !== null &&
      typeof quelle === "object" &&
      typeof quelle.id === "string" &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
        quelle.id,
      ) &&
      typeof quelle.label === "string" &&
      quelle.label.trim().length > 0,
  );
}

export type ChatNachricht = {
  id: string;
  role: "user" | "assistant";
  inhalt: string;
  quellen?: ChatQuelle[];
};

// ARC-022: Der Verlauf liegt beim Server; der Browser merkt sich nur die
// Thread-Id, um nach einem Neuladen dort fortsetzen zu können.
const threadSpeicherSchluessel = "arc-chat-thread";

export function ladeGespeicherteThreadId(): string | null {
  if (typeof window === "undefined") return null;
  try {
    return window.localStorage.getItem(threadSpeicherSchluessel);
  } catch {
    return null;
  }
}

export function speichereThreadId(threadId: string): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(threadSpeicherSchluessel, threadId);
  } catch {
    // Kein dauerhafter Speicher verfügbar; der Verlauf bleibt sitzungsspezifisch.
  }
}

export function vergesseThread(): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.removeItem(threadSpeicherSchluessel);
  } catch {
    // Ohne Speicher gibt es auch nichts zu vergessen.
  }
}

export type ChatVerlauf = { threadId: string; nachrichten: ChatNachricht[] };

export async function ladeChatVerlauf(
  threadId: string,
  signal?: AbortSignal,
): Promise<ChatVerlauf> {
  const antwort = await fetch(
    `/api/chat/thread/${encodeURIComponent(threadId)}`,
    {
      credentials: "same-origin",
      cache: "no-store",
      signal,
    },
  );
  if (!antwort.ok) throw antwort;
  const daten = (await antwort.json()) as {
    threadId: string;
    messages: Array<{ role: string; content: string }>;
  };
  const nachrichten = (daten.messages ?? [])
    .filter(
      (eintrag) => eintrag.role === "user" || eintrag.role === "assistant",
    )
    .map((eintrag, index) => ({
      id: `verlauf-${index}`,
      role: eintrag.role as "user" | "assistant",
      inhalt: eintrag.content,
    }));
  return { threadId: daten.threadId, nachrichten };
}

export type ChatStromRueckrufe = {
  // RUN_STARTED trägt die vom Server aufgelöste Thread-Id; der Browser
  // übernimmt sie, wenn er eine leere gesendet hat.
  onStart?: (threadId: string) => void;
  onAssistantDelta?: (text: string) => void;
  onCitations?: (quellen: ChatQuelle[]) => void;
  onError?: (titel: string) => void;
};

// Deutsche Rückfalltexte, falls ein ProblemDetails-Körper keinen Titel trägt.
const fehlerTitelNachStatus: Record<number, string> = {
  400: "Die Anfrage war ungültig. Bitte erneut versuchen.",
  401: "Anmeldung erforderlich.",
  403: "Keine Berechtigung für diesen Chatverlauf.",
  503: "Der Archiv-Chat ist derzeit nicht verfügbar.",
};

const allgemeinerChatFehler =
  "Die Antwort konnte nicht fertig gestellt werden.";

const verbindungsFehler = "Das Archiv antwortet nicht. Bitte erneut versuchen.";

async function fehlerTitel(antwort: Response): Promise<string> {
  const daten = (await antwort
    .clone()
    .json()
    .catch(() => null)) as { title?: unknown } | null;
  if (typeof daten?.title === "string" && daten.title) return daten.title;
  return fehlerTitelNachStatus[antwort.status] ?? verbindungsFehler;
}

type AgUiEreignis = {
  type?: unknown;
  threadId?: unknown;
  message?: unknown;
  delta?: unknown;
  name?: unknown;
  value?: unknown;
};

// Der Chat läuft als AG-UI-Ereignisstrom (Server-Sent Events, ein
// JSON-Objekt je data:-Zeile). Die Antwort wächst delta für delta;
// zitierte Archivtreffer kommen als eigenes CUSTOM-Ereignis. Alle Fehler
// werden über onError ausgespielt; die Funktion selbst wirft nicht.
export async function streamChatAntwort(
  { threadId, frage }: { threadId: string; frage: string },
  rueckrufe: ChatStromRueckrufe,
  signal?: AbortSignal,
): Promise<void> {
  let fehlerGesendet = false;
  const sendeFehler = (titel: string) => {
    // Nach einem Abbruch bleibt der Zustand ruhig; der Nutzer hat selbst
    // beendet, deshalb ist kein Fehler zu zeigen.
    if (fehlerGesendet || signal?.aborted) return;
    fehlerGesendet = true;
    rueckrufe.onError?.(titel);
  };

  let antwort: Response;
  try {
    antwort = await postAuth(
      "/api/chat",
      {
        threadId,
        runId: crypto.randomUUID(),
        messages: [{ id: crypto.randomUUID(), role: "user", content: frage }],
      },
      signal,
    );
  } catch {
    sendeFehler(verbindungsFehler);
    return;
  }
  if (!antwort.ok) {
    sendeFehler(await fehlerTitel(antwort));
    return;
  }
  const leser = antwort.body?.getReader();
  if (!leser) {
    sendeFehler(allgemeinerChatFehler);
    return;
  }

  const verarbeiteEreignis = (ereignis: AgUiEreignis) => {
    switch (ereignis.type) {
      case "RUN_STARTED":
        if (typeof ereignis.threadId === "string" && ereignis.threadId) {
          rueckrufe.onStart?.(ereignis.threadId);
        }
        return;
      case "TEXT_MESSAGE_CONTENT":
        if (typeof ereignis.delta === "string" && ereignis.delta) {
          rueckrufe.onAssistantDelta?.(ereignis.delta);
        }
        return;
      case "CUSTOM":
        if (
          ereignis.name === "archive.citations" &&
          Array.isArray(ereignis.value)
        ) {
          rueckrufe.onCitations?.(leseQuellen(ereignis.value));
        }
        return;
      case "RUN_ERROR":
        sendeFehler(
          typeof ereignis.message === "string" && ereignis.message
            ? ereignis.message
            : allgemeinerChatFehler,
        );
        return;
      case "RUN_FINISHED":
        fertig = true;
        return;
      default:
        // Unbekannte Ereignisarten (auch TOOL_CALL_*, rawEvent-Felder)
        // bleiben unberücksichtigt.
        return;
    }
  };

  const verarbeiteZeile = (roh: string) => {
    const zeile = roh.replace(/\r$/, "");
    if (!zeile.startsWith("data:")) return;
    const text = zeile.slice(5).trimStart();
    if (!text) return;
    try {
      verarbeiteEreignis(JSON.parse(text) as AgUiEreignis);
    } catch {
      // Unverständliche Zeile überspringen statt den ganzen Lauf abzubrechen.
    }
  };

  const dekodierer = new TextDecoder();
  let puffer = "";
  let fertig = false;
  try {
    for (;;) {
      const { done, value } = await leser.read();
      if (done) break;
      puffer += dekodierer.decode(value, { stream: true });
      let trennung = puffer.indexOf("\n");
      while (trennung !== -1) {
        verarbeiteZeile(puffer.slice(0, trennung));
        puffer = puffer.slice(trennung + 1);
        trennung = puffer.indexOf("\n");
      }
    }
    puffer += dekodierer.decode();
    if (puffer) verarbeiteZeile(puffer);
  } catch {
    if (!signal?.aborted) sendeFehler(allgemeinerChatFehler);
  } finally {
    leser.releaseLock();
  }
  if (!fertig && !fehlerGesendet && !signal?.aborted) {
    // Ein Strom ohne RUN_FINISHED/RUN_ERROR wurde abgebrochen oder halb
    // abgegeben; das behandeln wir wie eine fehlgeschlagene Antwort.
    sendeFehler(allgemeinerChatFehler);
  }
}
