import Link from "next/link";
import { KiKosten } from "@/components/ki-kosten";

export default function KiBudgetSeite() {
  return (
    <>
      <nav className="detail-zurueck" aria-label="Zurück zur Verwaltung">
        <Link href="/verwaltung/">Verwaltung</Link>
      </nav>
      <section className="introduction compact" aria-labelledby="ki-titel">
        <h1 id="ki-titel">KI-Kosten</h1>
        <p>
          Verbrauch des laufenden Monats je Feature aus dem Verbrauchsbuch.
          Reine Lesesicht: hier verbraucht nichts.
        </p>
      </section>
      <KiKosten />
    </>
  );
}
