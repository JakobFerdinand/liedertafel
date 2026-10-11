import { type AgentSubscriber, HttpAgent } from "@ag-ui/client";
import { a2uiEreignisName } from "@/lib/a2ui";
import { authedFetch } from "@/lib/auth";

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
  // ARC-022-4: die A2UI-Nachrichten, die der Server zur Antwort geschickt hat.
  // Roh, denn gezeigt wird erst, was lib/a2ui.ts als Katalog-Inhalt liest.
  oberflaechen?: unknown;
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
    messages: Array<{ role: string; content: string; a2ui?: unknown }>;
  };
  const nachrichten = (daten.messages ?? [])
    .filter(
      (eintrag) => eintrag.role === "user" || eintrag.role === "assistant",
    )
    .map((eintrag, index) => ({
      id: `verlauf-${index}`,
      role: eintrag.role as "user" | "assistant",
      inhalt: eintrag.content,
      ...(Array.isArray(eintrag.a2ui) && eintrag.a2ui.length > 0
        ? { oberflaechen: eintrag.a2ui }
        : {}),
    }));
  return { threadId: daten.threadId, nachrichten };
}

export type ChatStromRueckrufe = {
  // RUN_STARTED trägt die vom Server aufgelöste Thread-Id; der Browser
  // übernimmt sie, wenn er eine leere gesendet hat.
  onStart?: (threadId: string) => void;
  onAssistantDelta?: (text: string) => void;
  onCitations?: (quellen: ChatQuelle[]) => void;
  // Die vom Server geprüften A2UI-Nachrichten der Antwort (roh).
  onOberflaechen?: (nachrichten: unknown) => void;
  // code ist der maschinenlesbare RUN_ERROR-Code des Servers, falls vorhanden.
  onError?: (titel: string, code?: string) => void;
};

// ARC-022-3: Das harte KI-Monatsbudget ist erreicht. Ein erneuter Versuch
// hilft erst im nächsten Monat, deshalb bietet die Oberfläche keinen an.
export const budgetErreichtCode = "monatsbudget_erreicht";

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

// Der AG-UI-Client wirft bei einer Fehlerantwort einen Error mit status und
// dem Körper (payload); ein ProblemDetails-Titel des Servers hat Vorrang.
function fehlerTitel(fehler: unknown): string {
  const { status, payload } = fehler as { status?: unknown; payload?: unknown };
  if (typeof status !== "number") return verbindungsFehler;
  let daten: unknown = payload;
  if (typeof payload === "string") {
    try {
      daten = JSON.parse(payload);
    } catch {
      daten = null;
    }
  }
  const titel = (daten as { title?: unknown } | null)?.title;
  if (typeof titel === "string" && titel) return titel;
  return fehlerTitelNachStatus[status] ?? verbindungsFehler;
}

function istHttpFehler(fehler: unknown): boolean {
  return (
    typeof fehler === "object" &&
    fehler !== null &&
    typeof (fehler as { status?: unknown }).status === "number"
  );
}

// Der Chat läuft als AG-UI-Ereignisstrom (Server-Sent Events). Den Strom liest
// @ag-ui/client direkt vom .NET-Endpunkt; er prüft jedes Ereignis gegen das
// Protokoll. Die Antwort wächst delta für delta; zitierte Archivtreffer und
// die geprüften A2UI-Oberflächen kommen als eigene CUSTOM-Ereignisse. Alle
// Fehler werden über onError ausgespielt; die Funktion selbst wirft nicht.
export async function streamChatAntwort(
  { threadId, frage }: { threadId: string; frage: string },
  rueckrufe: ChatStromRueckrufe,
  signal?: AbortSignal,
): Promise<void> {
  let fehlerGesendet = false;
  let fertig = false;
  let begonnen = false;
  const sendeFehler = (titel: string, code?: string) => {
    // Nach einem Abbruch bleibt der Zustand ruhig; der Nutzer hat selbst
    // beendet, deshalb ist kein Fehler zu zeigen.
    if (fehlerGesendet || signal?.aborted) return;
    fehlerGesendet = true;
    rueckrufe.onError?.(titel, code);
  };

  // Der Server kennt den Verlauf selbst; gesendet wird immer nur die neue
  // Frage, ältere Nachrichten vom Browser würde er ohnehin ignorieren.
  const agent = new HttpAgent({
    url: "/api/chat",
    threadId,
    fetch: (url, init) => authedFetch(url, init),
  });
  agent.addMessage({ id: crypto.randomUUID(), role: "user", content: frage });

  const abbruch = new AbortController();
  const abbrechen = () => abbruch.abort();
  if (signal?.aborted) return;
  signal?.addEventListener("abort", abbrechen, { once: true });

  const abonnent: AgentSubscriber = {
    onRunStartedEvent: ({ event }) => {
      begonnen = true;
      if (event.threadId) rueckrufe.onStart?.(event.threadId);
    },
    onTextMessageContentEvent: ({ event }) => {
      if (event.delta) rueckrufe.onAssistantDelta?.(event.delta);
    },
    onCustomEvent: ({ event }) => {
      if (event.name === "archive.citations" && Array.isArray(event.value)) {
        rueckrufe.onCitations?.(leseQuellen(event.value));
      } else if (
        event.name === a2uiEreignisName &&
        Array.isArray(event.value)
      ) {
        rueckrufe.onOberflaechen?.(event.value);
      }
    },
    onRunErrorEvent: ({ event }) => {
      sendeFehler(event.message || allgemeinerChatFehler, event.code);
    },
    onRunFinishedEvent: () => {
      fertig = true;
    },
    onRunFailed: ({ error }) => {
      sendeFehler(
        istHttpFehler(error)
          ? fehlerTitel(error)
          : fehlerTitelOhneStatus(error, begonnen),
      );
    },
  };

  try {
    await agent.runAgent(
      { runId: crypto.randomUUID(), abortController: abbruch },
      abonnent,
    );
  } catch (fehler) {
    sendeFehler(
      istHttpFehler(fehler)
        ? fehlerTitel(fehler)
        : fehlerTitelOhneStatus(fehler, begonnen),
    );
  } finally {
    signal?.removeEventListener("abort", abbrechen);
  }
  if (!fertig && !fehlerGesendet && !signal?.aborted) {
    // Ein Strom ohne RUN_FINISHED/RUN_ERROR wurde abgebrochen oder halb
    // abgegeben; das behandeln wir wie eine fehlgeschlagene Antwort.
    sendeFehler(allgemeinerChatFehler);
  }
}

// Ohne HTTP-Status ist die Verbindung selbst gescheitert, bevor der Lauf
// begann (kein Netz), oder der Strom brach ab oder verletzte das Protokoll.
function fehlerTitelOhneStatus(fehler: unknown, begonnen: boolean): string {
  return !begonnen && fehler instanceof TypeError
    ? verbindungsFehler
    : allgemeinerChatFehler;
}
