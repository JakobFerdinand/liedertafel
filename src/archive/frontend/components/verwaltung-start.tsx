"use client";

import { MitgliederVerwaltung } from "@/components/mitglieder-verwaltung";

// Die Verwaltungsseite bündelt die Redaktionswerkzeuge. Seit dem Wegfall der
// Vorschlagsliste ist das die Mitgliederverwaltung; weitere Werkzeuge
// (etwa der Papierkorb) kommen als Verweise hierher.

export function VerwaltungStart() {
  return <MitgliederVerwaltung />;
}
