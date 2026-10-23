"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { DateienAblegen } from "@/components/lied-ablegen";
import { ladeEntwuerfe, ladeEntwurf } from "@/lib/lied-entwuerfe";
import type { Gespeichert } from "@/lib/lied-entwurf/gespeichert";
import {
  kannSpeichern,
  liedFelder,
  liestNoch,
  type Zustand,
} from "@/lib/lied-entwurf/reducer";
import {
  gruppiereImport,
  type SammelGruppe,
  type SammelPlan,
} from "@/lib/sammel-import";

// Der Sammel-Import (#143): ein Ordner - zum Beispiel ein Drive-Export -
// wird zu Vorschlägen, ein Unterordner je Lied (Regeln der Inventur, #98).
// Die Redaktion bearbeitet und speichert jeden Vorschlag genau so wie bei
// der Ablage eines einzelnen Liedes (#159): dieselben Entwürfe im Backend,
// dieselben Lesungen, dieselben Treffer als "hinzufügen". Ohne Drive:
// die Archivevidence sind eigene Kopien, die Bytes bleiben im Browser,
// bis ein Vorschlag gespeichert ist.
//
// Der Wiederaufnahme nach bleibt nichts doppelt: die Entwurf-Id eines
// Vorschlags ist vom Ordnerinhalt bestimmt - ein noch einmal gefallener
// Ordner ist derselbe Stand, kein zweiter Entwurf, kein zweites Lied.

type Meldung = {
  entwurfId: string;
  zustand: Exclude<Zustand, { phase: "leer" | "verworfen" }>;
  speichern: () => Promise<void>;
};

type Klarheit =
  | { art: "arbeitet"; klar: boolean; liest: boolean }
  | { art: "fertig"; liedId: string };

/** Die Klarheit eines Meldungszustands (dasselbe Maß wie die Meldung der Einträge). */
function klarheitVon(zustand: Meldung["zustand"]): Klarheit {
  if (zustand.phase === "gespeichert" && zustand.liedId)
    return { art: "fertig", liedId: zustand.liedId };
  const klar =
    zustand.phase === "sammeln" &&
    zustand.fehler === null &&
    kannSpeichern(zustand) &&
    !liestNoch(zustand) &&
    // Ein angebotener Treffer ist eine Redaktionsentscheidung.
    zustand.ziel.status !== "angeboten" &&
    // Unsichere Vorschläge bleiben der Handarbeit.
    !Object.values(zustand.vorschlaege).some((s) => s === "unsicher") &&
    // Wo das Lesen scheiterte, entscheidet die Redaktion.
    !zustand.dateien.some(
      (d) =>
        d.lesen.status === "fehlgeschlagen" ||
        d.lesen.status === "nicht-verfuegbar",
    );
  return { art: "arbeitet", klar, liest: liestNoch(zustand) };
}

/** Der gegen das Backend gestellte Anfang für einen neuen Vorschlag. */
function vorschlagEntwurf(gruppe: SammelGruppe): Gespeichert {
  return {
    entwurfId: gruppe.entwurfId,
    lied: Object.fromEntries(
      liedFelder.map((feld) => [feld, feld === "title" ? gruppe.titel : ""]),
    ) as Record<(typeof liedFelder)[number], string>,
    bearbeitet: [],
    vorschlaege: {},
    dateien: [],
  };
}

export function SammelImport() {
  const [plan, setPlan] = useState<SammelPlan | null>(null);
  const [dateienNachGruppe, setDateienNachGruppe] = useState<
    Record<string, File[]>
  >({});
  // Entwurf-Ids, die das Backend schon hält: sie setzen fort statt neu zu
  // beginnen.
  const vorhandenRef = useRef(new Set<string>());
  const [offen, setOffen] = useState<string | null>(null);
  const [geladen, setGeladen] = useState<Record<string, Gespeichert>>({});
  const [klarheiten, setKlarheiten] = useState<Record<string, Klarheit>>({});
  const [fehler, setFehler] = useState("");
  const [lädt, setLädt] = useState(false);
  const meldungenRef = useRef(new Map<string, Meldung>());
  const sperrRef = useRef(false);

  const melde = useCallback((meldung: Meldung) => {
    meldungenRef.current.set(meldung.entwurfId, meldung);
    setKlarheiten((bestand) => ({
      ...bestand,
      [meldung.entwurfId]: klarheitVon(meldung.zustand),
    }));
  }, []);

  const ordnerGewaehlt = useCallback(async (gewaehlt: File[]) => {
    if (gewaehlt.length === 0) return;
    setFehler("");
    setLädt(true);
    try {
      const einplan = await gruppiereImport(
        gewaehlt.map((f) => ({
          name: f.name,
          pfad:
            "webkitRelativePath" in f && f.webkitRelativePath
              ? f.webkitRelativePath
              : f.name,
          sizeBytes: f.size,
        })),
      );
      // Die vorhandenen Entwürfe fragt der Plan bevor er angelegt: dieselbe
      // Auswahl, die schon angefangen wurde, soll fortgesetzt werden.
      try {
        const eintraege = await ladeEntwuerfe();
        if (eintraege) {
          vorhandenRef.current = new Set(
            eintraege.map((eintrag) => eintrag.entwurfId),
          );
        }
      } catch {
        // Ohne die Liste gilt jeder Vorschlag als neu.
      }
      setPlan(einplan);
      // Die Bytes je Vorschlag: zuordnen nach Name und Größe (im einen
      // Ordner eindeutig; die Doppel der Inventur tragen andere Größen).
      const bytesNachName = new Map<string, File>();
      for (const original of gewaehlt)
        bytesNachName.set(`${original.name}\u0000${original.size}`, original);
      const bytesNachId: Record<string, File[]> = {};
      for (const gruppe of einplan.gruppen) {
        bytesNachId[gruppe.entwurfId] = gruppe.dateien.flatMap((datei) => {
          const original = bytesNachName.get(
            `${datei.name}\u0000${datei.sizeBytes}`,
          );
          return original ? [original] : [];
        });
      }
      setDateienNachGruppe(bytesNachId);
      setKlarheiten({});
      meldungenRef.current = new Map();
      setOffen(null);
    } catch {
      setFehler(
        "Der Ordner konnte nicht gelesen werden. Bitte erneut versuchen.",
      );
    } finally {
      setLädt(false);
    }
  }, []);

  // Fortsetzen: der Stand im Backend anfordern; ein neuer Vorschlag beginnt
  // mit seinem Namen aus dem Ordner.
  const gruppeOffen = plan?.gruppen.find((g) => g.entwurfId === offen) ?? null;
  useEffect(() => {
    if (gruppeOffen === null || geladen[gruppeOffen.entwurfId]) return;
    const fortsetzen = vorhandenRef.current.has(gruppeOffen.entwurfId);
    if (!fortsetzen) {
      setGeladen((bestand) => ({
        ...bestand,
        [gruppeOffen.entwurfId]: vorschlagEntwurf(gruppeOffen),
      }));
      return;
    }
    const steuerung = new AbortController();
    setLädt(true);
    void ladeEntwurf(gruppeOffen.entwurfId, steuerung.signal)
      .then((stand) => {
        if (!steuerung.signal.aborted)
          setGeladen((bestand) => ({
            ...bestand,
            [gruppeOffen.entwurfId]: stand,
          }));
      })
      .catch(() => {
        // Ohne Stand im Backend beginnt der Vorschlag von vorn.
        if (!steuerung.signal.aborted)
          setGeladen((bestand) => ({
            ...bestand,
            [gruppeOffen.entwurfId]: vorschlagEntwurf(gruppeOffen),
          }));
      })
      .finally(() => {
        if (!steuerung.signal.aborted) setLädt(false);
      });
    return () => steuerung.abort();
  }, [gruppeOffen, geladen]);

  /**
   * Alle klaren Vorschläge nacheinander speichern. Ein zugeklappter Eintrag
   * wird dazu aufgeklappt und wartet kurz auf seine Meldung; was kein klarer
   * Vorschlag ist (unsichere Vorschläge, laufende Lesungen, Bytes fehlen),
   * bleibt der Handarbeit.
   */
  const alleKlareSpeichern = useCallback(async () => {
    if (sperrRef.current || !plan) return;
    sperrRef.current = true;
    setLädt(true);
    try {
      for (const gruppe of plan.gruppen) {
        const ausgang = klarheiten[gruppe.entwurfId];
        if (ausgang?.art === "fertig") continue;
        if ((dateienNachGruppe[gruppe.entwurfId] ?? []).length === 0) continue;
        setOffen(gruppe.entwurfId);
        // Der Eintrag braucht einen Moment: erst steht der Entwurf im
        // Backend, dann wandern die Bytes nach; klarheitVon entscheidet.
        let klarheit: Klarheit | null = null;
        for (let schritt = 0; schritt < 240; schritt++) {
          await new Promise((fertig) => setTimeout(fertig, 250));
          const meldung = meldungenRef.current.get(gruppe.entwurfId);
          const zustand = meldung?.zustand;
          // Ohne Bytes im Zustand ist der Stand noch unterwegs: Warten.
          klarheit =
            zustand &&
            !(zustand.phase === "sammeln" && zustand.dateien.length === 0)
              ? klarheitVon(zustand)
              : null;
          if (
            klarheit &&
            (klarheit.art === "fertig" ||
              (klarheit.art === "arbeitet" && !klarheit.liest))
          )
            // Endentscheidung: klar bleibt klar, Unklares bleibt der Handarbeit.
            break;
        }
        if (klarheit?.art !== "arbeitet" || !klarheit.klar) continue;
        const meldung = meldungenRef.current.get(gruppe.entwurfId);
        if (!meldung) continue;
        await meldung.speichern();
      }
    } catch {
      setFehler(
        "Ein Vorschlag ließ sich nicht speichern. Bitte den Rest einzeln prüfen.",
      );
    } finally {
      sperrRef.current = false;
      setLädt(false);
    }
  }, [plan, klarheiten, dateienNachGruppe]);

  // Klare und noch nicht angefasste Vorschläge: die noch nicht geöffneten
  // gelten als klar, bis ihre Meldung sie zurückstuft.
  const klar =
    plan?.gruppen.filter((g) => {
      const k = klarheiten[g.entwurfId];
      if (k?.art !== "arbeitet") return false;
      return k.klar;
    }).length ?? 0;
  const anwärtige =
    plan?.gruppen.filter((g) => {
      const k = klarheiten[g.entwurfId];
      if (k) return false;
      return (dateienNachGruppe[g.entwurfId] ?? []).length > 0;
    }).length ?? 0;
  const fertig =
    plan?.gruppen.filter((g) => klarheiten[g.entwurfId]?.art === "fertig")
      .length ?? 0;

  return (
    <div className="sammel-import">
      <label className="lied-ablegen-zone" htmlFor="sammel-import-ordner">
        <span>Ordner auswählen</span>
        <span className="feld-hinweis">
          Ein Ordner mit Unterordnern, zum Beispiel ein Drive-Export: Jeder
          Unterordner wird zum Vorschlag eines Lieds. Die Bytes liegen im
          Browser, bis ein Vorschlag gespeichert wird; unterbrochene Ansätze
          laufen fort, ohne etwas zu verdoppeln.
        </span>
        <OrdnerEingang onGewaehlt={(dateien) => void ordnerGewaehlt(dateien)} />
      </label>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {lädt && (
        <p aria-live="polite" className="feld-hinweis">
          Der Ordner wird geordnet …
        </p>
      )}
      {plan && plan.gruppen.length > 0 && (
        <>
          <p className="feld-hinweis">
            {plan.gruppen.length === 1
              ? "1 Vorschlag"
              : `${plan.gruppen.length} Vorschläge`}
            {fertig > 0 && `, ${fertig} gespeichert`}
          </p>
          {plan.separatPruefen.length > 0 && (
            <p className="feld-hinweis">
              Separat prüfen, nicht automatisch importiert:{" "}
              {plan.separatPruefen
                .map((p) => `${p.name}${p.ordner ? ` (${p.ordner})` : ""}`)
                .join(", ")}
            </p>
          )}
          {plan.verknuepfungen.length > 0 && (
            <p className="feld-fehler">
              Verknüpfungen ohne Ziel in dem gewählten Ordner:{" "}
              {plan.verknuepfungen.map((v) => v.name).join(", ")}
            </p>
          )}
          <ul aria-label="Vorschläge" className="sammel-vorschläge">
            {plan.gruppen.map((gruppe) => (
              <VorschlagEintrag
                key={gruppe.entwurfId}
                gruppe={gruppe}
                offen={offen === gruppe.entwurfId}
                geladen={geladen[gruppe.entwurfId]}
                klarheit={klarheiten[gruppe.entwurfId]}
                dateien={dateienNachGruppe[gruppe.entwurfId] ?? []}
                fortsetzen={vorhandenRef.current.has(gruppe.entwurfId)}
                aufklappen={() => setOffen(gruppe.entwurfId)}
                zuklappen={() => setOffen(null)}
                meldung={melde}
              />
            ))}
          </ul>
          <div className="auth-aktionen">
            <button
              type="button"
              disabled={klar + anwärtige === 0 || lädt}
              onClick={() => void alleKlareSpeichern()}
            >
              {klar + anwärtige === 1
                ? "1 klaren Vorschlag speichern"
                : `${klar + anwärtige} klare Vorschläge speichern`}
            </button>
          </div>
        </>
      )}
    </div>
  );
}

/** Der Ordner-Eingang des Browsers: eine Verzeichnisauswahl. */
function OrdnerEingang({
  onGewaehlt,
}: {
  onGewaehlt: (dateien: File[]) => void;
}) {
  const eingangRef = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    // webkitdirectory kennt React nicht; nur mit ihr wählt der Browser einen
    // Ordner statt einzelner Dateien.
    eingangRef.current?.setAttribute("webkitdirectory", "");
  }, []);
  return (
    <input
      ref={eingangRef}
      id="sammel-import-ordner"
      type="file"
      multiple
      aria-label="Ordner wählen"
      onChange={(ereignis) => {
        onGewaehlt(Array.from(ereignis.target.files ?? []));
        ereignis.target.value = "";
      }}
    />
  );
}

/** Ein Vorschlag: der Ordner, seine Dateien und sein Entwurf. */
function VorschlagEintrag({
  gruppe,
  offen,
  geladen,
  klarheit,
  dateien,
  fortsetzen,
  aufklappen,
  zuklappen,
  meldung,
}: {
  gruppe: SammelGruppe;
  offen: boolean;
  geladen?: Gespeichert;
  klarheit?: Klarheit;
  dateien: File[];
  fortsetzen: boolean;
  aufklappen: () => void;
  zuklappen: () => void;
  meldung: (meldung: Meldung) => void;
}) {
  const unbekannte = gruppe.dateien.filter((d) => d.stimmeUnbekannt).length;
  return (
    <li
      className="sammel-vorschlag"
      data-vorschlag={
        klarheit?.art === "fertig" ? "fertig" : klarheit?.klar ? "klar" : "neu"
      }
    >
      <p className="sammel-vorschlag-kopf">
        <strong>{gruppe.ordner || gruppe.titel}</strong>{" "}
        <span className="feld-hinweis">
          {gruppe.dateien.length === 1
            ? "1 Datei"
            : `${gruppe.dateien.length} Dateien`}
          {unbekannte > 0 && `, ${unbekannte} Stimme(n) unbekannt`}
        </span>
      </p>
      {klarheit?.art === "fertig" ? (
        <p className="feld-hinweis">
          Gespeichert.{" "}
          <a href={`/lied/?id=${encodeURIComponent(klarheit.liedId)}`}>
            Zum Lied
          </a>
        </p>
      ) : !offen ? (
        <button type="button" className="knopf-leise" onClick={aufklappen}>
          {fortsetzen ? "Fortsetzen" : "Bearbeiten"}
        </button>
      ) : geladen ? (
        <DateienAblegen
          key={gruppe.entwurfId}
          gespeichert={geladen}
          wiederablage={dateien.length > 0 ? dateien : undefined}
          ohneWeiterleitung
          onFertig={zuklappen}
          onVerworfen={zuklappen}
          sammelAnmeldung={meldung}
        />
      ) : (
        <p aria-live="polite" className="feld-hinweis">
          Der Entwurf wird geladen …
        </p>
      )}
    </li>
  );
}
