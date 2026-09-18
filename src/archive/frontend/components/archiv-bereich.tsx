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
        <p>Bitte anmelden, um den Mitgliederbereich zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }
  return (
    <div>
      <p>Willkommen im Archiv. Inhalte folgen in den nächsten Schritten.</p>
      <p>
        Angemeldet als {me.displayName ?? me.email} ({me.roles.join(", ")})
      </p>
      <button type="button" onClick={abmelden} disabled={busy}>
        Abmelden
      </button>
      <PasskeyVerwaltung />
    </div>
  );
}
