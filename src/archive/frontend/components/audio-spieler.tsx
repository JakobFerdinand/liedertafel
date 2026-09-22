"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { AssetAccessResponse } from "@/lib/assets";
import { fetchAssetAccess, restlaufzeitMs } from "@/lib/assets";

type FehlerArt = {
  art: "format" | "laden";
  meldung: string;
};

/** Vorlauf vor dem Ticketablauf, zu dem still neue Tickets angefordert werden. */
const ErneuerungsVorlaufMs = 60_000;

/** Nachlauf für einen stillen Erneuerungsversuch nach einem Fehlschlag. */
const ErneuerungsWiederholungMs = 30_000;

/** Wiederholbare Meldung für vorübergehende Ladefehler. */
const LadeFehler: FehlerArt = {
  art: "laden",
  meldung: "Audio konnte nicht geladen werden. Bitte erneut versuchen.",
};

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

function zeitText(sekunden: number) {
  if (!Number.isFinite(sekunden) || sekunden < 0) return "0:00";
  const gesamt = Math.floor(sekunden);
  const stunden = Math.floor(gesamt / 3600);
  const minuten = Math.floor((gesamt % 3600) / 60);
  const rest = gesamt % 60;
  const mm = stunden > 0 ? String(minuten).padStart(2, "0") : String(minuten);
  const ss = String(rest).padStart(2, "0");
  return stunden > 0 ? `${stunden}:${mm}:${ss}` : `${mm}:${ss}`;
}

export function AudioSpieler({
  assetId,
  stimme,
  zugriff,
  aktiv,
  onAbspielen,
  onErneuert,
}: AudioSpielerProps) {
  const audioRef = useRef<HTMLAudioElement | null>(null);
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

  const [wiedergabe, setWiedergabe] = useState(false);
  const [position, setPosition] = useState(0);
  const [dauer, setDauer] = useState<number | null>(null);
  const [lautstaerke, setLautstaerke] = useState(1);
  const [fehler, setFehler] = useState<FehlerArt | null>(null);
  // Die Ticket-URL wird erst nach dem Mount gesetzt: servergerendertes
  // Audio lädt sofort und kann Fehler feuern, bevor React die Behandler
  // angehängt hat.
  const [eingehaengt, setEingehaengt] = useState(false);

  useEffect(() => {
    setEingehaengt(true);
  }, []);

  const erneuern = useCallback(
    async (hintergrund: boolean) => {
      const audio = audioRef.current;
      if (audio) {
        fortsetzenRef.current = {
          position: audio.currentTime,
          wiedergabe: wiedergabeRef.current,
        };
      }
      try {
        const neu = await fetchAssetAccess(assetId);
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
        const status = ursache instanceof Response ? ursache.status : 0;
        setFehler(
          status === 404
            ? {
                art: "laden",
                meldung: "Für dieses Material liegt keine abrufbare Datei vor.",
              }
            : status === 401
              ? {
                  art: "laden",
                  meldung:
                    "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.",
                }
              : LadeFehler,
        );
      }
    },
    [assetId],
  );

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
    setFehler({
      art: "format",
      meldung:
        "Dieses Audioformat kann im Browser nicht wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden.",
    });
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

  return (
    <section
      className={`audio-spieler${wiedergabe ? " audio-laeuft" : ""}`}
      aria-label={`Audio-Spieler · ${stimme}`}
    >
      {/* Sprachaufnahmen ohne Untertitel-Datei: die beschrifteten
          Bedienelemente und der Status im Klartext tragen die Zugänglichkeit. */}
      {/* biome-ignore lint/a11y/useMediaCaption: Sprachaufnahmen haben keine Untertitel-Dateien */}
      <audio
        ref={audioRef}
        src={eingehaengt ? zugriff.viewUrl : undefined}
        preload="metadata"
        onPlay={beiWiedergabeStart}
        onPause={beiWiedergabeStopp}
        onEnded={beiWiedergabeStopp}
        onTimeUpdate={beiZeit}
        onLoadedMetadata={beiMetadaten}
        onError={() => void beiFehler()}
      />
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
      </div>
      <div className="audio-bereiche">
        <label
          className="visually-hidden"
          htmlFor={`audio-position-${assetId}`}
        >
          Position ({stimme})
        </label>
        <input
          id={`audio-position-${assetId}`}
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
          htmlFor={`audio-lautstaerke-${assetId}`}
        >
          Lautstärke ({stimme})
        </label>
        <input
          id={`audio-lautstaerke-${assetId}`}
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
