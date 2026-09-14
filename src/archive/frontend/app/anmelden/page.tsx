import { AnmeldeFormular } from "@/components/anmelde-formular";

export default function AnmeldenSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="anmelden-titel"
      >
        <p className="section-label">Mitgliederzugang</p>
        <h1 id="anmelden-titel">Anmelden</h1>
        <p>
          Mitglieder melden sich mit E-Mail-Adresse und Code an. Der Code kommt
          per E-Mail und ist einmalig gültig.
        </p>
      </section>
      <AnmeldeFormular />
    </>
  );
}
