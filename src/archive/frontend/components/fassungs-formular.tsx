"use client";

import { useState } from "react";
import { feldHerkunft, KiAbzeichen } from "@/components/ki-abzeichen";
import { patchAuth, postAuth } from "@/lib/auth";
import type { FeldHerkunft, LiedDetails } from "@/lib/songs";

type FassungsFormularAnfang = {
  label: string;
  person: string;
  zusatz: string;
  begleitung?: string;
};

type FassungsFormularProps = {
  variante: "arrangement" | "version";
  pfad: string;
  methode: "post" | "patch";
  idPraefix: string;
  anfang?: FassungsFormularAnfang;
  absendenText: string;
  onSuccess: (lied: LiedDetails, meldung: string) => void;
  /** ARC-013-1: gespeicherte Feldherkunft, Zeilenstand und Ziel (nur
   * Änderungen; Anlegen schreibt nichts Automatisches). */
  feldHerkunftListe?: FeldHerkunft[];
  zielId?: string;
  rowVersion?: number;
  neuLaden?: () => void;
};

export function FassungsFormular({
  variante,
  pfad,
  methode,
  idPraefix,
  anfang,
  absendenText,
  onSuccess,
  feldHerkunftListe,
  zielId,
  rowVersion,
  neuLaden,
}: FassungsFormularProps) {
  const [label, setLabel] = useState(anfang?.label ?? "");
  const [person, setPerson] = useState(anfang?.person ?? "");
  const [zusatz, setZusatz] = useState(anfang?.zusatz ?? "");
  const [begleitung, setBegleitung] = useState(anfang?.begleitung ?? "");
  const [labelFehler, setLabelFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);

  const personFeld = variante === "arrangement" ? "arranger" : "creator";
  const zusatzFeld =
    variante === "arrangement" ? "voiceConfiguration" : "musicalKey";
  const personLabel =
    variante === "arrangement" ? "Bearbeiter (optional)" : "Urheber (optional)";
  const zusatzLabel =
    variante === "arrangement"
      ? "Stimmkonfiguration (optional)"
      : "Tonart (optional)";
  const herkunftTyp =
    variante === "arrangement"
      ? ("arrangement" as const)
      : ("musical_version" as const);
  // ARC-013-1: Herkunftszeile pro Feld, nur bei einer Änderung.
  const herkunft = (feld: string) =>
    methode === "patch" && zielId
      ? feldHerkunft(feldHerkunftListe, herkunftTyp, zielId, feld)
      : undefined;

  async function speichern(event: React.FormEvent) {
    event.preventDefault();
    setLabelFehler("");
    setHinweis("");
    const bezeichnung = label.trim();
    if (!bezeichnung) {
      setLabelFehler("Bitte eine Bezeichnung eingeben.");
      return;
    }
    setBusy(true);
    try {
      const body: Record<string, unknown> = {
        label: bezeichnung,
        [personFeld]: person.trim() ? person.trim() : null,
        [zusatzFeld]: zusatz.trim() ? zusatz.trim() : null,
      };
      // Begleitung gibt es nur am Arrangement (ARC-023).
      if (variante === "arrangement")
        body.accompaniment = begleitung.trim() ? begleitung.trim() : null;
      // ARC-013-1: der optimistische Zeiger der bearbeiteten Zeile.
      if (methode === "patch" && rowVersion !== undefined)
        body.rowVersion = rowVersion;
      const response =
        methode === "patch"
          ? await patchAuth(pfad, body)
          : await postAuth(pfad, body);
      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      const gespeichert = payload?.song as LiedDetails | undefined;
      if (gespeichert) {
        onSuccess(
          gespeichert,
          variante === "arrangement"
            ? methode === "patch"
              ? "Arrangement gespeichert."
              : "Arrangement angelegt."
            : methode === "patch"
              ? "Fassung gespeichert."
              : "Fassung angelegt.",
        );
      } else {
        setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
      }
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={speichern} noValidate>
      <label htmlFor={`${idPraefix}-label`}>Bezeichnung</label>
      {herkunft("label") && (
        <div className="feld-abzeichen">
          <KiAbzeichen
            herkunft={herkunft("label")}
            feldbezeichnung="Bezeichnung"
            zurueckgeholt={neuLaden}
            meldungsziel={setHinweis}
          />
        </div>
      )}
      <input
        id={`${idPraefix}-label`}
        type="text"
        required
        maxLength={300}
        value={label}
        onChange={(event) => setLabel(event.target.value)}
        aria-invalid={labelFehler ? true : undefined}
        aria-describedby={`${idPraefix}-label-fehler`}
      />
      {labelFehler && (
        <p
          id={`${idPraefix}-label-fehler`}
          role="alert"
          className="feld-fehler"
        >
          {labelFehler}
        </p>
      )}
      <label htmlFor={`${idPraefix}-person`}>{personLabel}</label>
      {herkunft(personFeld === "arranger" ? "arranger" : "creator") && (
        <div className="feld-abzeichen">
          <KiAbzeichen
            herkunft={herkunft(
              personFeld === "arranger" ? "arranger" : "creator",
            )}
            feldbezeichnung={
              variante === "arrangement" ? "Bearbeiter" : "Urheber"
            }
            zurueckgeholt={neuLaden}
            meldungsziel={setHinweis}
          />
        </div>
      )}
      <input
        id={`${idPraefix}-person`}
        type="text"
        maxLength={300}
        value={person}
        onChange={(event) => setPerson(event.target.value)}
      />
      <label htmlFor={`${idPraefix}-zusatz`}>{zusatzLabel}</label>
      {herkunft(
        zusatzFeld === "voiceConfiguration"
          ? "voice_configuration"
          : "musical_key",
      ) && (
        <div className="feld-abzeichen">
          <KiAbzeichen
            herkunft={herkunft(
              zusatzFeld === "voiceConfiguration"
                ? "voice_configuration"
                : "musical_key",
            )}
            feldbezeichnung={
              variante === "arrangement" ? "Stimmkonfiguration" : "Tonart"
            }
            zurueckgeholt={neuLaden}
            meldungsziel={setHinweis}
          />
        </div>
      )}
      <input
        id={`${idPraefix}-zusatz`}
        type="text"
        maxLength={200}
        value={zusatz}
        onChange={(event) => setZusatz(event.target.value)}
      />
      {variante === "arrangement" && (
        <>
          <label htmlFor={`${idPraefix}-begleitung`}>
            Begleitung (optional)
          </label>
          {herkunft("accompaniment") && (
            <div className="feld-abzeichen">
              <KiAbzeichen
                herkunft={herkunft("accompaniment")}
                feldbezeichnung="Begleitung"
                zurueckgeholt={neuLaden}
                meldungsziel={setHinweis}
              />
            </div>
          )}
          <input
            id={`${idPraefix}-begleitung`}
            type="text"
            maxLength={200}
            value={begleitung}
            onChange={(event) => setBegleitung(event.target.value)}
          />
        </>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
      <div className="auth-aktionen">
        <button type="submit" disabled={busy}>
          {busy ? "Wird gespeichert …" : absendenText}
        </button>
      </div>
    </form>
  );
}
