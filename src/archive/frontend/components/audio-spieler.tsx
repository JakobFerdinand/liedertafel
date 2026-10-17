"use client";

import {
  MedienSpieler,
  type SpielerZugriff,
} from "@/components/medien-spieler";
import { type AssetAccessResponse, fetchAssetAccess } from "@/lib/assets";

export type AudioSpielerProps<
  Zugriff extends SpielerZugriff = AssetAccessResponse,
> = {
  assetId: string;
  /** Beschriftete Stimme (inklusive Vollmix-Ersatz), für Klartext-Labels. */
  stimme: string;
  zugriff: Zugriff;
  /** Holt ein frisches Ticket; ohne Angabe das Ticket des alten Materials. */
  holeZugriff?: () => Promise<Zugriff>;
  /** Nur ein Spieler läuft gleichzeitig; inaktive pausieren sich selbst. */
  aktiv: boolean;
  onAbspielen: () => void;
  onErneuert: (zugriff: Zugriff) => void;
};

/** Stimmaufnahme eines Liedes (ARC-018) auf dem gemeinsamen Spieler. */
export function AudioSpieler<
  Zugriff extends SpielerZugriff = AssetAccessResponse,
>({
  assetId,
  stimme,
  zugriff,
  holeZugriff,
  aktiv,
  onAbspielen,
  onErneuert,
}: AudioSpielerProps<Zugriff>) {
  return (
    <MedienSpieler
      kennung={assetId}
      art="audio"
      name={stimme}
      zugriff={zugriff}
      holeZugriff={
        holeZugriff ??
        (() => fetchAssetAccess(assetId) as unknown as Promise<Zugriff>)
      }
      aktiv={aktiv}
      onAbspielen={onAbspielen}
      onErneuert={onErneuert}
      ladeMeldung="Audio konnte nicht geladen werden. Bitte erneut versuchen."
      herunterladenErlaubt
      formatMeldung="Dieses Audioformat kann im Browser nicht wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden."
    />
  );
}
