import { Suspense } from "react";
import { AuftrittDetail } from "@/components/auftritt-detail";

export default function AuftrittSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="auftritt-titel"
      >
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="auftritt-titel">Auftritt</h1>
        <p>Ort, Datum und überlieferte Angaben zu einem Auftritt des Chores.</p>
      </section>
      <Suspense fallback={<p aria-live="polite">Auftritt wird geladen …</p>}>
        <AuftrittDetail />
      </Suspense>
    </>
  );
}
