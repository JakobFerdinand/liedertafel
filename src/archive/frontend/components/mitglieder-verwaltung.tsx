"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import {
  fetchMe,
  fetchMitglieder,
  type MeResponse,
  type Mitglied,
  postAuth,
} from "@/lib/auth";

const ROLLEN = [
  { wert: "Member", beschriftung: "Mitglied" },
  { wert: "Editor", beschriftung: "Editor" },
  { wert: "Administrator", beschriftung: "Administrator" },
] as const;

function statusText(status: Mitglied["status"]): string {
  switch (status) {
    case "active":
      return "Aktiv";
    case "deactivated":
      return "Deaktiviert";
    default:
      return "Eingeladen";
  }
}

function mailText(status: Mitglied["invitationMailStatus"]): string {
  switch (status) {
    case "sent":
      return "E-Mail angenommen";
    case "failed":
      return "Versand fehlgeschlagen";
    case "pending":
      return "Ausstehend";
    default:
      return "–";
  }
}

function rollenText(rollen: string[]): string {
  return rollen
    .map(
      (rolle) =>
        ROLLEN.find((eintrag) => eintrag.wert === rolle)?.beschriftung ?? rolle,
    )
    .join(", ");
}

export function MitgliederVerwaltung() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [mitglieder, setMitglieder] = useState<Mitglied[] | null>(null);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [email, setEmail] = useState("");
  const [name, setName] = useState("");
  const [rolle, setRolle] = useState<string>("Member");
  const [emailFehler, setEmailFehler] = useState("");
  const [busy, setBusy] = useState(false);
  const [resendBusy, setResendBusy] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [rollenEntwurf, setRollenEntwurf] = useState<Record<string, string>>(
    {},
  );

  const laden = useCallback(async (signal?: AbortSignal) => {
    const antwort = await fetchMe(signal);
    if (!signal?.aborted) setMe(antwort);
    if (antwort.authenticated && antwort.roles.includes("Administrator")) {
      const liste = await fetchMitglieder(signal);
      if (!signal?.aborted) setMitglieder(liste);
    }
  }, []);

  useEffect(() => {
    const abort = new AbortController();
    laden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden]);

  async function neuLaden() {
    setFehler("");
    try {
      const aktuell = await fetchMe();
      setMe(aktuell);
      if (aktuell.authenticated && aktuell.roles.includes("Administrator")) {
        const liste = await fetchMitglieder();
        setMitglieder(liste);
      } else {
        setMitglieder(null);
      }
    } catch (antwort) {
      setHinweisFehler(antwort);
    }
  }

  function setHinweisFehler(antwort: unknown) {
    if (antwort instanceof Response) {
      void antwort
        .json()
        .catch(() => null)
        .then((problem: { title?: string } | null) => {
          if (
            problem?.title ===
            "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich."
          ) {
            setHinweis(
              "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich. Bitte erneut anmelden.",
            );
          } else if (problem?.title) {
            setHinweis(problem.title);
          } else if (antwort.status === 403) {
            setHinweis("Keine Berechtigung für die Mitgliederverwaltung.");
          } else {
            setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
          }
        });
      return;
    }
    setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
  }

  async function einladen(event: React.FormEvent) {
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
      const response = await postAuth("/api/admin/invitations", {
        email: adresse,
        displayName: name.trim() ? name.trim() : null,
        role: rolle,
      });
      const payload = await response.json().catch(() => null);
      if (
        response.status === 403 &&
        payload?.title?.includes("erneute Anmeldung")
      ) {
        setHinweis(
          "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich. Bitte erneut anmelden.",
        );
        return;
      }
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      setErfolg(`${payload?.message ?? "Einladung gesendet."}`);
      setEmail("");
      setName("");
      await neuLaden();
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy(false);
    }
  }

  async function erneutSenden(adresse: string) {
    setHinweis("");
    setErfolg("");
    setResendBusy(adresse);
    try {
      const response = await postAuth("/api/admin/invitations/resend", {
        email: adresse,
      });
      const payload = await response.json().catch(() => null);
      if (
        response.status === 403 &&
        payload?.title?.includes("erneute Anmeldung")
      ) {
        setHinweis(
          "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich. Bitte erneut anmelden.",
        );
        return;
      }
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        await neuLaden();
        return;
      }
      setErfolg(payload?.message ?? "Einladung erneut gesendet.");
      await neuLaden();
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setResendBusy("");
    }
  }

  async function mitgliedAktion(
    schluessel: string,
    pfad: string,
    nutzlast: unknown,
  ) {
    setHinweis("");
    setErfolg("");
    setAktionBusy(schluessel);
    try {
      const response = await postAuth(pfad, nutzlast);
      const payload = await response.json().catch(() => null);
      if (
        response.status === 403 &&
        payload?.title?.includes("erneute Anmeldung")
      ) {
        setHinweis(
          "Für diese Aktion ist eine erneute Anmeldung mit Code erforderlich. Bitte erneut anmelden.",
        );
        return;
      }
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        await neuLaden();
        return;
      }
      setErfolg(payload?.message ?? "Änderung gespeichert.");
      await neuLaden();
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setAktionBusy("");
    }
  }

  async function deaktivieren(mitglied: Mitglied) {
    await mitgliedAktion(
      `deaktivieren:${mitglied.accountId}`,
      "/api/admin/members/deactivate",
      {
        accountId: mitglied.accountId,
      },
    );
  }

  async function reaktivieren(mitglied: Mitglied) {
    await mitgliedAktion(
      `reaktivieren:${mitglied.accountId}`,
      "/api/admin/members/reactivate",
      {
        accountId: mitglied.accountId,
      },
    );
  }

  async function rolleSpeichern(mitglied: Mitglied) {
    const neu =
      rollenEntwurf[mitglied.accountId] ?? mitglied.roles[0] ?? "Member";
    await mitgliedAktion(
      `rolle:${mitglied.accountId}`,
      "/api/admin/members/role",
      {
        accountId: mitglied.accountId,
        role: neu,
      },
    );
  }

  if (fehler) {
    return (
      <div aria-live="polite">
        <p>{fehler}</p>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p>Mitgliedschaft wird geprüft …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um die Mitgliederverwaltung zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }
  if (!me.roles.includes("Administrator")) {
    return (
      <div aria-live="polite">
        <p>Keine Berechtigung für die Mitgliederverwaltung.</p>
        <Link href="/archiv/">Zum Mitgliederbereich</Link>
      </div>
    );
  }

  return (
    <div>
      <section className="auth-karte" aria-labelledby="einladen-titel">
        <h2 id="einladen-titel">Mitglied einladen</h2>
        <form onSubmit={einladen} noValidate>
          <label htmlFor="einladung-email">E-Mail-Adresse</label>
          <input
            id="einladung-email"
            type="email"
            autoComplete="email"
            required
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            aria-invalid={emailFehler ? true : undefined}
            aria-describedby="einladung-email-fehler"
          />
          {emailFehler && (
            <p id="einladung-email-fehler" role="alert" className="feld-fehler">
              {emailFehler}
            </p>
          )}
          <label htmlFor="einladung-name">Name (optional)</label>
          <input
            id="einladung-name"
            type="text"
            autoComplete="name"
            maxLength={200}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <label htmlFor="einladung-rolle">Rolle</label>
          <select
            id="einladung-rolle"
            value={rolle}
            onChange={(event) => setRolle(event.target.value)}
          >
            {ROLLEN.map((eintrag) => (
              <option key={eintrag.wert} value={eintrag.wert}>
                {eintrag.beschriftung}
              </option>
            ))}
          </select>
          <p className="feld-hinweis">
            Die E-Mail wird zum Versand angenommen; die Zustellung wird nicht
            bestätigt. Bestehende Adressen erhalten keine neue Einladung und
            ihre Rolle wird nicht geändert.
          </p>
          <div className="auth-aktionen">
            <button type="submit" disabled={busy}>
              {busy ? "Einladung wird gesendet …" : "Einladung senden"}
            </button>
          </div>
        </form>
      </section>

      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}{" "}
          {hinweis.includes("erneut anmelden") && (
            <Link href="/anmelden/">Anmelden</Link>
          )}
        </output>
      )}

      <section aria-labelledby="mitglieder-titel" className="mitglieder-liste">
        <h2 id="mitglieder-titel">Mitglieder und Einladungen</h2>
        <p className="feld-hinweis">
          Deaktivierte behalten Kennung und Verlauf; ihre Sitzungen werden
          abgemeldet und eine erneute Anmeldung ist nach Reaktivierung nötig.
          Rollen gelten ab der nächsten Anfrage.
        </p>
        {mitglieder === null ? (
          <p aria-live="polite">Mitglieder werden geladen …</p>
        ) : mitglieder.length === 0 ? (
          <p>Noch keine Mitglieder vorhanden.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th scope="col">E-Mail</th>
                <th scope="col">Name</th>
                <th scope="col">Rollen</th>
                <th scope="col">Status</th>
                <th scope="col">Einladung</th>
                <th scope="col">Aktion</th>
              </tr>
            </thead>
            <tbody>
              {mitglieder.map((mitglied) => {
                const Entwurf =
                  rollenEntwurf[mitglied.accountId] ??
                  mitglied.roles[0] ??
                  "Member";
                const beschaeftigt = resendBusy !== "" || aktionBusy !== "";
                return (
                  <tr key={mitglied.accountId}>
                    <td>{mitglied.email}</td>
                    <td>{mitglied.displayName ?? "–"}</td>
                    <td>{rollenText(mitglied.roles)}</td>
                    <td>{statusText(mitglied.status)}</td>
                    <td>{mailText(mitglied.invitationMailStatus)}</td>
                    <td>
                      <div className="mitglied-aktionen">
                        {mitglied.status === "invited" && (
                          <button
                            type="button"
                            disabled={beschaeftigt}
                            onClick={() => erneutSenden(mitglied.email)}
                          >
                            {resendBusy === mitglied.email
                              ? "Wird gesendet …"
                              : "Erneut senden"}
                          </button>
                        )}
                        <select
                          id={`rolle-${mitglied.accountId}`}
                          aria-label={`Rolle für ${mitglied.email} wählen`}
                          value={Entwurf}
                          disabled={beschaeftigt}
                          onChange={(event) =>
                            setRollenEntwurf((bisher) => ({
                              ...bisher,
                              [mitglied.accountId]: event.target.value,
                            }))
                          }
                        >
                          {ROLLEN.map((eintrag) => (
                            <option key={eintrag.wert} value={eintrag.wert}>
                              {eintrag.beschriftung}
                            </option>
                          ))}
                        </select>
                        <button
                          type="button"
                          disabled={beschaeftigt}
                          onClick={() => rolleSpeichern(mitglied)}
                        >
                          {aktionBusy === `rolle:${mitglied.accountId}`
                            ? "Wird gespeichert …"
                            : "Rolle speichern"}
                        </button>
                        {mitglied.status === "deactivated" ? (
                          <button
                            type="button"
                            disabled={beschaeftigt}
                            onClick={() => reaktivieren(mitglied)}
                          >
                            {aktionBusy === `reaktivieren:${mitglied.accountId}`
                              ? "Wird reaktiviert …"
                              : "Reaktivieren"}
                          </button>
                        ) : (
                          <button
                            type="button"
                            disabled={beschaeftigt}
                            onClick={() => deaktivieren(mitglied)}
                          >
                            {aktionBusy === `deaktivieren:${mitglied.accountId}`
                              ? "Wird deaktiviert …"
                              : "Deaktivieren"}
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
