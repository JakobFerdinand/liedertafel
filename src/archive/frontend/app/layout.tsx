import type { Metadata } from "next";
import Link from "next/link";
import { ArchivArbeitsplatz } from "@/components/archiv-arbeitsplatz";
import { HauptNavigation } from "@/components/haupt-navigation";
import { MaintenanceBanner } from "@/components/maintenance-banner";
import "./globals.css";
import "./fragen/chat.css";
import "./arbeitsplatz.css";

export const metadata: Metadata = {
  title: "Liedertafel Archiv",
  description: "Das Chorarchiv der Liedertafel Mining 1906.",
  robots: { index: false, follow: false },
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="de-AT">
      <body>
        <a className="skip-link" href="#inhalt">
          Zum Inhalt
        </a>
        <header className="site-header">
          <Link
            href="/"
            className="wordmark"
            aria-label="Liedertafel Archiv, Startseite"
          >
            Liedertafel <span>Mining 1906</span>
          </Link>
          <HauptNavigation />
        </header>
        <MaintenanceBanner />
        <ArchivArbeitsplatz>{children}</ArchivArbeitsplatz>
        <footer>Gemeinsam singen. Gemeinsam bewahren.</footer>
      </body>
    </html>
  );
}
