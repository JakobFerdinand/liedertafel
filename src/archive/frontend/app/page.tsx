import Link from "next/link";
import { SystemStatus } from "@/components/system-status";

export default function Home() {
  return (
    <>
      <section className="introduction" aria-labelledby="archive-title">
        <p className="section-label">Unser Chorarchiv</p>
        <h1 id="archive-title">
          Was wir singen,
          <br />
          bleibt bei uns.
        </h1>
        <p>
          Hier entsteht das gemeinsame Archiv der Liedertafel Mining: für unsere
          Noten, Aufnahmen und die Erinnerung an unsere Auftritte.
        </p>
      </section>
      <section className="archive-note" aria-labelledby="setup-title">
        <h2 id="setup-title">Der Anfang ist gemacht.</h2>
        <p>
          Das Archiv befindet sich im Aufbau. Die technische Grundlage steht;
          eingeladene Mitglieder melden sich mit einem Code aus ihrer E-Mail an.
        </p>
        <Link href="/anmelden/">Anmelden</Link> ·{" "}
        <Link href="/system/status/">Systemstatus öffnen</Link>
      </section>
      <SystemStatus />
    </>
  );
}
