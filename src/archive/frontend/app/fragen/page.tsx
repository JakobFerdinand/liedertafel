import { Suspense } from "react";
import { ChatBereich } from "@/components/chat-bereich";

export default function FragenSeite() {
  return (
    <>
      <section className="introduction compact" aria-labelledby="fragen-titel">
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="fragen-titel">Fragen zum Archiv</h1>
        <p>
          Frage das Archiv zu Liedern, Urhebern und Fassungen. Die Antworten
          entstehen aus dem gesammelten Bestand und nennen ihre Quellen.
        </p>
      </section>
      <Suspense fallback={<p aria-live="polite">Fragen werden geladen …</p>}>
        <ChatBereich />
      </Suspense>
    </>
  );
}
