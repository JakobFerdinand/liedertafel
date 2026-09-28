import { Suspense } from "react";
import { LiederKatalog } from "@/components/lieder-katalog";

export default function LiederSeite() {
  return (
    <>
      <section className="lieder-einleitung" aria-labelledby="lieder-titel">
        <h1 id="lieder-titel">Liederkatalog</h1>
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
