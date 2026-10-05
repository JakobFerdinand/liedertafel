"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { SpielerFehler } from "@/lib/assets";
import { ladeFehlerAusUrsache, restlaufzeitMs, zeitText } from "@/lib/assets";

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
  const [vollbild, setVollbild] = useState(false);
  const setzeMedium = useCallback((element: HTMLMediaElement | null) => {
    audioRef.current = element;
  }, []);

  const [wiedergabe, setWiedergabe] = useState(false);
  const [position, setPosition] = useState(0);
  const [dauer, setDauer] = useState<number | null>(null);
  const [lautstaerke, setLautstaerke] = useState(1);
  const [fehler, setFehler] = useState<SpielerFehler | null>(null);
  // Die Ticket-URL wird erst nach dem Mount gesetzt: servergerendertes
  // Audio lädt sofort und kann Fehler feuern, bevor React die Behandler
  // angehängt hat.
  const [eingehaengt, setEingehaengt] = useState(false);

  useEffect(() => {
    setEingehaengt(true);
  }, []);

  const erneuern = useCallback(async (hintergrund: boolean) => {
    const audio = audioRef.current;
    if (audio) {
      fortsetzenRef.current = {
        position: audio.currentTime,
        wiedergabe: wiedergabeRef.current,
      };
    }
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
    if (medium && medium.readyState >= 1) {
      medium.currentTime = ziel;
      setPosition(ziel);
    } else {
      offenerSprungRef.current = ziel;
    }
  }, [sprungSekunden, sprungMarke]);

  // Im Vollbild trägt der Browser die Bedienung; die eigenen Regler liegen
  // dann ausserhalb des sichtbaren Bereichs.
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
    if (audio) setPosition(audio.currentTime);
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
    if (offenerSprung !== null && !fortsetzen) {
      try {
        audio.currentTime = offenerSprung;
        setPosition(offenerSprung);
      } catch {
        // Position jenseits der Datei: Wiedergabe startet vorn.
      }
    }
    if (fortsetzen) {
      try {
        audio.currentTime = fortsetzen.position;
      } catch {
        // Position jenseits der neuen Datei: Wiedergabe startet vorn.
      }
      if (fortsetzen.wiedergabe) {
        void audio.play().catch(() => {
          // Ohne neuerliche Nutzergeste entscheidet der Abspielknopf.
        });
      }
    }
  }

  async function beiFehler() {
    // Abgelaufene Tickets, Netzwerkfehler und nicht dekodierbare Originale
    // äussern sich alle als Ladefehler: erst einmal still erneuern und an
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
    src: eingehaengt ? zugriff.viewUrl : undefined,
    preload: "metadata" as const,
    onPlay: beiWiedergabeStart,
    onPause: beiWiedergabeStopp,
    onEnded: beiWiedergabeStopp,
    onTimeUpdate: beiZeit,
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
            if (audioRef.current) audioRef.current.currentTime = wert;
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
