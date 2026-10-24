"use client";

import { useEffect, useState } from "react";
import { fetchMe, type MeResponse } from "@/lib/auth";
import { fetchKiKosten, type KiKostenUeberblick } from "@/lib/ki-kosten";

function eur(wert: number): string {
  return `${wert.toLocaleString("de-AT", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })} EUR`;
}

function istRedaktion(me: MeResponse): boolean {
  return (
    me.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"))
  );
}

/**
 * KI-Kostenüberblick der Verwaltung (ARC-043, #150): der Verbrauch des
 * laufenden Monats je Feature aus dem Verbrauchsbuch, die harte
 * Monatsgrenze und die Schwellwerte, deren Begrenzungsstand bereits gemeldet
 * ist. Reine Lesesicht — es verbraucht nichts.
 */
export function KiKosten() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [stand, setStand] = useState<KiKostenUeberblick | null>(null);
  const [fehler, setFehler] = useState("");

  useEffect(() => {
    const abbruch = new AbortController();
    (async () => {
      const aktuell = await fetchMe(abbruch.signal);
      setMe(aktuell);
      if (istRedaktion(aktuell)) {
        setStand(await fetchKiKosten(abbruch.signal));
      }
    })().catch(() => {
      if (!abbruch.signal.aborted) {
        setFehler("Der KI-Kostenüberblick konnte nicht geladen werden.");
      }
    });
    return () => abbruch.abort();
  }, []);

  if (fehler) return <p className="feld-fehler">{fehler}</p>;
  if (me === null || stand === null)
    return <p aria-live="polite">KI-Kosten werden geladen …</p>;
  if (!istRedaktion(me)) {
    return <p>Der KI-Kostenüberblick ist nur für die Redaktion sichtbar.</p>;
  }

  const schwellen = stand.thresholdsEur
    .filter((t) => t < stand.capEur)
    .map((t) => ({
      wert: t,
      gemeldet: stand.notifiedEur >= t || stand.spentEur >= stand.capEur,
    }));

  return (
    <section className="ki-kosten" aria-label="KI-Kosten des laufenden Monats">
      <h2>KI-Kosten {stand.month}</h2>
      <p className="ki-kosten-stand">
        Verbrauch: <strong>{eur(stand.spentEur)}</strong> von{" "}
        {eur(stand.capEur)} Monatsgrenze.
      </p>
      {schwellen.length > 0 && (
        <p className="ki-kosten-schwellen">
          Schwellwerte:{" "}
          {schwellen
            .map((t) => `${eur(t.wert)}${t.gemeldet ? " (gemeldet)" : ""}`)
            .join(", ")}
          . Die Monatsgrenze meldet sich selbst, sobald sie erreicht ist.
        </p>
      )}
      {stand.features.length === 0 ? (
        <p className="noten-info">In diesem Monat ruht jede KI-Nutzung.</p>
      ) : (
        <table className="ki-kosten-tabelle">
          <caption className="visually-hidden">
            KI-Verbrauch je Feature im Monat {stand.month}
          </caption>
          <thead>
            <tr>
              <th scope="col">Feature</th>
              <th scope="col">Modell</th>
              <th scope="col">Aufrufe</th>
              <th scope="col">Eingabe-/Ausgabe-Token</th>
              <th scope="col">Verbrauch</th>
            </tr>
          </thead>
          <tbody>
            {stand.features.map((eintrag) => (
              <tr key={`${eintrag.feature}-${eintrag.model}`}>
                <td>{eintrag.feature}</td>
                <td>{eintrag.model}</td>
                <td>{eintrag.calls}</td>
                <td>
                  {eintrag.inputTokens} / {eintrag.outputTokens}
                </td>
                <td>{eur(eintrag.spentEur)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="ki-kosten-hinweis">
        Die harte Monatsgrenze gilt für alle KI-Nutzung zusammen (Archiv: 15 EUR
        im Monat); sie ist vom EUR-10-Ziel der übrigen Azure-Kosten getrennt.
        Schwellwerte und Meldeweg stehen in der Infrastruktur, siehe
        infrastructure/archive/README.md, „Kostenübersicht (ARC-043)“.
      </p>
    </section>
  );
}
