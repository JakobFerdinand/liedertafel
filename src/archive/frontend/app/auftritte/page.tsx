import { Suspense } from "react";
import { AuftritteBereich } from "@/components/auftritte-bereich";

export default function AuftritteSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="auftritte-titel"
      >
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="auftritte-titel">Auftritte</h1>
        <p>
          Die dokumentierten Auftritte des Chores: Konzerte, Gottesdienste und
          Feste mit Ort und überliefertem Datum. Wo die Unterlagen nur ungefähre
          Angaben hergeben, steht das Datum als unsicher im Verzeichnis.
        </p>
      </section>
      <Suspense
        fallback={
          <p aria-live="polite" className="auth-statuszeile">
            Auftritte werden geladen …
          </p>
        }
      >
        <AuftritteBereich />
      </Suspense>
    </>
  );
}
