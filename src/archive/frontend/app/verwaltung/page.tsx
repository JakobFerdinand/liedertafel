import { MitgliederVerwaltung } from "@/components/mitglieder-verwaltung";

export default function VerwaltungSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="verwaltung-titel"
      >
        <h1 id="verwaltung-titel">
          Mitglieder
          <wbr />
          verwaltung
        </h1>
      </section>
      <MitgliederVerwaltung />
    </>
  );
}
