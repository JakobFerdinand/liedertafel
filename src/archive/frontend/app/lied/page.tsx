import { Suspense } from "react";
import { LiedDetail } from "@/components/lied-detail";

export default function LiedSeite() {
  return (
    <>
      <section className="introduction compact" aria-labelledby="lied-titel">
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="lied-titel">Lied</h1>
        <p>Titel, Mitwirkende und alle erfassten Fassungen eines Liedes.</p>
      </section>
      <Suspense fallback={<p aria-live="polite">Lied wird geladen …</p>}>
        <LiedDetail />
      </Suspense>
    </>
  );
}
