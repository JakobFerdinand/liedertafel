"use client";

import { A2uiSurface, type ReactCatalogComponent } from "@a2ui/react/v0_9";
import { MessageProcessor, STRICT_VALIDATION } from "@a2ui/web_core/v0_9";
import { useEffect, useMemo, useRef } from "react";
import { archivKatalog } from "@/components/a2ui-katalog";
import { leseA2uiOberflaechen } from "@/lib/a2ui";

// Zeigt die A2UI-Oberflächen einer Antwort. Was nicht vollständig dem Katalog
// des Archivs entspricht, wird nicht gezeigt (siehe lib/a2ui.ts); die
// Bibliothek prüft danach ein zweites Mal streng, und eine Oberfläche, die sie
// ablehnt, entfällt ganz.
export default function A2uiOberflaechen({
  nachrichten,
  beiAnzeige,
}: {
  nachrichten: unknown;
  // Die Bibliothek lädt nach der Antwort nach und macht den Verlauf höher;
  // der Aufrufer erfährt, um wie viele Pixel, und kann nachscrollen.
  beiAnzeige?: (zuwachs: number) => void;
}) {
  const oberflaechen = useMemo(() => {
    const verarbeiter = new MessageProcessor<ReactCatalogComponent>(
      [archivKatalog],
      undefined,
      {
        validationConfig: STRICT_VALIDATION,
      },
    );
    for (const oberflaeche of leseA2uiOberflaechen(nachrichten)) {
      try {
        verarbeiter.processMessages(oberflaeche.nachrichten);
      } catch {
        verarbeiter.model.deleteSurface(oberflaeche.surfaceId);
      }
    }
    return Array.from(verarbeiter.model.surfacesMap.values());
  }, [nachrichten]);

  // Die Karten entstehen erst nach dem ersten Rendern der Bibliothek; jede
  // Höhenänderung meldet der Behälter, damit der Verlauf mitwandern kann.
  const behaelter = useRef<HTMLDivElement | null>(null);
  const sichtbar = oberflaechen.length > 0;
  useEffect(() => {
    const element = behaelter.current;
    if (!sichtbar || !element || !beiAnzeige) return;
    let letzteHoehe = 0;
    const melde = () => {
      const hoehe = element.offsetHeight;
      beiAnzeige(hoehe - letzteHoehe);
      letzteHoehe = hoehe;
    };
    melde();
    const beobachter = new ResizeObserver(melde);
    beobachter.observe(element);
    return () => beobachter.disconnect();
  }, [sichtbar, beiAnzeige]);

  if (oberflaechen.length === 0) return null;
  return (
    <div className="a2ui-oberflaechen" ref={behaelter}>
      {oberflaechen.map((oberflaeche) => (
        <A2uiSurface key={oberflaeche.id} surface={oberflaeche} />
      ))}
    </div>
  );
}
