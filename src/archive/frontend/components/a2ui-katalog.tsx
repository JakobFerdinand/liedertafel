"use client";

import {
  createComponentImplementation,
  type ReactComponentImplementation,
} from "@a2ui/react/v0_9";
import { Catalog } from "@a2ui/web_core/v0_9";
import Link from "next/link";
import {
  type AuftrittKarteEigenschaften,
  a2uiKatalogId,
  a2uiKomponentenApis,
  a2uiVersion,
  auftrittAdresse,
  type LiedKarteEigenschaften,
  liedAdresse,
  type ProgrammListeEigenschaften,
  type QuellenEigenschaften,
} from "@/lib/a2ui";
import { auftrittArtName } from "@/lib/events";

// Die Komponenten des Archivs für A2UI (ARC-022-4). Sie zeigen nur, was der
// Server aufgelöst hat; Verweise entstehen hier aus den IDs, nie aus Text.

function LiedKarte({ props }: { props: LiedKarteEigenschaften }) {
  return (
    <article className="a2ui-karte" aria-label={`Lied: ${props.titel}`}>
      <h3 className="a2ui-titel">{props.titel}</h3>
      {(props.komponist || props.textdichter) && (
        <dl className="a2ui-angaben">
          {props.komponist && (
            <div>
              <dt>Komponist</dt>
              <dd>{props.komponist}</dd>
            </div>
          )}
          {props.textdichter && (
            <div>
              <dt>Text</dt>
              <dd>{props.textdichter}</dd>
            </div>
          )}
        </dl>
      )}
      {props.textanfang && (
        <p className="a2ui-textanfang">{props.textanfang}</p>
      )}
      <Link
        className="a2ui-verweis"
        href={liedAdresse(props.liedId)}
        aria-label={`Lied im Archiv öffnen: ${props.titel}`}
      >
        Lied im Archiv öffnen <span aria-hidden="true">↗</span>
      </Link>
    </article>
  );
}

function AuftrittKarte({ props }: { props: AuftrittKarteEigenschaften }) {
  return (
    <article className="a2ui-karte" aria-label={`Auftritt: ${props.titel}`}>
      <h3 className="a2ui-titel">{props.titel}</h3>
      <dl className="a2ui-angaben">
        <div>
          <dt>Art</dt>
          <dd>{auftrittArtName(props.art)}</dd>
        </div>
        <div>
          <dt>Datum</dt>
          <dd>{props.datum}</dd>
        </div>
        {props.ort && (
          <div>
            <dt>Ort</dt>
            <dd>{props.ort}</dd>
          </div>
        )}
      </dl>
      <Link
        className="a2ui-verweis"
        href={auftrittAdresse(props.auftrittId)}
        aria-label={`Auftritt im Archiv öffnen: ${props.titel}`}
      >
        Auftritt im Archiv öffnen <span aria-hidden="true">↗</span>
      </Link>
    </article>
  );
}

function ProgrammListe({ props }: { props: ProgrammListeEigenschaften }) {
  return (
    <section className="a2ui-karte" aria-label={`Programm: ${props.titel}`}>
      <h3 className="a2ui-titel">Programm: {props.titel}</h3>
      <ol className="a2ui-liste">
        {props.punkte.map((punkt) => (
          <li key={`${punkt.position}-${punkt.liedId}`}>
            <Link href={liedAdresse(punkt.liedId)}>{punkt.titel}</Link>
            {punkt.notiz && (
              <span className="a2ui-notiz"> – {punkt.notiz}</span>
            )}
          </li>
        ))}
      </ol>
      <Link
        className="a2ui-verweis"
        href={auftrittAdresse(props.auftrittId)}
        aria-label={`Auftritt im Archiv öffnen: ${props.titel}`}
      >
        Auftritt im Archiv öffnen <span aria-hidden="true">↗</span>
      </Link>
    </section>
  );
}

function Quellen({ props }: { props: QuellenEigenschaften }) {
  return (
    <section className="a2ui-karte" aria-label="Lieder im Archiv">
      <h3 className="a2ui-titel">Lieder im Archiv</h3>
      <ul className="a2ui-liste">
        {props.quellen.map((quelle) => (
          <li key={quelle.liedId}>
            <Link
              href={liedAdresse(quelle.liedId)}
              aria-label={`Lied im Archiv öffnen: ${quelle.titel}`}
            >
              {quelle.titel}
            </Link>
            {quelle.komponist && (
              <span className="a2ui-notiz"> – {quelle.komponist}</span>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

// Wie der Generic Binder die Eigenschaften reicht, ist Sache der Bibliothek;
// die Schemas in lib/a2ui.ts sind strikt und lassen nur einfache Werte zu,
// daher sind die aufgelösten Eigenschaften genau diese Typen.
const ansichten: Record<string, (eigenschaften: never) => React.ReactNode> = {
  LiedKarte: (props: LiedKarteEigenschaften) => <LiedKarte props={props} />,
  AuftrittKarte: (props: AuftrittKarteEigenschaften) => (
    <AuftrittKarte props={props} />
  ),
  ProgrammListe: (props: ProgrammListeEigenschaften) => (
    <ProgrammListe props={props} />
  ),
  Quellen: (props: QuellenEigenschaften) => <Quellen props={props} />,
};

const implementierungen: ReactComponentImplementation[] =
  a2uiKomponentenApis.map((api) =>
    createComponentImplementation(api, ({ props }) =>
      (ansichten[api.name] as (eigenschaften: unknown) => React.ReactNode)(
        props,
      ),
    ),
  );

// Der Katalog besteht nur aus den vier Komponenten des Archivs und hat keine
// Funktionen; ein Basis-Katalog wird nicht eingebunden.
export const archivKatalog = new Catalog<ReactComponentImplementation>(
  a2uiKatalogId,
  a2uiVersion,
  implementierungen,
  [],
);
