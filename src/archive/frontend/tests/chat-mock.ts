// Geteilte Mocks für den Chat (ARC-022 / ARC-022-4). Die Ereignisse haben die
// Form, die der .NET-Endpunkt wirklich sendet (AGUI.Server, Felder ohne Wert
// weggelassen) und die @ag-ui/client gegen das Protokoll prüft; ein Mock mit
// erfundenen Feldern würde genau die Fehler verbergen, die dort auffallen.
import type { Page } from "@playwright/test";

export const katalogId = "urn:liedertafel:archiv:a2ui:katalog:v1";

export type Ereignis = Record<string, unknown>;

// AG-UI-Ereignisstrom als Text: ein JSON-Objekt je data:-Zeile.
export function sse(ereignisse: Ereignis[]): string {
  return ereignisse
    .map((ereignis) => `data: ${JSON.stringify(ereignis)}\n\n`)
    .join("");
}

export function laufGestartet(threadId: string, runId = "run_1"): Ereignis {
  return { type: "RUN_STARTED", threadId, runId };
}

export function laufBeendet(threadId: string, runId = "run_1"): Ereignis {
  return {
    type: "RUN_FINISHED",
    threadId,
    runId,
    outcome: { type: "success" },
    usage: [{ inputTokens: 12, outputTokens: 34 }],
  };
}

export function laufFehler(message: string, code?: string): Ereignis {
  return { type: "RUN_ERROR", message, ...(code ? { code } : {}) };
}

export function werkzeugAufruf(
  toolCallId: string,
  toolCallName: string,
  argumente: Record<string, unknown>,
): Ereignis[] {
  return [
    {
      type: "TOOL_CALL_START",
      toolCallId,
      toolCallName,
      parentMessageId: "msg-1",
    },
    { type: "TOOL_CALL_ARGS", toolCallId, delta: JSON.stringify(argumente) },
    { type: "TOOL_CALL_END", toolCallId },
  ];
}

export function textBeginn(messageId = "msg-1"): Ereignis {
  return {
    type: "TEXT_MESSAGE_START",
    messageId,
    role: "assistant",
    name: "archive-chat",
  };
}

export function textTeil(delta: string, messageId = "msg-1"): Ereignis {
  return { type: "TEXT_MESSAGE_CONTENT", messageId, delta };
}

export function textEnde(messageId = "msg-1"): Ereignis {
  return { type: "TEXT_MESSAGE_END", messageId };
}

export function quellenEreignis(
  quellen: Array<{ id: string; label: string }>,
): Ereignis {
  return { type: "CUSTOM", name: "archive.citations", value: quellen };
}

export function oberflaechenEreignis(nachrichten: unknown): Ereignis {
  return { type: "CUSTOM", name: "archive.a2ui", value: nachrichten };
}

// Eine Oberfläche, wie der Server sie baut: Erstellen und die eine Wurzel.
export function oberflaeche(
  komponente: Record<string, unknown>,
  surfaceId = "s1",
): unknown[] {
  return [
    {
      version: "v0.9",
      createSurface: { surfaceId, catalogId: katalogId },
    },
    {
      version: "v0.9",
      updateComponents: {
        surfaceId,
        components: [{ id: "root", ...komponente }],
      },
    },
  ];
}

// Eine vollständige, erfolgreiche Antwort: Text, optional Oberflächen und
// Quellen, so wie der Server sie in dieser Reihenfolge schickt.
export function antwortStrom(
  threadId: string,
  teile: string[],
  optionen: {
    oberflaechen?: unknown;
    quellen?: Array<{ id: string; label: string }>;
    werkzeug?: Ereignis[];
  } = {},
): string {
  return sse([
    laufGestartet(threadId),
    ...(optionen.werkzeug ?? []),
    textBeginn(),
    ...teile.map((teil) => textTeil(teil)),
    ...(optionen.oberflaechen === undefined
      ? []
      : [oberflaechenEreignis(optionen.oberflaechen)]),
    ...(optionen.quellen ? [quellenEreignis(optionen.quellen)] : []),
    textEnde(),
    laufBeendet(threadId),
  ]);
}

export const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

export function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  };
}

export async function mockSitzung(page: Page) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(memberMe)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}
