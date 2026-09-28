import { Suspense } from "react";
import { AuftritteBereich } from "@/components/auftritte-bereich";

export default function AuftritteSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="auftritte-titel"
      >
        <h1 id="auftritte-titel">Auftritte</h1>
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
