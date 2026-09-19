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

export async function getCsrfToken(): Promise<string> {
  const response = await fetch("/api/antiforgery", {
    credentials: "same-origin",
    cache: "no-store",
  });
  if (!response.ok) throw new Error("csrf");
  const { token } = await response.json();
  if (typeof token !== "string" || !token) throw new Error("csrf");
  return token;
}

export async function postAuth(path: string, body: unknown): Promise<Response> {
  const token = await getCsrfToken();
  return fetch(path, {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: JSON.stringify(body),
  });
}

export async function patchAuth(
  path: string,
  body: unknown,
): Promise<Response> {
  const token = await getCsrfToken();
  return fetch(path, {
    method: "PATCH",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: JSON.stringify(body),
  });
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
