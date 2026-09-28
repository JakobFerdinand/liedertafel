import Link from "next/link";
import { Suspense } from "react";
import { AuftrittDetail } from "@/components/auftritt-detail";

export default function AuftrittSeite() {
  return (
    <>
      <nav className="detail-zurueck" aria-label="Zurück zum Verzeichnis">
        <Link href="/auftritte/">Auftritte</Link>
      </nav>
      <Suspense fallback={<p aria-live="polite">Auftritt wird geladen …</p>}>
        <AuftrittDetail />
      </Suspense>
    </>
  );
}
