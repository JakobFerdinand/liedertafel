import { holeCsrfToken } from "@/lib/auth";

export type Passkey = {
  credentialId: string;
  name: string | null;
  createdAt: string;
};

export function webAuthnSupported(): boolean {
  return (
    typeof window !== "undefined" &&
    typeof window.PublicKeyCredential === "function" &&
    typeof window.PublicKeyCredential.parseCreationOptionsFromJSON ===
      "function" &&
    typeof window.PublicKeyCredential.parseRequestOptionsFromJSON === "function"
  );
}

async function post(path: string, body: unknown): Promise<Response> {
  const token = await holeCsrfToken();
  return fetch(path, {
    method: "POST",
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: JSON.stringify(body),
  });
}

export async function fetchPasskeys(): Promise<Passkey[]> {
  const response = await fetch("/api/auth/passkeys", {
    credentials: "same-origin",
    cache: "no-store",
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as { passkeys: Passkey[] };
  return data.passkeys ?? [];
}

export async function loginOptions(): Promise<PublicKeyCredentialRequestOptionsJSON> {
  const response = await fetch("/api/auth/passkeys/login/options", {
    method: "POST",
    credentials: "same-origin",
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as {
    requestOptions: string;
  };
  // Identity returns the options as a JSON string; the WebAuthn API needs
  // the parsed PublicKeyCredentialRequestOptionsJSON object.
  return JSON.parse(
    data.requestOptions,
  ) as PublicKeyCredentialRequestOptionsJSON;
}

export async function enrollOptions(): Promise<PublicKeyCredentialCreationOptionsJSON> {
  const response = await post("/api/auth/passkeys/register/options", {});
  if (!response.ok) throw response;
  const data = (await response.json()) as {
    creationOptions: string;
  };
  // Identity returns the options as a JSON string; the WebAuthn API needs
  // the parsed PublicKeyCredentialCreationOptionsJSON object.
  return JSON.parse(
    data.creationOptions,
  ) as PublicKeyCredentialCreationOptionsJSON;
}

export async function enrollPasskey(
  credential: unknown,
  name: string,
): Promise<void> {
  const response = await post("/api/auth/passkeys/register/verify", {
    credential: JSON.stringify(credential),
    name,
  });
  if (!response.ok) throw response;
}

export async function loginPasskey(credential: unknown): Promise<Response> {
  return post("/api/auth/passkeys/login/verify", {
    credential: JSON.stringify(credential),
  });
}

export async function renamePasskey(credentialId: string, name: string) {
  const response = await post("/api/auth/passkeys/rename", {
    credentialId,
    name,
  });
  if (!response.ok) throw response;
}

export async function removePasskey(credentialId: string) {
  const response = await post("/api/auth/passkeys/remove", { credentialId });
  if (!response.ok) throw response;
}
