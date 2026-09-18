"use client";

import { useCallback, useEffect, useState } from "react";
import {
  enrollOptions,
  enrollPasskey,
  fetchPasskeys,
  type Passkey,
  removePasskey,
  renamePasskey,
  webAuthnSupported,
} from "@/lib/passkeys";

export function PasskeyVerwaltung() {
  const [passkeys, setPasskeys] = useState<Passkey[] | null>(null);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);
  const [name, setName] = useState("");
  const [bearbeitet, setBearbeitet] = useState<string | null>(null);
  const [neuerName, setNeuerName] = useState("");
  // WebAuthn support is detected after mount only: a direct render-time
  // branch on `typeof window` would break server/client hydration.
  const [unterstuetzt, setUnterstuetzt] = useState<boolean | null>(null);

  const laden = useCallback(async () => {
    try {
      setPasskeys(await fetchPasskeys());
    } catch {
      setPasskeys([]);
      setFehler("Passkeys konnten nicht geladen werden.");
    }
  }, []);

  useEffect(() => {
    setUnterstuetzt(webAuthnSupported());
    laden();
  }, [laden]);

  async function hinzufuegen() {
    setBusy(true);
    setFehler("");
    setHinweis("");
    try {
      const options = await enrollOptions();
      const credential = await navigator.credentials.create({
        publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options),
      });
      if (credential === null) {
        setHinweis("Registrierung abgebrochen.");
        return;
      }
      await enrollPasskey(credential, name.trim());
      setName("");
      await laden();
      setHinweis("Passkey registriert.");
    } catch (fehler) {
      if (fehler instanceof DOMException && fehler.name === "NotAllowedError") {
        setHinweis("Registrierung abgebrochen.");
        return;
      }
      setFehler(
        "Der Passkey konnte nicht registriert werden. Bitte erneut versuchen.",
      );
      await laden();
    } finally {
      setBusy(false);
    }
  }

  async function umbenennen(credentialId: string) {
    setBusy(true);
    setFehler("");
    try {
      await renamePasskey(credentialId, neuerName.trim());
      setBearbeitet(null);
      setNeuerName("");
      await laden();
    } catch {
      setFehler("Umbenennen fehlgeschlagen. Bitte erneut versuchen.");
    } finally {
      setBusy(false);
    }
  }

  async function entfernen(credentialId: string) {
    setBusy(true);
    setFehler("");
    try {
      await removePasskey(credentialId);
      await laden();
      setHinweis("Passkey entfernt.");
    } catch {
      setFehler("Entfernen fehlgeschlagen. Bitte erneut versuchen.");
      await laden();
    } finally {
      setBusy(false);
    }
  }

  if (passkeys === null) {
    return <p aria-live="polite">Passkeys werden geladen …</p>;
  }
  return (
    <div className="passkey-verwaltung">
      <p>Passkeys dieses Kontos</p>
      {passkeys.length === 0 ? (
        <p className="feld-hinweis">Noch kein Passkey registriert.</p>
      ) : (
        <ul>
          {passkeys.map((passkey) => (
            <li key={passkey.credentialId}>
              {bearbeitet === passkey.credentialId ? (
                <>
                  <input
                    aria-label="Neuer Name"
                    value={neuerName}
                    maxLength={100}
                    onChange={(event) => setNeuerName(event.target.value)}
                  />
                  <button
                    type="button"
                    disabled={busy || neuerName.trim().length === 0}
                    onClick={() => umbenennen(passkey.credentialId)}
                  >
                    Speichern
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => setBearbeitet(null)}
                  >
                    Abbrechen
                  </button>
                </>
              ) : (
                <>
                  <span>{passkey.name ?? "Passkey"}</span>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => {
                      setBearbeitet(passkey.credentialId);
                      setNeuerName(passkey.name ?? "");
                    }}
                  >
                    Umbenennen
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => entfernen(passkey.credentialId)}
                  >
                    Entfernen
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      {unterstuetzt === null ? null : unterstuetzt ? (
        <div className="auth-aktionen">
          <input
            aria-label="Name für den neuen Passkey"
            placeholder="z. B. Laptop"
            value={name}
            maxLength={100}
            onChange={(event) => setName(event.target.value)}
          />
          <button type="button" disabled={busy} onClick={hinzufuegen}>
            {busy ? "Registrierung läuft …" : "Passkey hinzufügen"}
          </button>
        </div>
      ) : (
        <p className="feld-hinweis">
          Dieses Browser unterstützt keine Passkeys.
        </p>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-hinweis">
          {hinweis}
        </output>
      )}
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
    </div>
  );
}
