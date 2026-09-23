import { Suspense } from "react";
import { ChatBereich } from "@/components/chat-bereich";
import "./chat.css";

export default function FragenSeite() {
  return (
    <div className="chat-seite">
      <section className="chat-einleitung" aria-labelledby="fragen-titel">
        <h1 id="fragen-titel">Fragen zum Archiv</h1>
        <p>
          Entdecke unsere Lieder und ihre Urheber – mit Antworten aus dem
          Archiv.
        </p>
      </section>
      <Suspense fallback={<p aria-live="polite">Fragen werden geladen …</p>}>
        <ChatBereich />
      </Suspense>
    </div>
  );
}
