import { Suspense } from "react";
import { ProgrammListe } from "@/components/programm-liste";

export default function ProgrammeSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="programme-titel"
      >
        <h1 id="programme-titel">Programme</h1>
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
