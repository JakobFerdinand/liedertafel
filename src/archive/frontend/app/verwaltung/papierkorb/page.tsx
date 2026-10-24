import Link from "next/link";
import { Papierkorb } from "@/components/papierkorb";

export default function PapierkorbSeite() {
  return (
    <>
      <nav className="detail-zurueck" aria-label="Zurück zur Verwaltung">
        <Link href="/verwaltung/">Verwaltung</Link>
      </nav>
      <section
        className="introduction compact"
        aria-labelledby="papierkorb-titel"
      >
        <h1 id="papierkorb-titel">Papierkorb</h1>
        <p>Gelöschte Lieder, Auftritte und Dateien. Mitglieder sehen sie nicht.</p>
      </section>
      <Papierkorb />
    </>
  );
}
