import { AnmeldeFormular } from "@/components/anmelde-formular";

export default function AnmeldenSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="anmelden-titel"
      >
        <h1 id="anmelden-titel">Anmelden</h1>
        <p>Mit E-Mail-Adresse und Einmalcode zum Chorarchiv.</p>
      </section>
      <AnmeldeFormular />
    </>
  );
}
