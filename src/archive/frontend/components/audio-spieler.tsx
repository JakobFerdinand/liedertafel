"use client";

import { MedienSpieler } from "@/components/medien-spieler";
import { type AssetAccessResponse, fetchAssetAccess } from "@/lib/assets";

export type AudioSpielerProps = {
  assetId: string;
  /** Beschriftete Stimme (inklusive Vollmix-Ersatz), für Klartext-Labels. */
  stimme: string;
  zugriff: AssetAccessResponse;
  /** Nur ein Spieler läuft gleichzeitig; inaktive pausieren sich selbst. */
  aktiv: boolean;
  onAbspielen: () => void;
  onErneuert: (zugriff: AssetAccessResponse) => void;
};

/** Stimmaufnahme eines Liedes (ARC-018) auf dem gemeinsamen Spieler. */
export function AudioSpieler({
  assetId,
  stimme,
  zugriff,
  aktiv,
  onAbspielen,
  onErneuert,
}: AudioSpielerProps) {
  return (
    <MedienSpieler
      kennung={assetId}
      art="audio"
      name={stimme}
      zugriff={zugriff}
      holeZugriff={() => fetchAssetAccess(assetId)}
      aktiv={aktiv}
      onAbspielen={onAbspielen}
      onErneuert={onErneuert}
      ladeMeldung="Audio konnte nicht geladen werden. Bitte erneut versuchen."
      formatMeldung="Dieses Audioformat kann im Browser nicht wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden."
    />
  );
}
