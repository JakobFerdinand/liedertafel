"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { SpielerFehler } from "@/lib/assets";
import { ladeFehlerAusUrsache, restlaufzeitMs, zeitText } from "@/lib/assets";
import { bereichText } from "@/lib/zeitmarken";

// Gemeinsamer Spieler für Stimmaufnahmen (ARC-018) und ganze
// Konzertaufnahmen in Ton oder Bild (ARC-030): beschriftete Bedienelemente,
// Suchen, Lautstärke und die stille Ticket-Erneuerung, die Position und
// Wiedergabezustand über den Wechsel der Adresse hinweg erhält. Woher ein
// frisches Ticket kommt, entscheidet der Aufrufer (`holeZugriff`).

/** Vorlauf vor dem Ticketablauf, zu dem still neue Tickets angefordert werden. */
const ErneuerungsVorlaufMs = 60_000;

/** Nachlauf für einen stillen Erneuerungsversuch nach einem Fehlschlag. */
const ErneuerungsWiederholungMs = 30_000;

/** Was der Spieler von einem Ticket braucht: Adresse und Ablauf. */
export type SpielerZugriff = {
  viewUrl: string;
  expiresAt: string;
};

/** Eine angeforderte Position; `marke` unterscheidet wiederholte Sprünge. */
export type SpielerSprung = {
  sekunden: number;
  marke: number;
};

/**
 * Ein markierter Abschnitt (ARC-032): der Spieler hält am Ende an, wenn die
 * Wiedergabe in ihm läuft, und sagt es. `marke` unterscheidet wiederholte
 * Sprünge in denselben Abschnitt.
 */
export type SpielerAbschnitt = {
  von: number;
  bis: number;
  titel: string;
  marke: number;
};

/**
 * aktiv: Halt am Ende · ende: Halt erreicht · frei: die ganze Aufnahme läuft
 * weiter · ausserhalb: der Anfang liegt hinter dem Ende der Datei.
 */
type AbschnittZustand = "aktiv" | "ende" | "frei" | "ausserhalb";

export type MedienSpielerProps<Zugriff extends SpielerZugriff> = {
  /** Eindeutig je Seite; bildet die Kennungen der Regler. */
  kennung: string;
  art: "audio" | "video";
  /** Klartext-Name der Aufnahme oder Stimme für alle Beschriftungen. */
  name: string;
  zugriff: Zugriff;
  /** Holt ein frisches Ticket; die API prüft dabei die Berechtigung neu. */
  holeZugriff: () => Promise<Zugriff>;
  /** Nur ein Spieler läuft gleichzeitig; inaktive pausieren sich selbst. */
  aktiv: boolean;
  onAbspielen: () => void;
  onErneuert: (zugriff: Zugriff) => void;
  /** Meldung für vorübergehende Ladefehler. */
  ladeMeldung: string;
  /** Meldung, wenn der Browser die Datei nicht wiedergeben kann. */
  formatMeldung: string;
  /**
   * Springt an eine Position, sobald die Datei sie kennt — beim Öffnen
   * (Verweis mit Zeitangabe) und bei jeder neuen Marke (ARC-032).
   */
  sprung?: SpielerSprung;
  /**
   * Darf die Datei heruntergeladen werden? Wenn nicht, bietet auch das
   * Medienelement selbst keinen Speichern-Weg an (Kontextmenü, „Download“
   * der Browser-Bedienung im Vollbild). Das ist kein Kopierschutz: die
   * Adresse bleibt ein zeitlich begrenztes Lese-Ticket.
   */
  herunterladenErlaubt: boolean;
  /** Meldet die aktuelle Position, damit der Aufrufer sie sich merken kann. */
  onPosition?: (sekunden: number) => void;
  /**
   * Der Abschnitt, zu dem zuletzt gesprungen wurde (ARC-032): wird
   * angezeigt, und die Wiedergabe hält an seinem Ende an.
   */
  abschnitt?: SpielerAbschnitt;
};

export function MedienSpieler<Zugriff extends SpielerZugriff>({
  kennung,
  art,
  name,
  zugriff,
  holeZugriff,
  aktiv,
  onAbspielen,
  onErneuert,
  ladeMeldung,
  formatMeldung,
  sprung,
  herunterladenErlaubt,
  onPosition,
  abschnitt,
}: MedienSpielerProps<Zugriff>) {
  const LadeFehler: SpielerFehler = { art: "laden", meldung: ladeMeldung };
  const stimme = name;
  const audioRef = useRef<HTMLMediaElement | null>(null);
  const timerRef = useRef<number | undefined>(undefined);
  // Fortsetzen merkt Position und Wiedergabezustand über den Wechsel auf
  // erneuerte Ticket-URLs hin; das Element selbst bleibt erhalten.
  const fortsetzenRef = useRef<{
    position: number;
    wiedergabe: boolean;
  } | null>(null);
  // Verhindert eine Endlosschleife Fehler → stiller Neustart → Fehler.
  const stillerVersuchRef = useRef(false);
  const wiedergabeRef = useRef(false);
  const onErneuertRef = useRef(onErneuert);
  onErneuertRef.current = onErneuert;
  const holeZugriffRef = useRef(holeZugriff);
  holeZugriffRef.current = holeZugriff;
  const ladeMeldungRef = useRef(ladeMeldung);
  ladeMeldungRef.current = ladeMeldung;
  // Ein Sprung, den die Datei noch nicht ausführen konnte.
  const offenerSprungRef = useRef<number | null>(null);
  const onPositionRef = useRef(onPosition);
  onPositionRef.current = onPosition;
  // Abschnittsgrenze: nur wer innerhalb des Abschnitts spielt und sein Ende
  // erreicht, wird angehalten; wer darüber hinaus sucht, wird nicht gebremst.
  const abschnittAktivRef = useRef(false);
  const letzteZeitRef = useRef<number | null>(null);
  const [abschnittZustand, setAbschnittZustand] =
    useState<AbschnittZustand>("aktiv");
  const [vollbild, setVollbild] = useState(false);
  const setzeMedium = useCallback((element: HTMLMediaElement | null) => {
    audioRef.current = element;
  }, []);

  const [wiedergabe, setWiedergabe] = useState(false);
  const [position, setPosition] = useState(0);
  const [dauer, setDauer] = useState<number | null>(null);
  const [lautstaerke, setLautstaerke] = useState(1);
  const [fehler, setFehler] = useState<SpielerFehler | null>(null);

  // Die Ticket-Adresse setzt der Spieler selbst, nach dem Einhängen (die
  // Behandler hängen dann schon) und bei jedem neuen Ticket. Position und
  // Wiedergabezustand werden im Augenblick des Wechsels gelesen – nicht
  // beim Anfordern des Tickets –, damit ein Suchen während der Erneuerung
  // gilt. Ein noch nicht ausgeführter Sprung oder ein noch nicht
  // abgeschlossener früherer Wechsel bleibt dabei das Ziel.
  const adresse = zugriff.viewUrl;
  useEffect(() => {
    const medium = audioRef.current;
    if (!medium || medium.getAttribute("src") === adresse) return;
    if (medium.getAttribute("src")) {
      const offen = fortsetzenRef.current;
      fortsetzenRef.current = {
        position:
          offen?.position ?? offenerSprungRef.current ?? medium.currentTime,
        wiedergabe: offen?.wiedergabe ?? wiedergabeRef.current,
      };
      offenerSprungRef.current = null;
    }
    medium.src = adresse;
  }, [adresse]);

  const erneuern = useCallback(async (hintergrund: boolean) => {
    try {
      const neu = await holeZugriffRef.current();
      onErneuertRef.current(neu);
    } catch (ursache) {
      if (hintergrund) {
        // Stiller Wiederholungsversuch: das alte Ticket kann weiterlaufen.
        window.clearTimeout(timerRef.current);
        timerRef.current = window.setTimeout(() => {
          void erneuern(true);
        }, ErneuerungsWiederholungMs);
        return;
      }
      setFehler(ladeFehlerAusUrsache(ursache, ladeMeldungRef.current));
    }
  }, []);

  // Stellt eine Position ein. Ein Ziel hinter dem Ende der Datei (alter
  // Verweis, kürzere Abspielfassung) beginnt vorn statt am Schluss.
  const geheZu = useCallback((medium: HTMLMediaElement, ziel: number) => {
    const ende = medium.duration;
    const start = Number.isFinite(ende) && ziel >= ende ? 0 : ziel;
    try {
      medium.currentTime = start;
    } catch {
      // Nicht suchbar: Wiedergabe startet vorn.
    }
    setPosition(start);
    onPositionRef.current?.(start);
  }, []);

  // Angeforderte Position: sofort, wenn die Datei ihre Länge kennt, sonst
  // mit den Metadaten.
  const sprungSekunden = sprung?.sekunden;
  const sprungMarke = sprung?.marke;
  useEffect(() => {
    void sprungMarke;
    if (sprungSekunden === undefined || !Number.isFinite(sprungSekunden)) {
      return;
    }
    const ziel = Math.max(0, sprungSekunden);
    const medium = audioRef.current;
    if (medium && medium.readyState >= 1 && fortsetzenRef.current === null) {
      geheZu(medium, ziel);
    } else if (fortsetzenRef.current) {
      fortsetzenRef.current.position = ziel;
    } else {
      offenerSprungRef.current = ziel;
    }
  }, [sprungSekunden, sprungMarke, geheZu]);

  // Ein neuer Abschnitt (oder ein erneuter Sprung) scharf stellen.
  const abschnittVon = abschnitt?.von;
  const abschnittBis = abschnitt?.bis;
  const abschnittMarke = abschnitt?.marke;
  useEffect(() => {
    void abschnittMarke;
    abschnittAktivRef.current =
      abschnittVon !== undefined && abschnittBis !== undefined;
    letzteZeitRef.current = null;
    setAbschnittZustand("aktiv");
  }, [abschnittVon, abschnittBis, abschnittMarke]);

  // Eine Marke hinter dem Ende der wirklichen Datei (Dauer unbekannt, als
  // die Marke gesetzt wurde, oder kürzere Datei): nichts wird vorgetäuscht,
  // die Wiedergabe beginnt vorn und die Meldung sagt es.
  useEffect(() => {
    void abschnittMarke;
    if (dauer !== null && abschnittVon !== undefined && abschnittVon >= dauer) {
      abschnittAktivRef.current = false;
      setAbschnittZustand("ausserhalb");
    }
  }, [dauer, abschnittVon, abschnittMarke]);

  // Im Vollbild trägt der Browser die Bedienung; die eigenen Regler liegen
  // dann außerhalb des sichtbaren Bereichs.
  useEffect(() => {
    if (art !== "video") return;
    function beiWechsel() {
      setVollbild(document.fullscreenElement === audioRef.current);
    }
    document.addEventListener("fullscreenchange", beiWechsel);
    return () => document.removeEventListener("fullscreenchange", beiWechsel);
  }, [art]);

  // Erneuert das Ticket kurz vor Ablauf; der Schlüssel auf `zugriff` plant
  // nach jeder erfolgreich erneuerten Antwort automatisch nach.
  useEffect(() => {
    window.clearTimeout(timerRef.current);
    const rest = restlaufzeitMs(zugriff.expiresAt) - ErneuerungsVorlaufMs;
    const wartezeit = rest > 5000 ? rest : 5000;
    timerRef.current = window.setTimeout(() => {
      void erneuern(true);
    }, wartezeit);
    return () => window.clearTimeout(timerRef.current);
  }, [zugriff, erneuern]);

  // Nur der aktive Spieler läuft; ein anderer startet, pausiert dieser.
  useEffect(() => {
    const audio = audioRef.current;
    if (!aktiv && audio && !audio.paused) audio.pause();
  }, [aktiv]);

  function beiWiedergabeStart() {
    // Wer nach dem Abschnittsende selbst weiterspielt, spielt die ganze
    // Aufnahme: die Meldung zum Ende und ihre Knöpfe gelten nicht mehr.
    if (abschnittZustand === "ende") {
      abschnittAktivRef.current = false;
      setAbschnittZustand("frei");
    }
    wiedergabeRef.current = true;
    setWiedergabe(true);
    onAbspielen();
  }

  function beiWiedergabeStopp() {
    wiedergabeRef.current = false;
    setWiedergabe(false);
  }

  function beiZeit() {
    const audio = audioRef.current;
    // Während ein Ticketwechsel lädt, steht das Element kurz auf null; die
    // gemerkte Position bleibt die Wahrheit.
    if (!audio || fortsetzenRef.current) return;
    setPosition(audio.currentTime);
    onPositionRef.current?.(audio.currentTime);
    if (abschnitt && abschnittAktivRef.current) {
      const jetzt = audio.currentTime;
      const davor = letzteZeitRef.current;
      letzteZeitRef.current = jetzt;
      if (jetzt >= abschnitt.bis) {
        abschnittAktivRef.current = false;
        // Nur ein Ende, das beim Abspielen erreicht wurde, hält an; ein
        // Sprung hinter das Ende gibt die Grenze frei.
        if (davor !== null && davor < abschnitt.bis && jetzt - davor <= 2) {
          audio.pause();
          audio.currentTime = abschnitt.bis;
          setPosition(abschnitt.bis);
          setAbschnittZustand("ende");
        } else {
          setAbschnittZustand("frei");
        }
      }
    }
  }

  // Wer aus dem Abschnitt heraus sucht (davor oder dahinter), will die ganze
  // Aufnahme hören: der Halt am Abschnittsende wird freigegeben.
  function beiGesprungen() {
    const audio = audioRef.current;
    if (!audio || !abschnitt || fortsetzenRef.current) return;
    const stelle = audio.currentTime;
    if (stelle < abschnitt.von - 0.5 || stelle > abschnitt.bis + 0.5) {
      abschnittAktivRef.current = false;
      letzteZeitRef.current = null;
      setAbschnittZustand((vorher) =>
        vorher === "ausserhalb" ? vorher : "frei",
      );
    }
  }

  function abschnittWiederholen() {
    const audio = audioRef.current;
    if (!audio || !abschnitt) return;
    abschnittAktivRef.current = true;
    letzteZeitRef.current = null;
    setAbschnittZustand("aktiv");
    geheZu(audio, abschnitt.von);
    void audio.play().catch(() => {});
  }

  function ganzWeiterspielen() {
    const audio = audioRef.current;
    abschnittAktivRef.current = false;
    setAbschnittZustand("frei");
    void audio?.play().catch(() => {});
  }

  function beiMetadaten() {
    const audio = audioRef.current;
    if (!audio) return;
    stillerVersuchRef.current = false;
    setDauer(Number.isFinite(audio.duration) ? audio.duration : null);
    const fortsetzen = fortsetzenRef.current;
    fortsetzenRef.current = null;
    const offenerSprung = offenerSprungRef.current;
    offenerSprungRef.current = null;
    const ziel = fortsetzen?.position ?? offenerSprung;
    if (ziel !== null && ziel !== undefined) geheZu(audio, ziel);
    if (fortsetzen?.wiedergabe) {
      void audio.play().catch(() => {
        // Ohne neuerliche Nutzergeste entscheidet der Abspielknopf.
      });
    }
  }

  async function beiFehler() {
    // Abgelaufene Tickets, Netzwerkfehler und nicht dekodierbare Originale
    // äußern sich alle als Ladefehler: erst einmal still erneuern und an
    // derselben Position weiterspielen.
    if (!stillerVersuchRef.current) {
      stillerVersuchRef.current = true;
      await erneuern(false);
      return;
    }
    if (audioRef.current?.error?.code === 2) {
      // 2: Übertragungsfehler – erneut versuchen bleibt möglich.
      setFehler(LadeFehler);
      return;
    }
    // Nicht abgespielbare Originale bleiben Herunterladsache; wir behandeln
    // sie nie als verifizierten abspielbaren Ableger.
    setFehler({ art: "format", meldung: formatMeldung });
  }

  function vollbildOeffnen() {
    const medium = audioRef.current as
      | (HTMLMediaElement & { webkitEnterFullscreen?: () => void })
      | null;
    if (!medium) return;
    if (typeof medium.requestFullscreen === "function") {
      void medium.requestFullscreen().catch(() => {});
    } else {
      // iPhone: nur das Video selbst kennt einen Vollbildmodus.
      medium.webkitEnterFullscreen?.();
    }
  }

  function umschalten() {
    const audio = audioRef.current;
    if (!audio) return;
    if (audio.paused) {
      void audio.play().catch(() => {
        // Ein vorhandener Ladefehler entscheidet die Meldung; sonst war es
        // ein transienter Startfehler.
        if (audio.error) void beiFehler();
        else setFehler(LadeFehler);
      });
    } else {
      audio.pause();
    }
  }

  function erneutVersuchen() {
    setFehler(null);
    void erneuern(false);
  }

  function beiVolumen(ereignis: React.ChangeEvent<HTMLInputElement>) {
    const wert = Number(ereignis.target.value);
    setLautstaerke(wert);
    if (audioRef.current) audioRef.current.volume = wert;
  }

  const medienEreignisse = {
    preload: "metadata" as const,
    controlsList: herunterladenErlaubt ? undefined : "nodownload",
    onContextMenu: herunterladenErlaubt
      ? undefined
      : (ereignis: React.MouseEvent) => ereignis.preventDefault(),
    onPlay: beiWiedergabeStart,
    onPause: beiWiedergabeStopp,
    onEnded: beiWiedergabeStopp,
    onTimeUpdate: beiZeit,
    onSeeked: beiGesprungen,
    onLoadedMetadata: beiMetadaten,
    onError: () => void beiFehler(),
  };

  return (
    <section
      className={`audio-spieler${art === "video" ? " video-spieler" : ""}${wiedergabe ? " audio-laeuft" : ""}`}
      aria-label={`${art === "video" ? "Video-Spieler" : "Audio-Spieler"} · ${stimme}`}
    >
      {/* Aufnahmen ohne Untertitel-Datei: die beschrifteten
          Bedienelemente und der Status im Klartext tragen die Zugänglichkeit. */}
      {art === "video" ? (
        <video
          ref={setzeMedium}
          className="video-bild"
          playsInline
          controls={vollbild}
          aria-label={stimme}
          {...medienEreignisse}
        />
      ) : (
        <audio ref={setzeMedium} {...medienEreignisse} />
      )}
      <div className="audio-steuerung">
        <button
          type="button"
          onClick={umschalten}
          aria-label={
            wiedergabe ? `${stimme} pausieren` : `${stimme} abspielen`
          }
        >
          {wiedergabe ? "Pause" : "Abspielen"}
        </button>
        <span className="audio-zeit">
          {zeitText(position)} / {dauer !== null ? zeitText(dauer) : "–:––"}
        </span>
        {art === "video" && (
          <button
            type="button"
            className="knopf-leise"
            onClick={vollbildOeffnen}
            aria-label={`${stimme} im Vollbild zeigen`}
          >
            Vollbild
          </button>
        )}
      </div>
      <div className="audio-bereiche">
        <label
          className="visually-hidden"
          htmlFor={`audio-position-${kennung}`}
        >
          Position ({stimme})
        </label>
        <input
          id={`audio-position-${kennung}`}
          type="range"
          min={0}
          max={dauer ?? 0}
          step={0.1}
          value={dauer !== null ? Math.min(position, dauer) : 0}
          disabled={dauer === null}
          onChange={(ereignis) => {
            const wert = Number(ereignis.target.value);
            setPosition(wert);
            onPositionRef.current?.(wert);
            // Lädt gerade ein erneuertes Ticket, gilt die neue Wahl dort.
            if (fortsetzenRef.current) fortsetzenRef.current.position = wert;
            else if (audioRef.current) audioRef.current.currentTime = wert;
          }}
        />
        <label
          className="visually-hidden"
          htmlFor={`audio-lautstaerke-${kennung}`}
        >
          Lautstärke ({stimme})
        </label>
        <input
          id={`audio-lautstaerke-${kennung}`}
          className="audio-lautstaerke"
          type="range"
          min={0}
          max={1}
          step={0.05}
          value={lautstaerke}
          onChange={beiVolumen}
        />
      </div>
      <output className="visually-hidden" aria-live="polite">
        {wiedergabe ? `${stimme} wird abgespielt` : `${stimme} pausiert`}
      </output>
      {abschnitt && (
        <div className="audio-abschnitt">
          <output>
            {abschnittZustand === "aktiv"
              ? `Abschnitt: „${abschnitt.titel}“ · ${bereichText(abschnitt.von, abschnitt.bis)}`
              : abschnittZustand === "ende"
                ? `Ende des Abschnitts „${abschnitt.titel}“ erreicht.`
                : abschnittZustand === "ausserhalb"
                  ? `Die Zeitmarke „${abschnitt.titel}“ (ab ${zeitText(abschnitt.von)}) liegt außerhalb der Datei${dauer !== null ? ` (Dauer ${zeitText(dauer)})` : ""}. Die Aufnahme beginnt am Anfang.`
                  : "Die ganze Aufnahme läuft weiter."}
          </output>
          {abschnittZustand === "ende" && (
            <div className="noten-aktionen">
              <button type="button" onClick={abschnittWiederholen}>
                Abschnitt wiederholen
              </button>
              <button
                type="button"
                className="knopf-leise"
                onClick={ganzWeiterspielen}
              >
                In der ganzen Aufnahme weiterspielen
              </button>
            </div>
          )}
        </div>
      )}
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler.meldung}
        </p>
      )}
      {fehler?.art === "laden" && (
        <div className="noten-aktionen">
          <button type="button" onClick={erneutVersuchen}>
            Erneut versuchen
          </button>
        </div>
      )}
    </section>
  );
}
