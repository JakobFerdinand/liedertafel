import Link from "next/link";
import { Suspense } from "react";
import { AuftrittDetail } from "@/components/auftritt-detail";

export default function AuftrittSeite() {
  return (
    <>
      <nav className="detail-zurueck" aria-label="Zurück zum Verzeichnis">
        <Link href="/auftritte/">Auftritte</Link>
      </nav>
      <Suspense
        fallback={
          <h1 aria-live="polite" className="detail-zustand-titel">
            Auftritt wird geladen …
          </h1>
        }
      >
        <AuftrittDetail />
      </Suspense>
    </>
  );
}
