"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

// Suchformular des Startbildschirms: schickt die Eingabe an die Katalogseite
// und startet dort die Suche auf Seite 1 (ARC-020).
export function SucheFormular({ feldId }: { feldId: string }) {
  const router = useRouter();
  const [begriff, setBegriff] = useState("");

  function absenden(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const wert = begriff.trim();
    if (wert) {
      router.push(`/lieder/?suche=${encodeURIComponent(wert)}&seite=1`);
      return;
    }
    router.push("/lieder/");
  }

  return (
    <form className="lieder-suche" onSubmit={absenden}>
      <label htmlFor={feldId}>Lieder suchen</label>
      <div className="lieder-suche-felder">
        <input
          id={feldId}
          name="suche"
          type="search"
          maxLength={200}
          placeholder="Titel, Urheber oder Textworte …"
          value={begriff}
          onChange={(event) => setBegriff(event.target.value)}
        />
        <button type="submit">Suchen</button>
      </div>
    </form>
  );
}
