"use client";

import { useEffect, useState } from "react";
import { postAuth } from "@/lib/auth";
import { loginOptions, loginPasskey, webAuthnSupported } from "@/lib/passkeys";

type Step = "email" | "code";

export function AnmeldeFormular() {
  const [step, setStep] = useState<Step>("email");
  const [email, setEmail] = useState("");
  const [code, setCode] = useState("");
  const [emailFehler, setEmailFehler] = useState("");
  const [codeFehler, setCodeFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [busy, setBusy] = useState(false);
  const [passkeyBusy, setPasskeyBusy] = useState(false);
  // WebAuthn support is detected after mount only: a direct render-time
  // branch on `typeof window` would break server/client hydration.
  const [passkeyUnterstuetzt, setPasskeyUnterstuetzt] = useState(false);
  const [cooldown, setCooldown] = useState(0);
  // Zustand des zweiten Schritts: „Code gesendet" ist der Stilltext
  // zwischen Anforderung und Prüfung, kein Erfolg am Seitenende.
  const [codeGesendet, setCodeGesendet] = useState(false);

  useEffect(() => {
    setPasskeyUnterstuetzt(webAuthnSupported());
  }, []);

  async function mitPasskeyAnmelden() {
    if (!webAuthnSupported()) {
      setHinweis(
        "Dieses Browser unterstützt keine Passkeys. Code-Anmeldung verwenden.",
      );
      return;
    }
    setPasskeyBusy(true);
    setHinweis("");
    setErfolg("");
    try {
      const options = await loginOptions();
      const credential = await navigator.credentials.get({
        publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options),
      });
      if (credential === null) {
        setHinweis("Passkey-Anmeldung abgebrochen. Bitte erneut versuchen.");
        return;
      }
      const response = await loginPasskey(credential);
      if (response.ok) {
        setErfolg("Anmeldung erfolgreich. Mitgliederbereich wird geöffnet.");
        window.location.assign("/archiv/");
        return;
      }
      const problem = await response.json().catch(() => null);
      if (problem?.title === "Ungültiger Sicherheitstoken.") {
        setHinweis(
          "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
        );
      } else {
        setHinweis("Passkey-Anmeldung fehlgeschlagen. Bitte erneut versuchen.");
      }
    } catch (fehler) {
      if (fehler instanceof DOMException && fehler.name === "NotAllowedError") {
        // Abbruch oder Zeitüberschreitung im Authenticator-Dialog: normal.
        return;
      }
      setHinweis("Passkey-Anmeldung fehlgeschlagen. Bitte erneut versuchen.");
    } finally {
      setPasskeyBusy(false);
    }
  }

  async function codeAnfordern(event: React.FormEvent) {
    event.preventDefault();
    setEmailFehler("");
    setHinweis("");
    setErfolg("");
    const adresse = email.trim();
    if (!adresse) {
      setEmailFehler("Bitte E-Mail-Adresse eingeben.");
      return;
    }
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(adresse)) {
      setEmailFehler("Bitte gültige E-Mail-Adresse eingeben.");
      return;
    }
    setBusy(true);
    try {
      const response = await postAuth("/api/auth/code/request", {
        email: adresse,
      });
      if (response.status === 429) {
        setHinweis("Zu viele Versuche. Bitte später erneut versuchen.");
        return;
      }
      if (!response.ok) {
        const problem = await response.json().catch(() => null);
        if (
          problem?.title === "Ungültiger Sicherheitstoken." ||
          response.status === 400
        ) {
          setHinweis(
            problem?.title === "Ungültiger Sicherheitstoken."
              ? "Sicherheitstoken konnte nicht geladen werden. Seite neu laden."
              : "Das hat nicht geklappt. Bitte erneut versuchen.",
          );
        } else {
          setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
        }
        return;
      }
      setStep("code");
      setCodeGesendet(true);
      setCooldown(60);
      const timer = window.setInterval(() => {
        setCooldown((rest) => {
          if (rest <= 1) {
            window.clearInterval(timer);
            return 0;
          }
          return rest - 1;
        });
      }, 1000);
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy(false);
    }
  }

  async function anmelden(event: React.FormEvent) {
    event.preventDefault();
    setCodeFehler("");
    setHinweis("");
    const ziffern = code.replace(/\D/g, "");
    if (!ziffern) {
      setCodeFehler("Bitte Code eingeben.");
      return;
    }
    if (ziffern.length !== 6) {
      setCodeFehler("Code ungültig oder abgelaufen. Neuen Code anfordern.");
      return;
    }
    setBusy(true);
    try {
      const response = await postAuth("/api/auth/code/verify", {
        email: email.trim(),
        code: ziffern,
      });
      if (response.status === 429) {
        setHinweis("Zu viele Versuche. Bitte später erneut versuchen.");
        return;
      }
      if (!response.ok) {
        const problem = await response.json().catch(() => null);
        if (problem?.title === "Ungültiger Sicherheitstoken.") {
          setHinweis(
            "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
          );
        } else {
          setCodeFehler("Code ungültig oder abgelaufen. Neuen Code anfordern.");
        }
        return;
      }
      setErfolg("Anmeldung erfolgreich. Mitgliederbereich wird geöffnet.");
      window.location.assign("/archiv/");
    } catch {
      setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="auth-karte">
      {step === "email" ? (
        <form onSubmit={codeAnfordern} noValidate>
          <label htmlFor="email">E-Mail-Adresse</label>
          <input
            id="email"
            type="email"
            autoComplete="email"
            required
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            aria-invalid={emailFehler ? true : undefined}
            aria-describedby="email-hinweis email-fehler"
          />
          <p id="email-hinweis" className="feld-hinweis">
            Nur eingeladene Mitglieder erhalten einen Code.
          </p>
          {emailFehler && (
            <p id="email-fehler" role="alert" className="feld-fehler">
              {emailFehler}
            </p>
          )}
          <div className="auth-aktionen">
            <button type="submit" disabled={busy}>
              {busy ? "Code wird gesendet …" : "Code anfordern"}
            </button>
          </div>
          {passkeyUnterstuetzt && (
            <div className="auth-aktionen">
              <button
                type="button"
                className="knopf-leise"
                disabled={passkeyBusy}
                onClick={mitPasskeyAnmelden}
              >
                {passkeyBusy
                  ? "Passkey wird geprüft …"
                  : "Mit Passkey anmelden"}
              </button>
            </div>
          )}
        </form>
      ) : (
        <form onSubmit={anmelden} noValidate>
          <label htmlFor="code">Code aus der E-Mail</label>
          <input
            id="code"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            required
            value={code}
            onChange={(event) => setCode(event.target.value)}
            aria-invalid={codeFehler ? true : undefined}
            aria-describedby="code-fehler"
          />
          {codeFehler && (
            <p id="code-fehler" role="alert" className="feld-fehler">
              {codeFehler}
            </p>
          )}
          <div className="auth-aktionen">
            <button type="submit" disabled={busy}>
              {busy ? "Anmeldung läuft …" : "Anmelden"}
            </button>
            <button
              type="button"
              className="knopf-leise"
              disabled={busy || cooldown > 0}
              onClick={(event) =>
                codeAnfordern(event as unknown as React.FormEvent<Element>)
              }
            >
              Code erneut senden
            </button>
            <button
              type="button"
              className="knopf-leise"
              disabled={busy}
              onClick={() => {
                setStep("email");
                setCode("");
                setCodeFehler("");
                setErfolg("");
                setCodeGesendet(false);
              }}
            >
              Andere E-Mail-Adresse verwenden
            </button>
          </div>
        </form>
      )}
      <output aria-live="polite" className="auth-statuszeile">
        {step === "code" && codeGesendet
          ? "Code gesendet. Bitte Postfach prüfen und Code hier eingeben …"
          : ""}
      </output>
      <output aria-live="polite" className="auth-statuszeile">
        {step === "code" && cooldown > 0
          ? `Neuer Code in ${cooldown} Sekunden möglich.`
          : ""}
      </output>
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="auth-fehler">
          {hinweis}
        </output>
      )}
    </div>
  );
}
