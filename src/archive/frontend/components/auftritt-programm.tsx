"use client";

// ARC-026: Programm des Auftritts — Lesesaal für Mitglieder (geordnete
// Programmpunkte mit tieflinkender Liedseite) und Redaktions-Werkbank
// nach der Rezeptur des Notenbereichs: je Eintrag eine Zeile mit
// Fassungswahl, Notiz, Umordnen (↑/↓) und Entfernen; Speichern (PUT,
// ganzer geordneter Ersatz) und Veröffentlichen trennen Entwurf und
// Mitgliedssicht. Veraltete Stände laden den frischen Stand und bitten
// um Nacharbeit.
//
// Die Werkzeilen folgen der Programm-rowVersion: eigenes Speichern und
// Veröffentlichen übernehmen die frische Einbettung der Antwort sofort,
// Geschwister-Nachladen mit unveränderter Version lässt ungespeicherte
// Arbeit unberührt. Nach einer Veröffentlichung startet der Entwurf als
// Kopie der veröffentlichten Liste — der nächste PUT legt jeden
// Eintrag neu an (Vertragssprache).
//
// ARC-027: die Werkbank zeigt ehrliche Stände — Entwurf und
// Mitgliedersicht mit Revision und Zeitpunkt, darunter die eingefrorenen
// früheren Fassungen. Ersetzt ein frischer Server-Stand unerledigte
// Arbeit, erklärt die Werkbank die Ersetzung statt sie stillschweigend
// zu schlucken.

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { FassungsWahl } from "@/components/fassungs-wahl";
import { problemTitel } from "@/lib/assets";
import {
  type AuftrittDetailsMitProgramm,
  type ProgrammRevision,
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

const ungespeicherteAenderungen =
  "Es gibt nicht gespeicherte Änderungen. Speichere den Entwurf erst, um sie zu veröffentlichen.";

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

// Zuletzt synchronisierter Server-Stand als Zeilenprojektion; Grundlage
// der Prüfung, ob ungespeicherte Arbeit vorliegt.
type SynchronZeile = {
  serverId: string | null;
  songId: string;
  musicalVersionId: string;
  notiz: string;
};

/**
 * Zeilen der Werkbank aus dem Server-Stand: mit Arbeitsrevision tragen
 * sie die stabilen Programmpunkt-Ids; ohne Arbeitsrevision (nach einer
 * Veröffentlichung) startet der Entwurf als Kopie der veröffentlichten
 * Liste — die Zeilen tragen keine Server-Ids, der nächste PUT legt
 * jeden Eintrag neu an (Vertragssprache).
 */
function zeilenAusRevisionen(
  working: ProgrammRevision | null,
  published: ProgrammRevisionVeroeffentlicht | null,
): { zeilen: WerkZeile[]; kopie: boolean } {
  if (working !== null) {
    return {
      kopie: false,
      zeilen: working.items.map((punkt) => ({
        schluessel: punkt.id,
        serverId: punkt.id,
        songId: punkt.songId,
        arrangementId: punkt.arrangementId,
        musicalVersionId: punkt.musicalVersionId,
        notiz: punkt.note ?? "",
      })),
    };
  }
  // Ohne Arbeitsrevision (nach einer Veröffentlichung) startet der
  // Entwurf als Kopie der veröffentlichten Liste: die Zeilen tragen
  // keine Server-Ids, der nächste PUT legt jeden Eintrag neu an.
  return {
    kopie: published !== null,
    zeilen: (published?.items ?? []).map((punkt) => ({
      schluessel: punkt.id,
      serverId: null,
      songId: punkt.songId,
      arrangementId: punkt.arrangementId,
      musicalVersionId: punkt.musicalVersionId,
      notiz: punkt.note ?? "",
    })),
  };
}

/** Zeilenprojektion eines Standes für den Vergleich mit der Arbeit. */
function synchronstandAusZeilen(zeilen: WerkZeile[]): SynchronZeile[] {
  return zeilen.map((zeile) => ({
    serverId: zeile.serverId,
    songId: zeile.songId ?? "",
    musicalVersionId: zeile.musicalVersionId ?? "",
    notiz: zeile.notiz,
  }));
}

/**
 * Ungespeicherte Arbeit: die Zeilen weichen vom zuletzt synchronisierten
 * Server-Stand ab (Id-Vorhandensein, Reihenfolge, Liedfassung, Notiz).
 */
function ungespeichert(
  aktuelleZeilen: WerkZeile[],
  bekannterStand: SynchronZeile[],
): boolean {
  if (aktuelleZeilen.length !== bekannterStand.length) return true;
  return aktuelleZeilen.some((zeile, index) => {
    const bekannt = bekannterStand[index];
    return (
      (zeile.serverId !== null) !== (bekannt.serverId !== null) ||
      zeile.songId !== bekannt.songId ||
      zeile.musicalVersionId !== bekannt.musicalVersionId ||
      zeile.notiz.trim() !== bekannt.notiz.trim()
    );
  });
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
  onErneutVersuchen,
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
  onErneutVersuchen: () => void;
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
        {/* Eine ankündigende Stelle je Zeile: der Fehler läuft hier mit
            und wiederholt sich nicht über den ganzen Bereich. */}
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
        <div>
          <p className="feld-hinweis">
            {liedLaeuft ? "Lied wird geladen …" : liedFehler}
          </p>
          {!liedLaeuft && liedFehler && (
            <button
              type="button"
              className="knopf-leise"
              onClick={onErneutVersuchen}
            >
              Erneut versuchen
            </button>
          )}
        </div>
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
  // Suche in fester Reihenfolge: jede neue Suche bricht die vorige ab
  // und zählt weiter; ältere Antworten dürfen jüngere Ergebnisse nicht
  // überschreiben, und nach dem Einklappen wird kein Zustand mehr
  // gesetzt.
  const laufNummer = useRef(0);
  const laufendeAbbruch = useRef<AbortController | null>(null);
  const lebt = useRef(true);

  useEffect(() => {
    lebt.current = true;
    return () => {
      lebt.current = false;
      laufendeAbbruch.current?.abort();
    };
  }, []);

  async function suchen(event: React.FormEvent) {
    event.preventDefault();
    const nummer = laufNummer.current + 1;
    laufNummer.current = nummer;
    laufendeAbbruch.current?.abort();
    const abbruch = new AbortController();
    laufendeAbbruch.current = abbruch;
    setBusy(true);
    setFehler("");
    try {
      const antwort = await fetchSongSearch(
        suche.trim() || null,
        1,
        abbruch.signal,
      );
      if (!lebt.current || nummer !== laufNummer.current) return;
      setErgebnisse(
        antwort.songs.map((eintrag) => ({
          id: eintrag.id,
          title: eintrag.title,
        })),
      );
      if (antwort.songs.length === 0) setFehler("Kein Lied gefunden.");
    } catch {
      // Abgebrochene oder überholte Antworten ändern nichts mehr.
      if (!lebt.current || nummer !== laufNummer.current) return;
      setFehler(katalogFehler);
    } finally {
      if (lebt.current && nummer === laufNummer.current) setBusy(false);
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
  const [synchronstand, setSynchronstand] = useState<SynchronZeile[]>([]);
  // true, wenn die Zeilen als Kopie der veröffentlichten Liste starten
  // (nach einer Veröffentlichung, noch ohne eigenen Entwurf).
  const [entwurfKopie, setEntwurfKopie] = useState(false);
  // rowVersion des Programmstands, aus dem die Zeilen zuletzt abgeleitet
  // wurden; Anker für Concurrency und Resync (null = ohne Programm).
  const [letzteGeladeneVersion, setLetzteGeladeneVersion] = useState<
    number | null
  >(null);
  const [lieder, setLieder] = useState<Record<string, LiedDetails | "fehler">>(
    {},
  );
  const [liedFehler, setLiedFehler] = useState<Record<string, string>>({});
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [speichernBusy, setSpeichernBusy] = useState(false);
  const [veröffentlichenBusy, setVeröffentlichenBusy] = useState(false);

  // ARC-027: ungespeicherte Arbeit meldet sich an der Werkbank, sobald
  // die Zeilen vom synchronisierten Stand abweichen — sie legt sich nach
  // dem Speichern wieder still.
  const ungespeicherteArbeit = ungespeichert(zeilen, synchronstand);

  // Die Werkzeilen folgen dem Server-Stand: beim Aufklappen und bei jedem
  // echten Versionswechsel (Speichern, 409-Nachladen, Veröffentlichen,
  // Erscheinen des Programms). Ein Geschwister-Nachladen mit unveränderter
  // rowVersion lässt die Zeilen unberührt, damit ungespeicherte Arbeit
  // nicht verloren geht; der ältere Stand des Elternteils überschreibt
  // keine schon übernommene frischere Version (eigenes Speichern ist
  // der Auftrittsabfrage voraus). Ersetzt ein frischerer Stand unerledigte
  // Arbeit, meldet sich die Werkbank: die Änderung kam von außen.
  useEffect(() => {
    if (!ausgeklappt) return;
    const version = programm?.rowVersion ?? null;
    if (version === letzteGeladeneVersion) return;
    if (
      letzteGeladeneVersion !== null &&
      (version === null || version < letzteGeladeneVersion)
    ) {
      return;
    }
    // ARC-027: liegt ungespeicherte Arbeit vor, stammt die Ablösung von
    // außen — eigenes Speichern übernimmt die frische Einbettung direkt,
    // ohne diesen Weg. Der Hinweis erklärt die Ersetzung, bevor die
    // Zeilen dem frischen Stand folgen.
    if (ungespeichert(zeilen, synchronstand)) {
      setHinweis(veralteteAenderung);
    }
    const folge = zeilenAusRevisionen(entwurf, veroeffentlicht);
    setZeilen(folge.zeilen);
    setSynchronstand(synchronstandAusZeilen(folge.zeilen));
    setEntwurfKopie(folge.kopie);
    setLetzteGeladeneVersion(version);
    // Zeilen und Synchronstand in den Abhängigkeiten: der Abgleich liest
    // den aktuellen Arbeitsstand, der Versionswechsel entscheidet weiter.
    // Passt die Version, kehrt der Effekt früh zurück — Tastenschläge
    // kosten nichts als den Vergleich.
  }, [
    ausgeklappt,
    programm,
    entwurf,
    veroeffentlicht,
    letzteGeladeneVersion,
    zeilen,
    synchronstand,
  ]);

  // Lieddetails je betroffenem Eintrag laden (FassungsWahl braucht sie);
  // schon geladene Lieder bleiben im Bestand, nur neue Ids werden geholt.
  // Eine fehlgeschlagene Abfrage merkt sich ihr Lied als "fehler" und
  // wartet auf Erneut versuchen, statt sich endlos zu wiederholen.
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
          return [id, "fehler"] as const;
        }
      }),
    ).then((paare) => {
      if (abbruch.signal.aborted) return;
      setLieder((bisher) => ({
        ...bisher,
        ...Object.fromEntries(paare),
      }));
      setLiedFehler((bisher) => {
        const stand = { ...bisher };
        for (const [id, ergebnis] of paare) {
          if (ergebnis === "fehler") stand[id] = liedLadeFehler;
          else delete stand[id];
        }
        return stand;
      });
    });
    return () => abbruch.abort();
  }, [ausgeklappt, zeilen, lieder]);

  function neuerEintrag(songId: string) {
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

  // Neuer Versuch für ein fehlgeschlagenes Lied: der Fehler-Marker und
  // die Meldung fallen weg, die Wirkung lädt das Lied erneut.
  function liedErneutVersuchen(songId: string) {
    setLieder((bisher) => {
      const stand = { ...bisher };
      delete stand[songId];
      return stand;
    });
    setLiedFehler((bisher) => {
      const stand = { ...bisher };
      delete stand[songId];
      return stand;
    });
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
      // entfällt); mit Programm zählt die zuletzt geladene Version als
      // Concurrency-Anker — auch dann, wenn die Auftrittsabfrage nach
      // einem eigenen Speichern noch unterwegs ist.
      const einbettung = await putProgrammItems(auftritt.id, {
        items: zeilen.map((zeile) => ({
          ...(zeile.serverId ? { id: zeile.serverId } : {}),
          songId: zeile.songId ?? "",
          musicalVersionId: zeile.musicalVersionId ?? "",
          note: zeile.notiz.trim(),
        })),
        ...(letzteGeladeneVersion !== null
          ? { rowVersion: letzteGeladeneVersion }
          : {}),
      });
      // Die frische Einbettung (neue Ids, neue rowVersion) kommt aus der
      // Antwort; die Zeilen folgen ihr sofort, damit ein zügiges zweites
      // Speichern den neuen Anker trägt. Das Nachladen des Elternteils
      // bringt dieselbe Version und löst keinen zweiten Resync aus.
      const folge = zeilenAusRevisionen(
        einbettung.working,
        einbettung.published,
      );
      setLetzteGeladeneVersion(einbettung.rowVersion);
      setZeilen(folge.zeilen);
      setSynchronstand(synchronstandAusZeilen(folge.zeilen));
      setEntwurfKopie(folge.kopie);
      setErfolg("Programmentwurf gespeichert.");
      setHinweis("");
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
    if (letzteGeladeneVersion === null) {
      setHinweis("Es liegt noch kein Programmentwurf vor.");
      return;
    }
    // Nichts wird stillschweigend verworfen: ungespeicherte Arbeit
    // bremst die Veröffentlichung, bis der Entwurf gespeichert ist.
    if (ungespeichert(zeilen, synchronstand)) {
      setHinweis(ungespeicherteAenderungen);
      return;
    }
    setVeröffentlichenBusy(true);
    setHinweis("");
    try {
      const einbettung = await publishProgramm(
        auftritt.id,
        letzteGeladeneVersion,
      );
      // Nach der Veröffentlichung ist der Entwurf weg: die Zeilen starten
      // als Kopie der veröffentlichten Liste (ohne Server-Ids), der
      // nächste PUT legt jeden Eintrag neu an.
      const folge = zeilenAusRevisionen(
        einbettung.working,
        einbettung.published,
      );
      setLetzteGeladeneVersion(einbettung.rowVersion);
      setZeilen(folge.zeilen);
      setSynchronstand(synchronstandAusZeilen(folge.zeilen));
      setEntwurfKopie(folge.kopie);
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
      ) : isEditor && programm !== null ? (
        // Ehrlich zur Redaktion: ein Entwurf existiert, es ist nur noch
        // nichts veröffentlicht; Mitglieder lesen dieselbe Situation als
        // „noch nicht erfasst", weil sie den Entwurf nicht sehen.
        <p className="auftritt-leer">Noch nichts veröffentlicht.</p>
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
                {/* ARC-027 Standzeilen: ehrlich in jedem Zustand — was der
                    Entwurf ist und was die Mitglieder lesen. */}
                <p className="programm-stand">
                  {entwurf
                    ? `Entwurf: Revision ${entwurf.number} · Letzte Änderung: ${publishedAtText(entwurf.updatedAt)}`
                    : "Entwurf: noch keiner gespeichert."}
                </p>
                <p className="programm-stand">
                  {veroeffentlicht
                    ? `Mitgliedersicht: Revision ${veroeffentlicht.number} · veröffentlicht am ${publishedAtText(veroeffentlicht.publishedAt)}`
                    : "Mitgliedersicht: noch nichts veröffentlicht."}
                </p>
                {/* Frühere Fassungen: die eingefrorenen Veröffentlichungen
                    vor der neuesten (Verlaufsrevisionen der Werkbank). */}
                {programm?.history && programm.history.length > 0 && (
                  <div className="programm-fruehere">
                    <p className="programm-fruehere-einleitung">
                      Frühere Fassungen
                    </p>
                    {programm.history.map((fassung) => (
                      <p className="programm-fruehere-zeile" key={fassung.id}>
                        Revision {fassung.number} · veröffentlicht am{" "}
                        {publishedAtText(fassung.publishedAt)}
                      </p>
                    ))}
                  </div>
                )}
                {entwurfKopie && (
                  <p className="noten-info">
                    Der neue Entwurf beginnt als Kopie der veröffentlichten
                    Liste; die veröffentlichte Fassung bleibt unverändert
                    stehen.
                  </p>
                )}
                {zeilen.length === 0 ? (
                  <p className="auftritt-leer">
                    Der Entwurf ist leer. Such ein Lied aus dem Katalog und füge
                    es mit seiner Fassung hinzu.
                  </p>
                ) : (
                  <ul className="programm-zeilen">
                    {zeilen.map((zeile, index) => {
                      const liedStand = zeile.songId
                        ? lieder[zeile.songId]
                        : undefined;
                      return (
                        <ProgrammZeile
                          key={zeile.schluessel}
                          zeile={zeile}
                          stelle={index + 1}
                          erste={index === 0}
                          letzte={index === zeilen.length - 1}
                          lied={
                            liedStand && liedStand !== "fehler"
                              ? liedStand
                              : null
                          }
                          liedFehler={
                            (zeile.songId && liedFehler[zeile.songId]) || ""
                          }
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
                          onErneutVersuchen={() => {
                            if (zeile.songId) liedErneutVersuchen(zeile.songId);
                          }}
                        />
                      );
                    })}
                  </ul>
                )}
                <LiedWahl onGewaehlt={neuerEintrag} />
                {ungespeicherteArbeit && (
                  <p className="programm-unerledigt">
                    Nicht gespeicherte Änderungen.
                  </p>
                )}
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
