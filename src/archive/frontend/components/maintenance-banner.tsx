"use client";

import { useEffect, useState } from "react";

// ARC-012: German maintenance state. The release workflow pauses member
// access during database migrations; this banner surfaces that window on
// every page. It stays hidden when the archive is available or unreachable
// (the status page covers retries), so normal browsing is unaffected.
export function MaintenanceBanner() {
  const [active, setActive] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    async function load() {
      try {
        const response = await fetch("/api/maintenance", {
          signal: abort.signal,
          cache: "no-store",
        });
        if (!response.ok) return;
        const data: { maintenance?: boolean } = await response.json();
        if (!abort.signal.aborted) setActive(data.maintenance === true);
      } catch {
        // Unreachable backend: stay hidden, no background polling.
      }
    }
    void load();
    return () => abort.abort();
  }, []);

  if (!active) return null;

  return (
    <output className="maintenance-banner" aria-live="polite">
      Wartungsarbeiten: Das Archiv ist vorübergehend nicht verfügbar. Bitte
      versuchen Sie es später erneut. <a href="/wartung/">Details</a>
    </output>
  );
}
