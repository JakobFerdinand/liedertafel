"use client";

import { useState } from "react";
import { postAuth } from "@/lib/auth";

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
  const [cooldown, setCooldown] = useState(0);

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
      setErfolg("Code gesendet. Bitte Postfach prüfen und Code hier eingeben.");
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
              disabled={busy || cooldown > 0}
              onClick={(event) =>
                codeAnfordern(event as unknown as React.FormEvent<Element>)
              }
            >
              {cooldown > 0
                ? `Neuer Code in ${cooldown} Sekunden möglich.`
                : "Code erneut senden"}
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => {
                setStep("email");
                setCode("");
                setCodeFehler("");
                setErfolg("");
              }}
            >
              Andere E-Mail-Adresse verwenden
            </button>
          </div>
        </form>
      )}
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
    </div>
  );
}
