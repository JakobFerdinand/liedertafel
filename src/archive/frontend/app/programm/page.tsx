import { Suspense } from "react";
import { ProgrammListe } from "@/components/programm-liste";

export default function ProgrammeSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="programme-titel"
      >
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="programme-titel">Programme</h1>
        <p>
          Die kommenden Auftritte mit ihrem veröffentlichten Programm:
          Reihenfolge, Fassungen und praktische Notizen zum Mitnehmen. Wo das
          Datum nur ungefähr überliefert ist, bleibt es als unsicher
          gekennzeichnet.
        </p>
      </section>
      <Suspense
        fallback={
          <p aria-live="polite" className="auth-statuszeile">
            Programme werden geladen …
          </p>
        }
      >
        <ProgrammListe />
      </Suspense>
    </>
  );
}
