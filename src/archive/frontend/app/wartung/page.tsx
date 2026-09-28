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
        <h1 id="wartung-title">Wartungsarbeiten im Archiv</h1>
        <p>
          Anmeldung, Suche und Dateizugriff sind vorübergehend nicht verfügbar.
          Bitte versuche es später erneut.
        </p>
      </section>
      <div className="wartung-aktionen">
        <Link href="/system/status/">Systemstatus ansehen</Link>
        <Link href="/" className="knopf-leise-rahmen">
          Zur Startseite
        </Link>
      </div>
    </>
  );
}
