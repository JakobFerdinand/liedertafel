import { Suspense } from "react";
import { LiederKatalog } from "@/components/lieder-katalog";

export default function LiederSeite() {
  return (
    <>
      <section className="introduction compact" aria-labelledby="lieder-titel">
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="lieder-titel">Liederkatalog</h1>
        <p>
          Gesammelte Lieder des Chores mit Komponisten, Textdichtern und
          musikalischen Fassungen.
        </p>
      </section>
      <Suspense
        fallback={
          <p aria-live="polite" className="auth-statuszeile">
            Lieder werden geladen …
          </p>
        }
      >
        <LiederKatalog />
      </Suspense>
    </>
  );
}
