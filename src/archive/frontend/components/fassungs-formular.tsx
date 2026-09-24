"use client";

import { useState } from "react";
import { patchAuth, postAuth } from "@/lib/auth";
import type { LiedDetails } from "@/lib/songs";

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
};

export function FassungsFormular({
  variante,
  pfad,
  methode,
  idPraefix,
  anfang,
  absendenText,
  onSuccess,
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
      <input
        id={`${idPraefix}-person`}
        type="text"
        maxLength={300}
        value={person}
        onChange={(event) => setPerson(event.target.value)}
      />
      <label htmlFor={`${idPraefix}-zusatz`}>{zusatzLabel}</label>
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
