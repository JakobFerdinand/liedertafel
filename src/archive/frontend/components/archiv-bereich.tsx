"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { PasskeyVerwaltung } from "@/components/passkey-verwaltung";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";

export function ArchivBereich() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    fetchMe(abort.signal)
      .then((antwort) => {
        if (!abort.signal.aborted) setMe(antwort);
      })
      .catch(() => {
        if (!abort.signal.aborted)
          setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
      });
    return () => abort.abort();
  }, []);

  async function abmelden() {
    setBusy(true);
    try {
      await postAuth("/api/auth/logout", {});
      window.location.assign("/anmelden/");
    } catch {
      setFehler("Abmeldung fehlgeschlagen. Bitte erneut versuchen.");
    } finally {
      setBusy(false);
    }
  }

  // Werkzeugzeile des Mitgliedsbereichs: Titel links, Sitzung rechts; die
  // Rollenzeile erscheint erst, wenn die Prüfung sie belegt.
  const sitzung = me?.authenticated ? me : null;
  const rolle = sitzung
    ? `Angemeldet als ${sitzung.displayName ?? sitzung.email} (${sitzung.roles.join(", ")})`
    : "";
  const kopf = (
    <div className="mitglieder-kopf">
      <h2 id="bereich-titel">Unser Archiv</h2>
      {sitzung && (
        <div className="mitglieder-sitzung">
          <p className="mitglieder-rolle">{rolle}</p>
          <button
            type="button"
            className="knopf-leise"
            disabled={busy}
            onClick={abmelden}
          >
            Abmelden
          </button>
        </div>
      )}
    </div>
  );

  if (fehler) {
    return (
      <>
        {kopf}
        <div aria-live="polite">
          <p className="hinweis-block">{fehler}</p>
        </div>
      </>
    );
  }
  if (me === null) {
    return (
      <>
        {kopf}
        <div aria-live="polite">
          <p className="auth-statuszeile">Mitgliedschaft wird geprüft …</p>
        </div>
      </>
    );
  }
  if (!me.authenticated) {
    return (
      <>
        {kopf}
        <div>
          <p>Bitte anmelden, um den Mitgliederbereich zu sehen.</p>
          <Link href="/anmelden/" className="verweis-kachel">
            <span>Anmelden</span>
            <span aria-hidden="true">↗</span>
          </Link>
        </div>
      </>
    );
  }
  return (
    <>
      {kopf}
      <p>Willkommen im Archiv. Hier entsteht die Sammlung unseres Chores.</p>
      <Link href="/lieder/" className="verweis-kachel">
        <span>
          <strong>Der Liederkatalog ist jetzt geöffnet</strong>
          <span className="verweis-kachel-neben">
            Veröffentlichte Lieder mit Fassungen und Bearbeitungen sind dort
            sichtbar.
          </span>
        </span>
        <span aria-hidden="true">↗</span>
      </Link>
      <PasskeyVerwaltung />
    </>
  );
}
