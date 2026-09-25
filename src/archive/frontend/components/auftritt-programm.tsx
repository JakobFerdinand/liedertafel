"use client";

// ARC-026: Programm des Auftritts — Lesesaal für Mitglieder (geordnete
// Programmpunkte mit tieflinkender Liedseite) und Redaktions-Werkbank
// nach der Rezeptur des Notenbereichs: je Eintrag eine Zeile mit
// Fassungswahl, Notiz, Umordnen (↑/↓) und Entfernen; Speichern (PUT,
// ganzer geordneter Ersatz) und Veröffentlichen trennen Entwurf und
// Mitgliedssicht. Veraltete Stände laden den frischen Stand und bitten
// um Nacharbeit.

import Link from "next/link";
import { useEffect, useState } from "react";
import { FassungsWahl } from "@/components/fassungs-wahl";
import {
  type AuftrittDetailsMitProgramm,
  type ProgrammRevisionVeroeffentlicht,
  programmPunktUrl,
  publishedAtText,
  publishProgramm,
  putProgrammItems,
} from "@/lib/events";
import { fetchSong, fetchSongSearch, type LiedDetails } from "@/lib/songs";

const notizMaximal = 500;

const veralteteAenderung =
  "Der Programmentwurf wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.";

const ersatzFehler = "Das hat nicht geklappt. Bitte erneut versuchen.";

const liedLadeFehler =
  "Ein Lied konnte nicht geladen werden. Die Fassungsauswahl bleibt dann leer.";

const katalogFehler = "Der Katalog antwortet nicht. Bitte erneut versuchen.";

// Ein Werkzeile der Workbench: serverseitige Programmpunkt-Id (stabil
// über Speicherungen) oder lokale Entwurfskennung für neue Einträge.
type WerkZeile = {
  schluessel: string;
  serverId: string | null;
  songId: string | null;
  arrangementId: string | null;
  musicalVersionId: string | null;
  notiz: string;
};

/** Deutscher ProblemDetails-Titel aus einer geworfenen Antwort oder Ersatz. */
async function problemTitel(
  ursache: unknown,
  fallback: string,
): Promise<string> {
  if (ursache instanceof Response) {
    const inhalt = (await ursache.json().catch(() => null)) as {
      title?: unknown;
    } | null;
    if (inhalt && typeof inhalt.title === "string" && inhalt.title) {
      return inhalt.title;
    }
  }
  return fallback;
}

// ─── Lesesaal: die veröffentlichte Revision ──────────────────────────

function VeröffentlichtListe({
  revision,
}: {
  revision: ProgrammRevisionVeroeffentlicht;
}) {
  return (
    <ol className="programm-liste">
      {revision.items.map((punkt) => {
        const zeile = [
          punkt.arrangementLabel,
          punkt.musicalVersionLabel,
          punkt.musicalKey ? `Tonart: ${punkt.musicalKey}` : null,
          punkt.voiceConfiguration
            ? `Stimmkonfiguration: ${punkt.voiceConfiguration}`
            : null,
        ].filter((teil): teil is string => teil !== null);
        return (
          <li className="programm-eintrag" key={punkt.id}>
            <h4 className="programm-titel">
              <span className="programm-nummer" aria-hidden="true">
                {punkt.position}.
              </span>{" "}
              <Link href={programmPunktUrl(punkt)}>
                {punkt.songTitle ?? "Ohne Titel"}
              </Link>
            </h4>
            {zeile.length > 0 && (
              <p className="noten-info">{zeile.join(" · ")}</p>
            )}
            {punkt.note && <p className="programm-notiz">{punkt.note}</p>}
          </li>
        );
      })}
    </ol>
  );
}

// ─── Werkbank-Zeile ──────────────────────────────────────────────────

function ProgrammZeile({
  zeile,
  stelle,
  erste,
  letzte,
  lied,
  liedFehler,
  liedLaeuft,
  onFassungGewaehlt,
  onNotiz,
  onHoch,
  onRunter,
  onEntfernen,
}: {
  zeile: WerkZeile;
  stelle: number;
  erste: boolean;
  letzte: boolean;
  lied: LiedDetails | null;
  liedFehler: string;
  liedLaeuft: boolean;
  onFassungGewaehlt: (arrangementId: string, versionId: string) => void;
  onNotiz: (wert: string) => void;
  onHoch: () => void;
  onRunter: () => void;
  onEntfernen: () => void;
}) {
  const idPraefix = `programm-zeile-${zeile.schluessel}`;
  const titel = lied?.title ?? "Lied wird geladen …";
  return (
    <li className="programm-zeile">
      <div className="programm-zeile-kopf">
        <span className="programm-zeile-nummer" aria-hidden="true">
          {stelle}.
        </span>
        <span className="programm-zeile-titel">{titel}</span>
        <span className="material-datei-status" aria-live="polite">
          {liedFehler || (lied ? "" : liedLaeuft ? "wird geladen …" : "")}
        </span>
      </div>
      <div className="programm-zeile-werkzeuge">
        <button
          type="button"
          className="knopf-leise programm-umordnen"
          aria-label={`Eintrag ${stelle} nach oben schieben`}
          disabled={erste}
          onClick={onHoch}
        >
          <span aria-hidden="true">↑</span>
        </button>
        <button
          type="button"
          className="knopf-leise programm-umordnen"
          aria-label={`Eintrag ${stelle} nach unten schieben`}
          disabled={letzte}
          onClick={onRunter}
        >
          <span aria-hidden="true">↓</span>
        </button>
        <button
          type="button"
          className="knopf-leise"
          aria-label={`Eintrag ${stelle} entfernen`}
          onClick={onEntfernen}
        >
          Entfernen
        </button>
      </div>
      {lied ? (
        <FassungsWahl
          lied={lied}
          arrangementId={zeile.arrangementId ?? ""}
          versionId={zeile.musicalVersionId ?? ""}
          onSelect={onFassungGewaehlt}
        />
      ) : (
        <p className="feld-hinweis">
          {liedLaeuft ? "Lied wird geladen …" : liedFehler}
        </p>
      )}
      <div className="programm-zeile-notiz">
        <label htmlFor={`${idPraefix}-notiz`}>Notiz (optional)</label>
        <input
          id={`${idPraefix}-notiz`}
          type="text"
          maxLength={notizMaximal}
          value={zeile.notiz}
          onChange={(event) => onNotiz(event.target.value)}
        />
      </div>
    </li>
  );
}

// ─── Liedauswahl aus dem Katalog ─────────────────────────────────────

function LiedWahl({ onGewaehlt }: { onGewaehlt: (songId: string) => void }) {
  const [suche, setSuche] = useState("");
  const [ergebnisse, setErgebnisse] = useState<
    Array<{ id: string; title: string }>
  >([]);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(false);

  async function suchen(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setFehler("");
    try {
      const antwort = await fetchSongSearch(suche.trim() || null, 1);
      setErgebnisse(
        antwort.songs.map((eintrag) => ({
          id: eintrag.id,
          title: eintrag.title,
        })),
      );
      if (antwort.songs.length === 0) setFehler("Kein Lied gefunden.");
    } catch {
      setFehler(katalogFehler);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="programm-lied-wahl"
      onSubmit={(event) => void suchen(event)}
    >
      <label htmlFor="programm-lied-suche">Lied aus dem Katalog suchen</label>
      <div className="programm-lied-suche-reihe">
        <input
          id="programm-lied-suche"
          type="search"
          value={suche}
          onChange={(event) => setSuche(event.target.value)}
        />
        <button type="submit" disabled={busy}>
          {busy ? "Wird gesucht …" : "Suchen"}
        </button>
      </div>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {ergebnisse.length > 0 && (
        <div className="programm-lied-treffer">
          {ergebnisse.map((eintrag) => (
            <button
              key={eintrag.id}
              type="button"
              className="knopf-leise-rahmen"
              onClick={() => {
                onGewaehlt(eintrag.id);
                setErgebnisse([]);
                setSuche("");
              }}
            >
              {eintrag.title}
            </button>
          ))}
        </div>
      )}
    </form>
  );
}

// ─── Der Bereich ─────────────────────────────────────────────────────

export function AuftrittProgramm({
  auftritt,
  isEditor,
  aktualisieren,
}: {
  auftritt: AuftrittDetailsMitProgramm;
  isEditor: boolean;
  aktualisieren: () => void;
}) {
  const programm = auftritt.programme ?? null;
  const veroeffentlicht = programm?.published ?? null;
  const entwurf = programm?.working ?? null;

  const [ausgeklappt, setAusgeklappt] = useState(false);
  const [zeilen, setZeilen] = useState<WerkZeile[]>([]);
  const [lieder, setLieder] = useState<Record<string, LiedDetails | null>>({});
  const [liedFehler, setLiedFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [speichernBusy, setSpeichernBusy] = useState(false);
  const [veröffentlichenBusy, setVeröffentlichenBusy] = useState(false);

  // Beim Aufklappen übersetzt der Entwurf sich in die Werkzeilen; nach
  // jedem frischen Programmstand (Speichern, 409-Nachladen, Veröffent-
  // lichen) folgt der Bestand neu — so überleben stabile Ids und die
  // Redaktion sieht den Stand, den der Server wirklich kennt.
  useEffect(() => {
    if (!ausgeklappt) return;
    setZeilen(
      (entwurf?.items ?? []).map((punkt) => ({
        schluessel: punkt.id,
        serverId: punkt.id,
        songId: punkt.songId,
        arrangementId: punkt.arrangementId,
        musicalVersionId: punkt.musicalVersionId,
        notiz: punkt.note ?? "",
      })),
    );
  }, [ausgeklappt, entwurf]);

  // Lieddetails je betroffenem Eintrag laden (FassungsWahl braucht sie);
  // schon geladene Lieder bleiben im Bestand, nur neue Ids werden geholt.
  useEffect(() => {
    if (!ausgeklappt) return;
    const fehlende = [
      ...new Set(
        zeilen
          .map((zeile) => zeile.songId)
          .filter((id): id is string => id !== null && !(id in lieder)),
      ),
    ];
    if (fehlende.length === 0) return;
    const abbruch = new AbortController();
    void Promise.all(
      fehlende.map(async (id) => {
        try {
          return [id, await fetchSong(id, abbruch.signal)] as const;
        } catch {
          return [id, null] as const;
        }
      }),
    ).then((paare) => {
      if (abbruch.signal.aborted) return;
      setLieder((bisher) => {
        const stand = { ...bisher };
        for (const [id, lied] of paare) stand[id] = lied;
        return stand;
      });
      if (paare.some(([, lied]) => lied === null))
        setLiedFehler(liedLadeFehler);
    });
    return () => abbruch.abort();
  }, [ausgeklappt, zeilen, lieder]);

  function neuerEintrag(songId: string) {
    setLiedFehler("");
    setZeilen((bisher) => [
      ...bisher,
      {
        schluessel: crypto.randomUUID(),
        serverId: null,
        songId,
        arrangementId: null,
        musicalVersionId: null,
        notiz: "",
      },
    ]);
  }

  function umordnen(index: number, richtung: -1 | 1) {
    setZeilen((bisher) => {
      const ziel = index + richtung;
      if (ziel < 0 || ziel >= bisher.length) return bisher;
      const stand = [...bisher];
      const [eintrag] = stand.splice(index, 1);
      stand.splice(ziel, 0, eintrag);
      return stand;
    });
  }

  function entfernen(schluessel: string) {
    setZeilen((bisher) =>
      bisher.filter((zeile) => zeile.schluessel !== schluessel),
    );
  }

  function fassungGewaehlt(
    schluessel: string,
    arrangementId: string,
    versionId: string,
  ) {
    setZeilen((bisher) =>
      bisher.map((zeile) =>
        zeile.schluessel === schluessel
          ? { ...zeile, arrangementId, musicalVersionId: versionId }
          : zeile,
      ),
    );
  }

  async function speichern() {
    if (zeilen.some((zeile) => !zeile.songId || !zeile.musicalVersionId)) {
      setHinweis(
        "Jeder Eintrag braucht eine Liedfassung, bevor der Entwurf gespeichert werden kann.",
      );
      return;
    }
    setSpeichernBusy(true);
    setHinweis("");
    try {
      // Ohne Programm legt der erste PUT es still an (rowVersion
      // entfällt); mit Programm zählt sie als Concurrency-Anker. Die
      // frische Einbettung (mit den neuen Ids) kommt über aktualisieren().
      await putProgrammItems(auftritt.id, {
        items: zeilen.map((zeile) => ({
          ...(zeile.serverId ? { id: zeile.serverId } : {}),
          songId: zeile.songId ?? "",
          musicalVersionId: zeile.musicalVersionId ?? "",
          note: zeile.notiz.trim(),
        })),
        ...(programm ? { rowVersion: programm.rowVersion } : {}),
      });
      setErfolg("Programmentwurf gespeichert.");
      setHinweis("");
      // Der frische Stand (mit neuen Ids) kommt über aktualisieren().
      aktualisieren();
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 409) {
        // Veralteter Stand: der frische kommt über aktualisieren(); die
        // Redaktion prüft ihn und wiederholt die Änderung (Vertragssprache).
        setHinweis(veralteteAenderung);
        aktualisieren();
        return;
      }
      setHinweis(await problemTitel(ursache, ersatzFehler));
    } finally {
      setSpeichernBusy(false);
    }
  }

  async function veroeffentlichen() {
    if (programm === null) {
      setHinweis("Es liegt noch kein Programmentwurf vor.");
      return;
    }
    setVeröffentlichenBusy(true);
    setHinweis("");
    try {
      await publishProgramm(auftritt.id, programm.rowVersion);
      setErfolg("Programm veröffentlicht. Mitglieder sehen es ab sofort.");
      setHinweis("");
      aktualisieren();
    } catch (ursache) {
      // Auch der Veröffentlichungssatz kennt den veralteten Stand (409)
      // und die nicht verfügbaren Fassungen; die Vertragssprache erklärt
      // beides schon.
      setHinweis(await problemTitel(ursache, ersatzFehler));
      if (ursache instanceof Response && ursache.status === 409) {
        aktualisieren();
      }
    } finally {
      setVeröffentlichenBusy(false);
    }
  }

  const hatVeroeffentlicht = veroeffentlicht !== null;

  return (
    <section
      className="auftritt-abschnitt auftritt-programm"
      aria-labelledby="auftritt-programm-titel"
    >
      <h3 id="auftritt-programm-titel">Programm</h3>
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
      {/* Lesesaal: die veröffentlichte Revision liest sich für alle;
          der Entwurf bleibt den Werkbank-Werkzeugen vorbehalten. */}
      {veroeffentlicht ? (
        <>
          <VeröffentlichtListe revision={veroeffentlicht} />
          <p className="noten-info">
            Veröffentlicht am {publishedAtText(veroeffentlicht.publishedAt)}
          </p>
        </>
      ) : (
        <p className="auftritt-leer">Das Programm wurde noch nicht erfasst.</p>
      )}
      {isEditor && (
        <details
          className="programm-verwaltung"
          aria-labelledby="programm-verwaltung-titel"
          onToggle={(event) =>
            setAusgeklappt((event.target as HTMLDetailsElement).open)
          }
        >
          <summary>
            <h4 id="programm-verwaltung-titel">Programm verwalten</h4>
            <span className="programm-verwaltung-umschalter" aria-hidden="true">
              <span className="programm-verwaltung-auf">Ausklappen</span>
              <span className="programm-verwaltung-zu">Einklappen</span>
            </span>
          </summary>
          <fieldset
            className="programm-verwaltung"
            aria-label="Programm verwalten"
          >
            {ausgeklappt && (
              <>
                {hatVeroeffentlicht && entwurf === null && (
                  <p className="noten-info">
                    Es liegt kein offener Entwurf vor. Das nächste Speichern
                    beginnt einen frischen Entwurf; die veröffentlichte Fassung
                    bleibt unverändert stehen.
                  </p>
                )}
                {zeilen.length === 0 ? (
                  <p className="auftritt-leer">
                    Der Entwurf ist leer. Such ein Lied aus dem Katalog und füge
                    es mit seiner Fassung hinzu.
                  </p>
                ) : (
                  <ul className="programm-zeilen">
                    {zeilen.map((zeile, index) => (
                      <ProgrammZeile
                        key={zeile.schluessel}
                        zeile={zeile}
                        stelle={index + 1}
                        erste={index === 0}
                        letzte={index === zeilen.length - 1}
                        lied={
                          zeile.songId ? (lieder[zeile.songId] ?? null) : null
                        }
                        liedFehler={liedFehler}
                        liedLaeuft={
                          zeile.songId !== null && !(zeile.songId in lieder)
                        }
                        onFassungGewaehlt={(arrangementId, versionId) =>
                          fassungGewaehlt(
                            zeile.schluessel,
                            arrangementId,
                            versionId,
                          )
                        }
                        onNotiz={(wert) =>
                          setZeilen((bisher) =>
                            bisher.map((eintrag) =>
                              eintrag.schluessel === zeile.schluessel
                                ? { ...eintrag, notiz: wert }
                                : eintrag,
                            ),
                          )
                        }
                        onHoch={() => umordnen(index, -1)}
                        onRunter={() => umordnen(index, 1)}
                        onEntfernen={() => entfernen(zeile.schluessel)}
                      />
                    ))}
                  </ul>
                )}
                <LiedWahl onGewaehlt={neuerEintrag} />
                <div className="noten-aktionen">
                  <button
                    type="button"
                    disabled={speichernBusy}
                    onClick={() => void speichern()}
                  >
                    {speichernBusy ? "Wird gespeichert …" : "Entwurf speichern"}
                  </button>
                  <button
                    type="button"
                    disabled={veröffentlichenBusy}
                    onClick={() => void veroeffentlichen()}
                  >
                    {veröffentlichenBusy
                      ? "Wird veröffentlicht …"
                      : "Veröffentlichen"}
                  </button>
                </div>
              </>
            )}
          </fieldset>
        </details>
      )}
    </section>
  );
}
