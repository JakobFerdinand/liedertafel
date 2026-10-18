"use client";

import {
  MedienSpieler,
  type SpielerZugriff,
} from "@/components/medien-spieler";

export type AudioSpielerProps<Zugriff extends SpielerZugriff> = {
  dateiId: string;
  /** Beschriftete Stimme (inklusive Vollmix-Ersatz), für Klartext-Labels. */
  stimme: string;
  zugriff: Zugriff;
  /** Holt ein frisches Ticket. */
  holeZugriff: () => Promise<Zugriff>;
  /** Nur ein Spieler läuft gleichzeitig; inaktive pausieren sich selbst. */
  aktiv: boolean;
  onAbspielen: () => void;
  onErneuert: (zugriff: Zugriff) => void;
};

/** Audio-Datei eines Liedes auf dem gemeinsamen Spieler. */
export function AudioSpieler<Zugriff extends SpielerZugriff>({
  dateiId,
  stimme,
  zugriff,
  holeZugriff,
  aktiv,
  onAbspielen,
  onErneuert,
}: AudioSpielerProps<Zugriff>) {
  return (
    <MedienSpieler
      kennung={dateiId}
      art="audio"
      name={stimme}
      zugriff={zugriff}
      holeZugriff={holeZugriff}
      aktiv={aktiv}
      onAbspielen={onAbspielen}
      onErneuert={onErneuert}
      ladeMeldung="Audio konnte nicht geladen werden. Bitte erneut versuchen."
      herunterladenErlaubt
      formatMeldung="Dieses Audioformat kann im Browser nicht wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden."
    />
  );
}
