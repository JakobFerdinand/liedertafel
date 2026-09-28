import { SystemStatus } from "@/components/system-status";

export default function StatusPage() {
  return (
    <>
      <section className="introduction compact">
        <h1>Systemstatus</h1>
      </section>
      <SystemStatus diagnostics />
    </>
  );
}
