import { MitgliederVerwaltung } from "@/components/mitglieder-verwaltung";

export default function VerwaltungSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="verwaltung-titel"
      >
        <p className="section-label">Verwaltung</p>
        <h1 id="verwaltung-titel">Mitgliederverwaltung</h1>
        <p>
          Administratoren laden neue Mitglieder ein und senden ausstehende
          Einladungen erneut. Eingeladene melden sich mit einem Code aus ihrer
          E-Mail an.
        </p>
      </section>
      <MitgliederVerwaltung />
    </>
  );
}
