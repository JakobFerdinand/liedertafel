import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "Wartungsarbeiten | Liedertafel Archiv",
  description:
    "Das Chorarchiv ist wegen Wartungsarbeiten vorübergehend nicht verfügbar.",
  robots: { index: false, follow: false },
};

// ARC-012: member-facing maintenance state. Linked from the site-wide
// maintenance banner while a release window is active.
export default function Wartung() {
  return (
    <>
      <section className="introduction compact" aria-labelledby="wartung-title">
        <p className="section-label">Hinweis</p>
        <h1 id="wartung-title">Wartungsarbeiten im Archiv.</h1>
        <p>
          Das Archiv wird gerade gewartet und ist vorübergehend nicht verfügbar.
          Bitte versuchen Sie es später erneut — Anmeldung, Suche und
          Dateizugriff pausieren, bis die Arbeiten abgeschlossen sind.
        </p>
      </section>
      <section className="archive-note" aria-labelledby="wartung-weiter">
        <h2 id="wartung-weiter">Wie geht es weiter?</h2>
        <p>
          Sobald die Wartung abgeschlossen ist, steht das Archiv wie gewohnt zur
          Verfügung. Den aktuellen Zustand sehen Sie auf der{" "}
          <Link href="/system/status/">Systemstatus-Seite</Link>.
        </p>
        <Link href="/">Zur Startseite</Link>
      </section>
    </>
  );
}
