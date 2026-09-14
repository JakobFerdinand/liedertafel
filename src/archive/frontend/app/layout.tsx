import type { Metadata } from "next";
import Link from "next/link";
import { AuthStatus } from "@/components/auth-status";
import "./globals.css";

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
          <nav aria-label="Hauptnavigation">
            <Link href="/">Archiv</Link>
            <Link href="/archiv/">Mitgliederbereich</Link>
            <Link href="/system/status/">Systemstatus</Link>
            <AuthStatus />
          </nav>
        </header>
        <main id="inhalt">{children}</main>
        <footer>Gemeinsam singen. Gemeinsam bewahren.</footer>
      </body>
    </html>
  );
}
