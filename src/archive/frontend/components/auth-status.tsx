"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";

export function AuthStatus() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [hinweis, setHinweis] = useState("");

  useEffect(() => {
    const abort = new AbortController();
    fetchMe(abort.signal)
      .then((antwort) => {
        if (!abort.signal.aborted) setMe(antwort);
      })
      .catch(() => {
        if (!abort.signal.aborted) setMe({ authenticated: false });
      });
    return () => abort.abort();
  }, []);

  async function abmelden() {
    setBusy(true);
    setHinweis("");
    try {
      const response = await postAuth("/api/auth/logout", {});
      if (!response.ok) throw new Error("logout");
      setMe({ authenticated: false });
      window.location.assign("/anmelden/");
    } catch {
      setHinweis("Abmeldung fehlgeschlagen. Bitte erneut versuchen.");
    } finally {
      setBusy(false);
    }
  }

  if (me === null) {
    return (
      <span aria-live="polite" className="auth-hinweis">
        Anmeldestatus wird geprüft …
      </span>
    );
  }
  if (!me.authenticated) {
    return <Link href="/anmelden/">Anmelden</Link>;
  }
  return (
    <span className="auth-zeile">
      <span>Angemeldet</span>{" "}
      <button type="button" onClick={abmelden} disabled={busy}>
        Abmelden
      </button>
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
    </span>
  );
}
