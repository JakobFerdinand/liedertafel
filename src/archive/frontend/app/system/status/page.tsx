import { SystemStatus } from "@/components/system-status";

export default function StatusPage() {
  return (
    <>
      <section className="introduction compact">
        <h1>Systemstatus</h1>
        <p>
          Version und Verbindung zum Archiv. Lokale Dienste werden nur auf
          Anfrage geprüft.
        </p>
      </section>
      <SystemStatus diagnostics />
    </>
  );
}
