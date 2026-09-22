"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { AssetAccessResponse, SpielerFehler } from "@/lib/assets";
import { fetchAssetAccess, ladeFehlerAusUrsache, zeitText } from "@/lib/assets";
import type { MidiStueck } from "@/lib/midi";
import { liesMidi, MidiFehler } from "@/lib/midi";

/** Wiederholbare Meldung für vorübergehende Ladefehler. */
const LadeFehler: SpielerFehler = {
  art: "laden",
  meldung: "MIDI konnte nicht geladen werden. Bitte erneut versuchen.",
};

/** Kleine Toleranz, damit Noten exakt an der Suchposition noch erklingen. */
const StartEpsilon = 1e-4;

/** Anschlagszeit der Hüllkurve in Sekunden. */
const AngriffSekunden = 0.01;

/** Ausklingzeit am Notenende in Sekunden. */
const LoesenSekunden = 0.05;

/** Scheitelfaktor der Hüllkurve: bescheidene Pegel halten Akkorde klar. */
const ScheitelFaktor = 0.2;

/** Abstand der Positionsaktualisierungen in Millisekunden. */
const TaktMs = 100;

export type MidiSpielerProps = {
  assetId: string;
  /** Beschriftete Stimme (inklusive Vollmix-Ersatz), für Klartext-Labels. */
  stimme: string;
  zugriff: AssetAccessResponse;
  /** Nur ein Spieler läuft gleichzeitig; inaktive pausieren sich selbst. */
  aktiv: boolean;
  onAbspielen: () => void;
  onErneuert: (zugriff: AssetAccessResponse) => void;
};

export function MidiSpieler({
  assetId,
  stimme,
  zugriff,
  aktiv,
  onAbspielen,
  onErneuert,
}: MidiSpielerProps) {
  const [wiedergabe, setWiedergabe] = useState(false);
  const [position, setPosition] = useState(0);
  const [tempo, setTempo] = useState(100);
  const [lautstaerke, setLautstaerke] = useState(1);
  const [stueck, setStueck] = useState<MidiStueck | null>(null);
  const [laedt, setLaedt] = useState(false);
  const [fehler, setFehler] = useState<SpielerFehler | null>(null);
  // Bytes erst nach dem Mount holen: der statische Export rendert diese
  // Komponente serverseitig mit, und dort gibt es kein fetchen.
  const [eingehaengt, setEingehaengt] = useState(false);

  const contextRef = useRef<AudioContext | null>(null);
  const masterRef = useRef<GainNode | null>(null);
  const quellenRef = useRef<AudioScheduledSourceNode[]>([]);
  const taktRef = useRef<number | undefined>(undefined);
  // Anker der Tempo-Rechnung: wall = Echtzeit, basis = Stückzeit; die
  // Position ergibt sich aus der verstrichenen Echtzeit mal dem Tempo.
  const ankerWallRef = useRef(0);
  const ankerBasisRef = useRef(0);
  const stueckRef = useRef<MidiStueck | null>(null);
  const positionRef = useRef(0);
  const tempoRef = useRef(100);
  const lautstaerkeRef = useRef(1);
  const wiedergabeRef = useRef(false);
  const onErneuertRef = useRef(onErneuert);
  onErneuertRef.current = onErneuert;

  const stoppQuellen = useCallback(() => {
    for (const quelle of quellenRef.current) {
      try {
        quelle.stop();
      } catch {
        // Quelle startete nie oder endete bereits.
      }
    }
    quellenRef.current = [];
  }, []);

  const aktuellePosition = useCallback(() => {
    const context = contextRef.current;
    if (!wiedergabeRef.current || !context) return positionRef.current;
    const faktor = tempoRef.current / 100;
    const position =
      ankerBasisRef.current +
      (context.currentTime - ankerWallRef.current) * faktor;
    const dauer = stueckRef.current?.dauer ?? Number.POSITIVE_INFINITY;
    return Math.min(dauer, position);
  }, []);

  /** Plant alle Noten ab der Stückposition anhand der aktuellen Anker. */
  const planeVon = useCallback((ab: number) => {
    const context = contextRef.current;
    const master = masterRef.current;
    const geplant = stueckRef.current;
    if (!context || !master || !geplant) return;
    const faktor = tempoRef.current / 100;
    for (const note of geplant.noten) {
      if (note.start + StartEpsilon < ab) continue;
      const wann =
        ankerWallRef.current + (note.start - ankerBasisRef.current) / faktor;
      const ende = wann + note.dauer / faktor;
      if (ende <= wann) continue;
      const oszillator = context.createOscillator();
      oszillator.type = "triangle";
      oszillator.frequency.value = 440 * 2 ** ((note.ton - 69) / 12);
      // Kurze Hüllkurve pro Note: weicher Anschlag, schnelles Ausklingen;
      // bescheidene Scheitelwerte halten Akkorde ohne Übersteuern.
      const huelle = context.createGain();
      const scheitel = ScheitelFaktor * note.staerke;
      const angriff = wann + AngriffSekunden;
      const loesen = Math.max(angriff, ende - LoesenSekunden);
      huelle.gain.setValueAtTime(0.0001, wann);
      huelle.gain.linearRampToValueAtTime(scheitel, angriff);
      huelle.gain.setValueAtTime(scheitel, loesen);
      huelle.gain.linearRampToValueAtTime(0.0001, ende);
      oszillator.connect(huelle);
      huelle.connect(master);
      oszillator.start(wann);
      oszillator.stop(ende);
      quellenRef.current.push(oszillator);
    }
  }, []);

  const takt = useCallback(() => {
    const context = contextRef.current;
    const geplant = stueckRef.current;
    if (!context || !geplant || !wiedergabeRef.current) return;
    const faktor = tempoRef.current / 100;
    const position =
      ankerBasisRef.current +
      (context.currentTime - ankerWallRef.current) * faktor;
    if (position >= geplant.dauer) {
      // Stückende: alles stoppen, das nächste Abspielen startet vorn.
      stoppQuellen();
      window.clearInterval(taktRef.current);
      wiedergabeRef.current = false;
      positionRef.current = geplant.dauer;
      setWiedergabe(false);
      setPosition(geplant.dauer);
      return;
    }
    positionRef.current = position;
    setPosition(position);
  }, [stoppQuellen]);

  /** Pausiert und behält die Position für das Wiederaufnehmen. */
  const pausieren = useCallback(() => {
    const position = aktuellePosition();
    stoppQuellen();
    window.clearInterval(taktRef.current);
    wiedergabeRef.current = false;
    positionRef.current = position;
    setWiedergabe(false);
    setPosition(position);
  }, [aktuellePosition, stoppQuellen]);

  const laden = useCallback(async (ticket: AssetAccessResponse) => {
    setLaedt(true);
    try {
      // Ticketierte Blob-URL: die Bytes kommen ohne Anmeldeinformationen.
      const antwort = await fetch(ticket.viewUrl);
      if (antwort.status === 404 || antwort.status === 401) {
        setFehler(ladeFehlerAusUrsache(antwort, LadeFehler.meldung));
        return;
      }
      if (!antwort.ok) throw antwort;
      const geplant = liesMidi(await antwort.arrayBuffer());
      // Nach dem Parsen werden die Bytes nicht mehr gebraucht.
      stueckRef.current = geplant;
      setStueck(geplant);
      setFehler(null);
    } catch (ursache) {
      if (ursache instanceof MidiFehler) {
        // Zerbrochene oder nicht unterstützte Datei: kein Wiederholungsversuch;
        // der Download bleibt außerhalb des Spielers möglich.
        setFehler({
          art: "format",
          meldung:
            "Diese MIDI-Datei kann nicht abgespielt werden. Die Datei kann weiterhin heruntergeladen werden.",
        });
        return;
      }
      setFehler(ladeFehlerAusUrsache(ursache, LadeFehler.meldung));
    } finally {
      setLaedt(false);
    }
  }, []);

  // Einmaliges Laden nach dem Mount; erneuerte Tickets beschafft
  // „Erneut versuchen" selbst.
  const anfangsZugriffRef = useRef(zugriff);

  useEffect(() => {
    setEingehaengt(true);
    void laden(anfangsZugriffRef.current);
  }, [laden]);

  // Nur der aktive Spieler läuft; ein anderer startet, pausiert dieser
  // und behält seine Position.
  useEffect(() => {
    if (!aktiv) pausieren();
  }, [aktiv, pausieren]);

  // Beim Verlassen alles freigeben: Taktgeber, Quellen, Audio-Kontext.
  useEffect(() => {
    return () => {
      window.clearInterval(taktRef.current);
      stoppQuellen();
      void contextRef.current?.close();
    };
  }, [stoppQuellen]);

  function umschalten() {
    const geplant = stueckRef.current;
    if (!geplant) return;
    if (wiedergabeRef.current) {
      pausieren();
      return;
    }
    // Audio-Kontext erst im Klick-Handler erzeugen: mobile Browser
    // verlangen die Nutzergeste; danach bleibt der Kontext bestehen.
    if (!contextRef.current || !masterRef.current) {
      const context = new AudioContext();
      const master = context.createGain();
      master.gain.value = lautstaerkeRef.current;
      // Der Begrenzer hält Summenklirren dichter Akkorde unterhalb der
      // Vollaussteuerung, ohne normale Wiedergabe zu verändern.
      const begrenzer = context.createDynamicsCompressor();
      master.connect(begrenzer);
      begrenzer.connect(context.destination);
      contextRef.current = context;
      masterRef.current = master;
    }
    void contextRef.current.resume();
    // Nach dem Ende startet das nächste Abspielen wieder vorn.
    const ab = positionRef.current >= geplant.dauer ? 0 : positionRef.current;
    const context = contextRef.current;
    ankerWallRef.current = context.currentTime;
    ankerBasisRef.current = ab;
    planeVon(ab);
    wiedergabeRef.current = true;
    positionRef.current = ab;
    setWiedergabe(true);
    setPosition(ab);
    window.clearInterval(taktRef.current);
    taktRef.current = window.setInterval(takt, TaktMs);
    onAbspielen();
  }

  function beiPosition(ereignis: React.ChangeEvent<HTMLInputElement>) {
    const wert = Number(ereignis.target.value);
    stoppQuellen();
    positionRef.current = wert;
    setPosition(wert);
    if (!wiedergabeRef.current) return;
    // Anker neu fassen, damit die Restzeit an der neuen Position weiterrechnet.
    const context = contextRef.current;
    if (!context) return;
    ankerWallRef.current = context.currentTime;
    ankerBasisRef.current = wert;
    planeVon(wert);
  }

  function beiTempo(ereignis: React.ChangeEvent<HTMLInputElement>) {
    const wert = Number(ereignis.target.value);
    tempoRef.current = wert;
    setTempo(wert);
    if (!wiedergabeRef.current) return;
    // Anker neu fassen: laufende Wiedergabe geht nahtlos im neuen Tempo
    // ab der aktuellen Position weiter.
    const aktuelle = aktuellePosition();
    stoppQuellen();
    const context = contextRef.current;
    if (!context) return;
    ankerWallRef.current = context.currentTime;
    ankerBasisRef.current = aktuelle;
    planeVon(aktuelle);
  }

  function beiLautstaerke(ereignis: React.ChangeEvent<HTMLInputElement>) {
    const wert = Number(ereignis.target.value);
    lautstaerkeRef.current = wert;
    setLautstaerke(wert);
    if (masterRef.current) masterRef.current.gain.value = wert;
  }

  async function erneutVersuchen() {
    setFehler(null);
    // Das alte Ticket kann abgelaufen sein: erst frische Zugriffsrechte
    // holen und dem Besitzer melden, dann die Bytes neu laden.
    let neu: AssetAccessResponse;
    try {
      neu = await fetchAssetAccess(assetId);
    } catch (ursache) {
      setFehler(ladeFehlerAusUrsache(ursache, LadeFehler.meldung));
      return;
    }
    onErneuertRef.current(neu);
    await laden(neu);
  }

  const bereit = eingehaengt && stueck !== null && !laedt;

  return (
    <section
      className={`audio-spieler midi-spieler${wiedergabe ? " audio-laeuft" : ""}`}
      aria-label={`MIDI-Spieler · ${stimme}`}
    >
      {/* Synthetisierte Wiedergabe ohne Untertitel: die beschrifteten
          Bedienelemente und der Status im Klartext tragen die Zugänglichkeit. */}
      <div className="audio-steuerung">
        <button
          type="button"
          onClick={umschalten}
          disabled={!bereit}
          aria-label={
            wiedergabe
              ? `${stimme} (MIDI) pausieren`
              : `${stimme} (MIDI) abspielen`
          }
        >
          {wiedergabe ? "Pause" : "Abspielen"}
        </button>
        {!eingehaengt || (laedt && fehler === null) ? (
          <span className="audio-zeit">MIDI wird geladen …</span>
        ) : (
          <span className="audio-zeit">
            {zeitText(position)} /{" "}
            {stueck !== null ? zeitText(stueck.dauer) : "–:––"}
          </span>
        )}
        <span className="audio-zeit">{tempo} %</span>
      </div>
      <div className="audio-bereiche">
        <label className="visually-hidden" htmlFor={`midi-position-${assetId}`}>
          Position ({stimme})
        </label>
        <input
          id={`midi-position-${assetId}`}
          type="range"
          min={0}
          max={stueck?.dauer ?? 0}
          step={0.1}
          value={stueck !== null ? Math.min(position, stueck.dauer) : 0}
          disabled={stueck === null}
          onChange={beiPosition}
        />
        <label className="visually-hidden" htmlFor={`midi-tempo-${assetId}`}>
          Tempo ({stimme})
        </label>
        <input
          id={`midi-tempo-${assetId}`}
          type="range"
          min={50}
          max={150}
          step={5}
          value={tempo}
          disabled={stueck === null}
          onChange={beiTempo}
        />
        <label
          className="visually-hidden"
          htmlFor={`midi-lautstaerke-${assetId}`}
        >
          Lautstärke ({stimme})
        </label>
        <input
          id={`midi-lautstaerke-${assetId}`}
          className="audio-lautstaerke"
          type="range"
          min={0}
          max={1}
          step={0.05}
          value={lautstaerke}
          onChange={beiLautstaerke}
        />
      </div>
      <output className="visually-hidden" aria-live="polite">
        {wiedergabe
          ? `MIDI (${stimme}) wird abgespielt`
          : `MIDI (${stimme}) pausiert`}
      </output>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler.meldung}
        </p>
      )}
      {fehler?.art === "laden" && (
        <div className="noten-aktionen">
          <button type="button" onClick={() => void erneutVersuchen()}>
            Erneut versuchen
          </button>
        </div>
      )}
    </section>
  );
}
