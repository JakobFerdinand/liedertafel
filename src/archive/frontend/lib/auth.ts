export type MeResponse =
  | { authenticated: false }
  | {
      authenticated: true;
      accountId: string;
      email: string;
      displayName: string | null;
      roles: string[];
      verifiedAt: string;
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

export async function fetchMe(signal?: AbortSignal): Promise<MeResponse> {
  const response = await fetch("/api/auth/me", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw new Error("me");
  return (await response.json()) as MeResponse;
}
