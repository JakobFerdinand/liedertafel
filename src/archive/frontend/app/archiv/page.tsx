import { ArchivBereich } from "@/components/archiv-bereich";

export default function ArchivSeite() {
  return (
    <>
      <section className="introduction compact" aria-labelledby="archiv-titel">
        <p className="section-label">Nur für Mitglieder</p>
        <h1 id="archiv-titel">Mitgliederbereich</h1>
      </section>
      <section className="auth-karte" aria-labelledby="bereich-titel">
        <h2 id="bereich-titel">Unser Archiv</h2>
        <ArchivBereich />
      </section>
    </>
  );
}
