import type { ComponentApi } from "@a2ui/web_core/v0_9";
import { z } from "zod";

// ARC-022-4: der Katalog des Archivs für A2UI. Der Server baut jede
// Oberfläche selbst aus Datensatz-IDs und prüft sie gegen den Katalog;
// der Browser prüft trotzdem jede Nachricht noch einmal, bevor etwas
// gezeigt wird, und kennt genau diese sechs Komponenten. Der Basis-Katalog
// von A2UI ist nicht registriert. Kein Feld trägt eine Adresse: Verweise
// leitet der Browser aus den IDs ab.

export const a2uiKatalogId = "urn:liedertafel:archiv:a2ui:katalog:v1";
export const a2uiVersion = "v0.9";
export const a2uiEreignisName = "archive.a2ui";
export const maximaleOberflaechen = 4;

const kennung = z.string().uuid();
const kurzerText = (maximum: number) => z.string().min(1).max(maximum);
const titel = kurzerText(200);

const liedKarteSchema = z
  .object({
    liedId: kennung,
    titel,
    komponist: titel.optional(),
    textdichter: titel.optional(),
    textanfang: titel.optional(),
  })
  .strict();

const auftrittKarteSchema = z
  .object({
    auftrittId: kennung,
    titel,
    art: kurzerText(40),
    datum: kurzerText(60),
    ort: titel.optional(),
  })
  .strict();

const programmListeSchema = z
  .object({
    auftrittId: kennung,
    titel,
    punkte: z
      .array(
        z
          .object({
            position: z.number().int().min(1).max(500),
            liedId: kennung,
            titel,
            notiz: kurzerText(500).optional(),
          })
          .strict(),
      )
      .min(1)
      .max(60),
  })
  .strict();

const vorschlagKarteSchema = z
  .object({
    vorschlagId: kennung,
    titel,
    grund: kurzerText(500),
    vertrauen: z.enum(["Sicher", "Unsicher"]).optional(),
  })
  .strict();

const aenderungsVorschauSchema = z
  .object({
    vorschlagId: kennung,
    titel,
    aenderungen: z
      .array(
        z
          .object({
            feld: kurzerText(80),
            bisher: kurzerText(500).optional(),
            neu: kurzerText(500),
          })
          .strict(),
      )
      .min(1)
      .max(20),
  })
  .strict();

const quellenSchema = z
  .object({
    quellen: z
      .array(
        z
          .object({ liedId: kennung, titel, komponist: titel.optional() })
          .strict(),
      )
      .min(1)
      .max(10),
  })
  .strict();

export type LiedKarteEigenschaften = z.infer<typeof liedKarteSchema>;
export type AuftrittKarteEigenschaften = z.infer<typeof auftrittKarteSchema>;
export type ProgrammListeEigenschaften = z.infer<typeof programmListeSchema>;
export type VorschlagKarteEigenschaften = z.infer<typeof vorschlagKarteSchema>;
export type AenderungsVorschauEigenschaften = z.infer<
  typeof aenderungsVorschauSchema
>;
export type QuellenEigenschaften = z.infer<typeof quellenSchema>;

// Eigenschaften je Komponente; die Schlüssel sind die Namen im Katalog.
export const a2uiKomponentenSchemas = {
  LiedKarte: liedKarteSchema,
  AuftrittKarte: auftrittKarteSchema,
  ProgrammListe: programmListeSchema,
  VorschlagKarte: vorschlagKarteSchema,
  AenderungsVorschau: aenderungsVorschauSchema,
  Quellen: quellenSchema,
} as const;

export type A2uiKomponentenName = keyof typeof a2uiKomponentenSchemas;

export const a2uiKomponentenNamen = Object.keys(
  a2uiKomponentenSchemas,
) as A2uiKomponentenName[];

export const a2uiKomponentenApis: ComponentApi[] = a2uiKomponentenNamen.map(
  (name) => ({ name, schema: a2uiKomponentenSchemas[name] }),
);

const oberflaechenId = z.string().regex(/^s[1-4]$/);

const komponenteSchema = z.union(
  a2uiKomponentenNamen.map((name) =>
    a2uiKomponentenSchemas[name].extend({
      id: z.literal("root"),
      component: z.literal(name),
    }),
  ) as unknown as [z.ZodTypeAny, z.ZodTypeAny, ...z.ZodTypeAny[]],
);

const erstellenSchema = z
  .object({
    version: z.literal(a2uiVersion),
    createSurface: z
      .object({
        surfaceId: oberflaechenId,
        catalogId: z.literal(a2uiKatalogId),
      })
      .strict(),
  })
  .strict();

const aktualisierenSchema = z
  .object({
    version: z.literal(a2uiVersion),
    updateComponents: z
      .object({
        surfaceId: oberflaechenId,
        components: z.array(komponenteSchema).length(1),
      })
      .strict(),
  })
  .strict();

export type A2uiOberflaeche = {
  surfaceId: string;
  // Genau die geprüften Nachrichten: Erstellen, dann die eine Wurzel.
  nachrichten: [
    z.infer<typeof erstellenSchema>,
    z.infer<typeof aktualisierenSchema>,
  ];
};

// Liest die A2UI-Nachrichten einer Antwort. Es zählt nur, was vollständig dem
// Katalog entspricht: je Oberfläche genau ein Erstellen und eine Wurzel einer
// bekannten Komponente mit genau den erlaubten Eigenschaften. Alles andere
// (kaputt, unbekannte Komponente, zusätzliche Eigenschaften, fremder Katalog,
// zu viele Oberflächen) wird verworfen, ohne die übrigen zu stören.
export function leseA2uiOberflaechen(roh: unknown): A2uiOberflaeche[] {
  if (!Array.isArray(roh)) return [];
  type Erstellen = z.infer<typeof erstellenSchema>;
  type Aktualisieren = z.infer<typeof aktualisierenSchema>;
  const erstellungen = new Map<
    string,
    { index: number; nachricht: Erstellen }[]
  >();
  const aktualisierungen = new Map<
    string,
    { index: number; nachricht: Aktualisieren }[]
  >();
  roh.slice(0, maximaleOberflaechen * 4).forEach((eintrag, index) => {
    const neu = erstellenSchema.safeParse(eintrag);
    if (neu.success) {
      const id = neu.data.createSurface.surfaceId;
      erstellungen.set(id, [
        ...(erstellungen.get(id) ?? []),
        { index, nachricht: neu.data },
      ]);
      return;
    }
    const aktuell = aktualisierenSchema.safeParse(eintrag);
    if (!aktuell.success) return;
    const id = aktuell.data.updateComponents.surfaceId;
    aktualisierungen.set(id, [
      ...(aktualisierungen.get(id) ?? []),
      { index, nachricht: aktuell.data },
    ]);
  });
  const fertig: (A2uiOberflaeche & { index: number })[] = [];
  for (const [id, erstellt] of erstellungen) {
    const aktualisiert = aktualisierungen.get(id);
    // Genau ein Erstellen und eine Wurzel danach; jede Doppelung macht die
    // Oberfläche unbrauchbar, statt eine der Fassungen zu erraten.
    if (erstellt.length !== 1 || aktualisiert?.length !== 1) continue;
    if (aktualisiert[0].index < erstellt[0].index) continue;
    fertig.push({
      surfaceId: id,
      index: erstellt[0].index,
      nachrichten: [erstellt[0].nachricht, aktualisiert[0].nachricht],
    });
  }
  return fertig
    .sort((a, b) => a.index - b.index)
    .slice(0, maximaleOberflaechen)
    .map(({ surfaceId, nachrichten }) => ({ surfaceId, nachrichten }));
}

export function liedAdresse(liedId: string): string {
  return `/lied/?id=${encodeURIComponent(liedId)}`;
}

export function auftrittAdresse(auftrittId: string): string {
  return `/auftritt/?id=${encodeURIComponent(auftrittId)}`;
}
