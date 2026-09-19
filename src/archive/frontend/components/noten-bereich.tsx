"use client";

import { useRef, useState } from "react";
import {
  type AssetAccessResponse,
  createAsset,
  createUploadSession,
  fetchAssetAccess,
  finalizeUpload,
} from "@/lib/assets";
import type { LiedAsset } from "@/lib/songs";

type NotenBereichProps = {
  fassungId: string;
  assets: LiedAsset[];
  isEditor: boolean;
  aktualisieren: () => void;
};

type UploadSchritt = "vorbereitet" | "uebertragen" | "geprueft";

const startMeldung =
  "Das Hochladen konnte nicht gestartet werden. Bitte erneut versuchen.";

function groesseText(sizeBytes: number): string {
  const format = new Intl.NumberFormat("de-AT", {
    maximumFractionDigits: 1,
  });
  if (sizeBytes >= 102400) return `${format.format(sizeBytes / 1048576)} MB`;
  if (sizeBytes >= 1024) return `${format.format(sizeBytes / 1024)} kB`;
  return `${format.format(sizeBytes)} B`;
}

function typText(contentType: string): string {
  return contentType.includes("pdf") ? "PDF" : contentType;
}

function abschlussFehler(ursache: unknown): string {
  if (ursache instanceof Response) {
    if (ursache.status === 409) {
      return "Die Datei wurde noch nicht übertragen. Bitte erneut hochladen.";
    }
    if (ursache.status === 413) return "Die Datei ist zu groß.";
    if (ursache.status === 422) return "Die Datei ist kein gültiges PDF.";
    if (ursache.status === 502) {
      return "Der Speicherdienst ist zurzeit nicht erreichbar. Bitte erneut versuchen.";
    }
  }
  return "Abschluss fehlgeschlagen. Bitte erneut versuchen.";
}

export function NotenBereich({
  fassungId,
  assets,
  isEditor,
  aktualisieren,
}: NotenBereichProps) {
  const eingabeRef = useRef<HTMLInputElement>(null);
  const [zugriff, setZugriff] = useState<AssetAccessResponse | null>(null);
  const [zugriffBusy, setZugriffBusy] = useState(false);
  const [zugriffFehler, setZugriffFehler] = useState("");
  const [uploadSchritt, setUploadSchritt] = useState<UploadSchritt | null>(
    null,
  );
  const [uploadFehler, setUploadFehler] = useState("");
  const [uploadErfolg, setUploadErfolg] = useState("");

  if (fassungId === "") return null;

  const noten = assets.find((asset) => asset.assetType === "score") ?? null;
  const revision = noten?.currentRevision ?? null;

  async function notenAnzeigen() {
    if (!noten) return;
    setZugriffBusy(true);
    setZugriffFehler("");
    try {
      setZugriff(await fetchAssetAccess(noten.id));
    } catch (ursache) {
      setZugriff(null);
      if (ursache instanceof Response && ursache.status === 404) {
        setZugriffFehler(
          "Für diese Fassung liegen noch keine aktuellen Noten vor.",
        );
      } else {
        setZugriffFehler(
          "Noten konnten nicht geladen werden. Bitte erneut versuchen.",
        );
      }
    } finally {
      setZugriffBusy(false);
    }
  }

  async function hochladen(datei: File) {
    setUploadFehler("");
    setUploadErfolg("");
    setUploadSchritt("vorbereitet");
    try {
      const leereFassung = assets.find(
        (asset) =>
          asset.assetType === "score" && asset.currentRevision === null,
      );
      const assetId = leereFassung
        ? leereFassung.id
        : (await createAsset(fassungId)).id;
      const sitzung = await createUploadSession(assetId);
      if (datei.size > sitzung.maxBytes) {
        setUploadFehler("Die Datei ist zu groß.");
        return;
      }
      setUploadSchritt("uebertragen");
      let uebertragung: Response;
      try {
        uebertragung = await fetch(sitzung.uploadUrl, {
          method: "PUT",
          headers: { "Content-Type": "application/pdf" },
          body: datei,
        });
      } catch {
        setUploadFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
        return;
      }
      if (!uebertragung.ok) {
        setUploadFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
        return;
      }
      setUploadSchritt("geprueft");
      try {
        await finalizeUpload(sitzung.uploadSessionId);
      } catch (ursache) {
        setUploadFehler(abschlussFehler(ursache));
        return;
      }
      setUploadErfolg("Noten veröffentlicht.");
      aktualisieren();
    } catch {
      setUploadFehler(startMeldung);
    } finally {
      setUploadSchritt(null);
    }
  }

  function waehlen(event: React.ChangeEvent<HTMLInputElement>) {
    const datei = event.target.files?.[0];
    event.target.value = "";
    if (!datei) return;
    void hochladen(datei);
  }

  const laedNoten = zugriffBusy
    ? "Noten werden vorbereitet …"
    : "Noten anzeigen";
  const uploadText =
    uploadSchritt === "vorbereitet"
      ? "Noten werden vorbereitet …"
      : uploadSchritt === "uebertragen"
        ? "Wird übertragen …"
        : uploadSchritt === "geprueft"
          ? "Wird geprüft …"
          : "";

  return (
    <section className="noten-bereich" aria-label="Noten">
      {isEditor && <h3 id="noten-titel">Noten</h3>}
      {revision && (
        <>
          <p className="noten-info">
            Noten · Fassung {revision.revisionNumber} ·{" "}
            {groesseText(revision.sizeBytes)} · {typText(revision.contentType)}
          </p>
          <div className="noten-aktionen">
            <button
              type="button"
              onClick={notenAnzeigen}
              disabled={zugriffBusy}
            >
              {laedNoten}
            </button>
            {zugriff && (
              <a className="noten-laden" href={zugriff.downloadUrl} download>
                Herunterladen
              </a>
            )}
          </div>
          {zugriff && (
            <iframe
              className="noten-ansicht"
              src={zugriff.viewUrl}
              title="Noten (PDF)"
            />
          )}
        </>
      )}
      {zugriffFehler && (
        <output aria-live="polite" className="feld-fehler">
          {zugriffFehler}
        </output>
      )}
      {isEditor && (
        <div className="noten-verwaltung">
          {uploadFehler && (
            <output aria-live="polite" className="feld-fehler">
              {uploadFehler}
            </output>
          )}
          {uploadErfolg && (
            <output aria-live="polite" className="auth-erfolg">
              {uploadErfolg}
            </output>
          )}
          {uploadText && (
            <p className="feld-hinweis" aria-live="polite">
              {uploadText}
            </p>
          )}
          <div className="noten-aktionen">
            <button
              type="button"
              onClick={() => eingabeRef.current?.click()}
              disabled={uploadSchritt !== null}
            >
              {revision ? "Neue Fassung hochladen" : "Noten hochladen"}
            </button>
            <input
              ref={eingabeRef}
              className="visually-hidden"
              type="file"
              accept="application/pdf,.pdf"
              onChange={waehlen}
              disabled={uploadSchritt !== null}
              tabIndex={-1}
              aria-hidden="true"
            />
          </div>
        </div>
      )}
    </section>
  );
}
