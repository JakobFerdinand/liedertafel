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

/* Zustandsmarken in der Geometrie von .lieder-status: lebende Konten
   tragen die Markenlinie, Einladung und Ruhestand die stille Linie. */
function statusMarke(status: Mitglied["status"]): string {
  return status === "active"
    ? "mitglieder-marke"
    : "mitglieder-marke mitglieder-marke-leise";
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
  const [neueAdressen, setNeueAdressen] = useState<Record<string, string>>({});
  const [wechselCodes, setWechselCodes] = useState<Record<string, string>>({});
  const [angefordert, setAngefordert] = useState<Record<string, string>>({});
  const [wechselFehler, setWechselFehler] = useState<Record<string, string>>(
    {},
  );
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

  async function wechselCodeAnfordern(mitglied: Mitglied) {
    const adresse = (neueAdressen[mitglied.accountId] ?? "").trim();
    setWechselFehler((bisher) => ({ ...bisher, [mitglied.accountId]: "" }));
    if (!adresse) {
      setWechselFehler((bisher) => ({
        ...bisher,
        [mitglied.accountId]: "Bitte neue E-Mail-Adresse eingeben.",
      }));
      return;
    }
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(adresse)) {
      setWechselFehler((bisher) => ({
        ...bisher,
        [mitglied.accountId]: "Bitte gültige E-Mail-Adresse eingeben.",
      }));
      return;
    }
    setHinweis("");
    setErfolg("");
    setAktionBusy(`wechsel-anfordern:${mitglied.accountId}`);
    try {
      const response = await postAuth("/api/admin/members/email/request", {
        accountId: mitglied.accountId,
        newEmail: adresse,
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
      setErfolg(payload?.message ?? "Bestätigungscode gesendet.");
      setAngefordert((bisher) => ({
        ...bisher,
        [mitglied.accountId]: adresse,
      }));
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setAktionBusy("");
    }
  }

  async function wechselBestaetigen(mitglied: Mitglied) {
    const adresse = angefordert[mitglied.accountId] ?? "";
    const code = (wechselCodes[mitglied.accountId] ?? "").replace(/\D/g, "");
    setWechselFehler((bisher) => ({ ...bisher, [mitglied.accountId]: "" }));
    if (code.length !== 6) {
      setWechselFehler((bisher) => ({
        ...bisher,
        [mitglied.accountId]: "Bitte sechsstelligen Code eingeben.",
      }));
      return;
    }
    setHinweis("");
    setErfolg("");
    setAktionBusy(`wechsel-bestaetigen:${mitglied.accountId}`);
    try {
      const response = await postAuth("/api/admin/members/email/confirm", {
        accountId: mitglied.accountId,
        newEmail: adresse,
        code,
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
      setErfolg(payload?.message ?? "Adresse geändert.");
      setNeueAdressen((bisher) => {
        const naechste = { ...bisher };
        delete naechste[mitglied.accountId];
        return naechste;
      });
      setWechselCodes((bisher) => {
        const naechste = { ...bisher };
        delete naechste[mitglied.accountId];
        return naechste;
      });
      setAngefordert((bisher) => {
        const naechste = { ...bisher };
        delete naechste[mitglied.accountId];
        return naechste;
      });
      await neuLaden();
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setAktionBusy("");
    }
  }

  if (fehler) {
    return (
      <div aria-live="polite">
        <p className="hinweis-block">{fehler}</p>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p className="auth-statuszeile">Mitgliedschaft wird geprüft …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um die Mitgliederverwaltung zu sehen.</p>
        <Link href="/anmelden/" className="verweis-kachel">
          <span>Anmelden</span>
          <span aria-hidden="true">↗</span>
        </Link>
      </div>
    );
  }
  if (!me.roles.includes("Administrator")) {
    return (
      <div aria-live="polite">
        <p>Keine Berechtigung für die Mitgliederverwaltung.</p>
        <Link href="/archiv/" className="verweis-kachel">
          <span>Zum Mitgliederbereich</span>
          <span aria-hidden="true">↗</span>
        </Link>
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
        <output aria-live="polite" className="auth-fehler">
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
          <p aria-live="polite" className="auth-statuszeile">
            Mitglieder werden geladen …
          </p>
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
                const einladung = mailText(mitglied.invitationMailStatus);
                return (
                  <tr key={mitglied.accountId}>
                    <td className="mitglieder-email">{mitglied.email}</td>
                    <td className="mitglieder-name">
                      {mitglied.displayName ?? "–"}
                    </td>
                    <td className="mitglieder-rollen">
                      {rollenText(mitglied.roles)}
                    </td>
                    <td className="mitglieder-zustand">
                      <span className={statusMarke(mitglied.status)}>
                        {statusText(mitglied.status)}
                      </span>
                    </td>
                    <td className="mitglieder-zustand">
                      {einladung === "–" ? (
                        "–"
                      ) : (
                        <span className="mitglieder-marke mitglieder-marke-leise">
                          {einladung}
                        </span>
                      )}
                    </td>
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
                            className="knopf-leise"
                            disabled={beschaeftigt}
                            onClick={() => deaktivieren(mitglied)}
                          >
                            {aktionBusy === `deaktivieren:${mitglied.accountId}`
                              ? "Wird deaktiviert …"
                              : "Deaktivieren"}
                          </button>
                        )}
                        <details className="adress-wechsel">
                          <summary>Adresse ändern</summary>
                          <div className="adress-wechsel-formular">
                            <p className="feld-hinweis">
                              Eine Adressänderung behält Kennung und Verlauf,
                              meldet offene Sitzungen des Kontos ab und wird
                              erst mit dem Code an die neue Adresse wirksam.
                              Kollisionen mit bestehenden Konten werden
                              abgewiesen.
                            </p>
                            <label
                              className="visually-hidden"
                              htmlFor={`neu-${mitglied.accountId}`}
                            >
                              {`Neue E-Mail-Adresse für ${mitglied.email}`}
                            </label>
                            <input
                              id={`neu-${mitglied.accountId}`}
                              type="email"
                              autoComplete="email"
                              placeholder="Neue E-Mail-Adresse"
                              aria-label={`Neue E-Mail-Adresse für ${mitglied.email}`}
                              value={neueAdressen[mitglied.accountId] ?? ""}
                              disabled={beschaeftigt}
                              onChange={(event) =>
                                setNeueAdressen((bisher) => ({
                                  ...bisher,
                                  [mitglied.accountId]: event.target.value,
                                }))
                              }
                            />
                            <button
                              type="button"
                              disabled={beschaeftigt}
                              onClick={() => wechselCodeAnfordern(mitglied)}
                            >
                              {aktionBusy ===
                              `wechsel-anfordern:${mitglied.accountId}`
                                ? "Wird gesendet …"
                                : "Code senden"}
                            </button>
                            {angefordert[mitglied.accountId] && (
                              <>
                                <p className="feld-hinweis">
                                  {`Code an ${angefordert[mitglied.accountId]} gesendet. Erst die Bestätigung übernimmt die Adresse; Kennung und Verlauf bleiben erhalten.`}
                                </p>
                                <label
                                  className="visually-hidden"
                                  htmlFor={`wechsel-code-${mitglied.accountId}`}
                                >
                                  {`Bestätigungscode für ${angefordert[mitglied.accountId]}`}
                                </label>
                                <input
                                  id={`wechsel-code-${mitglied.accountId}`}
                                  type="text"
                                  inputMode="numeric"
                                  autoComplete="one-time-code"
                                  placeholder="Code"
                                  aria-label={`Bestätigungscode für ${angefordert[mitglied.accountId]}`}
                                  value={wechselCodes[mitglied.accountId] ?? ""}
                                  disabled={beschaeftigt}
                                  onChange={(event) =>
                                    setWechselCodes((bisher) => ({
                                      ...bisher,
                                      [mitglied.accountId]: event.target.value,
                                    }))
                                  }
                                />
                                <button
                                  type="button"
                                  disabled={beschaeftigt}
                                  onClick={() => wechselBestaetigen(mitglied)}
                                >
                                  {aktionBusy ===
                                  `wechsel-bestaetigen:${mitglied.accountId}`
                                    ? "Wird geprüft …"
                                    : "Adresse bestätigen"}
                                </button>
                              </>
                            )}
                            {wechselFehler[mitglied.accountId] && (
                              <p role="alert" className="feld-fehler">
                                {wechselFehler[mitglied.accountId]}
                              </p>
                            )}
                          </div>
                        </details>
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
