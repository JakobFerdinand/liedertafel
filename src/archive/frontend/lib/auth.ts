export type MeResponse =
  | { authenticated: false }
  | {
      authenticated: true;
      accountId: string;
      email: string;
      displayName: string | null;
      roles: string[];
      verifiedAt: string;
      // ARC-011-1: how the current session was created ("email_code" or
      // "passkey"); older tickets count as email-code sessions.
      authMethod: "email_code" | "passkey";
    };

async function getCsrfToken(): Promise<string> {
  const response = await fetch("/api/antiforgery", {
    credentials: "same-origin",
    cache: "no-store",
  });
  if (!response.ok) throw new Error("csrf");
  const { token } = await response.json();
  if (typeof token !== "string" || !token) throw new Error("csrf");
  return token;
}

// Jeder Abruf von /api/antiforgery rotiert das archive.csrf-Cookie, und das
// Antwortpaar (Cookie + Token) ist nur miteinander gültig. Ein frischer Token
// pro Anfrage würde also gleichzeitig laufende Geschwisteranfragen brechen
// (parallel hochgeladene Dateien erhielten sonst „Ungültiger
// Sicherheitstoken.“). Ein Paar wird daher einmal pro Seite geladen und
// wiederverwendet; erst nach einer Ablehnung wird es ersetzt.
let csrfLadelauf: Promise<string> | null = null;

export function holeCsrfToken(): Promise<string> {
  csrfLadelauf ??= getCsrfToken().catch((fehler) => {
    csrfLadelauf = null;
    throw fehler;
  });
  return csrfLadelauf;
}

const sicherheitstokenTitel = "Ungültiger Sicherheitstoken.";

async function istSicherheitstokenFehler(antwort: Response): Promise<boolean> {
  if (antwort.status !== 400) return false;
  const inhalt = (await antwort
    .clone()
    .json()
    .catch(() => null)) as { title?: unknown } | null;
  return inhalt?.title === sicherheitstokenTitel;
}

async function authedFetch(path: string, init: RequestInit): Promise<Response> {
  const anfrage = (token: string): RequestInit => ({
    ...init,
    credentials: "same-origin",
    headers: { ...init.headers, "X-CSRF-TOKEN": token },
  });
  let antwort = await fetch(path, anfrage(await holeCsrfToken()));
  if (await istSicherheitstokenFehler(antwort)) {
    // Das Paar wurde woanders rotiert (Anmeldestatuswechsel, zweiter Tab);
    // einmal frisch laden und dieselbe Anfrage unverändert wiederholen.
    csrfLadelauf = null;
    antwort = await fetch(path, anfrage(await holeCsrfToken()));
  }
  return antwort;
}

export async function postAuth(
  path: string,
  body: unknown,
  signal?: AbortSignal,
): Promise<Response> {
  return authedFetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal,
  });
}

export async function patchAuth(
  path: string,
  body: unknown,
): Promise<Response> {
  return authedFetch(path, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

export async function deleteAuth(path: string): Promise<Response> {
  return authedFetch(path, { method: "DELETE" });
}

export async function fetchMe(signal?: AbortSignal): Promise<MeResponse> {
  const response = await fetch("/api/auth/me", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw new Error("me");
  return (await response.json()) as MeResponse;
}

export type MitgliedStatus = "invited" | "active" | "deactivated";
export type EinladungsMailStatus = "none" | "pending" | "sent" | "failed";

export type Mitglied = {
  accountId: string;
  email: string;
  displayName: string | null;
  roles: string[];
  status: MitgliedStatus;
  invitationId: string | null;
  invitedAt: string | null;
  invitedByAccountId: string | null;
  acceptedAt: string | null;
  lastInvitationSentAt: string | null;
  invitationMailStatus: EinladungsMailStatus;
};

export async function fetchMitglieder(
  signal?: AbortSignal,
): Promise<Mitglied[]> {
  const response = await fetch("/api/admin/members", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as { members: Mitglied[] };
  return data.members ?? [];
}
