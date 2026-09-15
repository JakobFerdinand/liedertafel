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
          Administratoren laden neue Mitglieder ein, senden ausstehende
          Einladungen erneut, ändern Rollen und deaktivieren oder reaktivieren
          Konten. Deaktivierte behalten Kennung und Verlauf; ihre Sitzungen
          werden abgemeldet. Eine neue E-Mail-Adresse wird erst nach Bestätigung
          per Code übernommen; Kollisionen mit bestehenden Konten werden
          abgewiesen.
        </p>
      </section>
      <MitgliederVerwaltung />
    </>
  );
}
