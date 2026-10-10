import { VerwaltungStart } from "@/components/verwaltung-start";

export default function VerwaltungSeite() {
  return (
    <>
      <section
        className="introduction compact"
        aria-labelledby="verwaltung-titel"
      >
        <h1 id="verwaltung-titel">Mitgliederverwaltung</h1>
      </section>
      <VerwaltungStart />
    </>
  );
}
