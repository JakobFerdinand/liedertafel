"use client";

import { useEffect, useState } from "react";

type Build = { application: string; version: string; development: boolean };

export function SystemStatus({
  diagnostics = false,
}: {
  diagnostics?: boolean;
}) {
  const [build, setBuild] = useState<Build>();
  const [error, setError] = useState("");
  const [result, setResult] = useState("");
  const [busy, setBusy] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const abort = new AbortController();
    async function load() {
      try {
        const response = await fetch("/api/build", {
          signal: abort.signal,
          cache: "no-store",
        });
        if (!response.ok) throw new Error("API unavailable");
        setBuild(await response.json());
        setError("");
      } catch {
        if (!abort.signal.aborted)
          setError("Das Archiv antwortet nicht. Bitte erneut versuchen.");
      }
    }
    // A user retry reruns this effect; there is no background polling.
    void attempt;
    void load();
    return () => abort.abort();
  }, [attempt]);

  async function exercise() {
    setBusy(true);
    setResult("");
    try {
      const database = await fetch("/api/dev/database");
      if (!database.ok)
        throw new Error(
          "PostgreSQL ist nicht erreichbar. Dienste in Aspire prüfen.",
        );
      const data: { connected: boolean; pendingMigrations: string[] } =
        await database.json();
      const csrf = await fetch("/api/antiforgery", {
        credentials: "same-origin",
      });
      if (!csrf.ok)
        throw new Error(
          "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
        );
      const { token } = await csrf.json();
      const response = await fetch("/api/dev/exercise", {
        method: "POST",
        credentials: "same-origin",
        headers: { "X-CSRF-TOKEN": token },
      });
      if (!response.ok)
        throw new Error(
          "Der Funktionstest ist fehlgeschlagen. Details in Aspire prüfen.",
        );
      const { message } = await response.json();
      setResult(
        `PostgreSQL verbunden. ${message}${data.pendingMigrations.length ? " Schema noch ausstehend: archive-migrate in Aspire starten." : " Schema ist aktuell."}`,
      );
    } catch (failure) {
      setResult(
        failure instanceof Error
          ? failure.message
          : "Prüfung fehlgeschlagen. Erneut versuchen.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="system-status" aria-labelledby="connection-title">
      <h2 id="connection-title">Verbindung zum Archiv</h2>
      <div aria-live="polite">
        {error ? (
          <p>{error}</p>
        ) : build ? (
          <dl>
            <div>
              <dt>Anwendung</dt>
              <dd>{build.application}</dd>
            </div>
            <div>
              <dt>API</dt>
              <dd>Verbunden</dd>
            </div>
            <div>
              <dt>Version</dt>
              <dd data-testid="build-version">{build.version}</dd>
            </div>
            <div>
              <dt>Umgebung</dt>
              <dd>{build.development ? "Lokale Entwicklung" : "Produktion"}</dd>
            </div>
          </dl>
        ) : (
          <p>Verbindung wird geprüft …</p>
        )}
      </div>
      {error && (
        <button type="button" onClick={() => setAttempt(attempt + 1)}>
          Erneut versuchen
        </button>
      )}
      {diagnostics && build?.development && (
        <div className="local-check">
          <h3>Lokale Dienste</h3>
          <p>
            Prüft PostgreSQL, schreibt und liest eine Testdatei sowie eine
            Warteschlangen-Nachricht und sendet eine Mail an das lokale
            Postfach.
          </p>
          <button type="button" onClick={exercise} disabled={busy}>
            {busy ? "Prüfung läuft …" : "Lokale Dienste prüfen"}
          </button>
          <output className="check-result" aria-live="polite">
            {result}
          </output>
        </div>
      )}
    </section>
  );
}
