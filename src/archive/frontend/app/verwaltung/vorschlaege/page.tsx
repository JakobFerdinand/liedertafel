import { VorschlaegeVerwaltung } from "@/components/vorschlaege-queue";

export default function VorschlaegeSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="vorschlaege-titel"
      >
        <h1 id="vorschlaege-titel">Vorschläge</h1>
        <p>
          Offene Vorschläge der automatisierten Auswertung nach Art geordnet.
          Annehmen wendet den Vorschlag an; Ablehnen verwirft ihn.
        </p>
      </section>
      <VorschlaegeVerwaltung />
    </>
  );
}
