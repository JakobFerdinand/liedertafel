import Link from "next/link";
import { Suspense } from "react";
import { LiedDetail } from "@/components/lied-detail";

export default function LiedSeite() {
  return (
    <>
      <nav className="detail-zurueck" aria-label="Zurück zum Verzeichnis">
        <Link href="/lieder/">Liederkatalog</Link>
      </nav>
      <Suspense
        fallback={
          <h1 aria-live="polite" className="detail-zustand-titel">
            Lied wird geladen …
          </h1>
        }
      >
        <LiedDetail />
      </Suspense>
    </>
  );
}
