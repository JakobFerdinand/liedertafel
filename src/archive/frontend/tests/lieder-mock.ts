import type { Page } from "@playwright/test";

// Gemeinsame Mocks für die Spezifikationen zum flachen Lied (#166): Sitzung,
// Antworten von /api/lieder und /api/lieder/{id}. Nur ergänzen, nichts umbenennen.

export const editorMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000001",
  email: "redaktion@liedertafel.test",
  displayName: "Redaktion",
  roles: ["Editor"],
  verifiedAt: new Date().toISOString(),
};

export const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

export function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  };
}

export function problem(title: string, status: number) {
  return {
    status,
    contentType: "application/problem+json",
    body: JSON.stringify({ title }),
  };
}

export async function mockSitzung(page: Page, me: unknown) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

export const mondId = "00000000-0000-0000-0000-00000000a001";
export const wolkenId = "00000000-0000-0000-0000-00000000a002";

export const mondEintrag = {
  id: mondId,
  title: "Der Mond ist aufgegangen",
  composer: "J. A. P. Schulz",
  arranger: null,
  lyricist: "Matthias Claudius",
  voiceConfiguration: "TTBB",
  musicalKey: "F-Dur",
  language: "Deutsch",
  occasion: "Abendlied",
  dateiTypen: ["noten"],
};

export const wolkenEintrag = {
  id: wolkenId,
  title: "Über den Wolken",
  composer: null,
  arranger: null,
  lyricist: null,
  voiceConfiguration: "SATB",
  musicalKey: null,
  language: null,
  occasion: "Frühling",
  dateiTypen: [],
};

export function liste(lieder: unknown[], extra: Record<string, unknown> = {}) {
  return {
    query: null,
    page: 1,
    pageSize: 20,
    total: lieder.length,
    filters: { voiceConfiguration: null, occasion: null, dateiTypen: [] },
    options: {
      voiceConfigurations: ["SATB", "TTBB"],
      occasions: ["Abendlied", "Frühling"],
    },
    lieder,
    ...extra,
  };
}

export function datei(
  id: string,
  extra: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    id,
    kind: "noten",
    isPartitur: false,
    stimme: "Tenor 1",
    description: null,
    fileName: "tenor1.pdf",
    contentType: "application/pdf",
    sizeBytes: 400000,
    createdAt: "2026-09-19T12:00:00.000Z",
    ...extra,
  };
}

export function liedDetail(
  dateien: unknown[],
  extra: Record<string, unknown> = {},
) {
  const { dateiTypen: _ignoriert, ...kern } = mondEintrag;
  return {
    lied: {
      ...kern,
      lyrics: "Der Mond ist aufgegangen, die goldnen Sternlein prangen",
      createdAt: "2026-08-20T08:00:00.000Z",
      updatedAt: "2026-09-01T10:00:00.000Z",
      rowVersion: 3,
      dateien,
      ...extra,
    },
  };
}

// #178: der angefangene Entwurf im Backend, nachgebildet. Das Backend hält den
// Entwurf (PUT/GET/DELETE) und die Lesungen je Datei; die echte Logik prüfen
// die xUnit-Tests (LiedEntwurfApiTests). Hier zählt, was der Browser damit tut.
export type Lesung = {
  anfrageId: number;
  status: "liest" | "gelesen" | "fehlgeschlagen" | "nicht-verfuegbar";
  meldung: string;
  felder: { feld: string; wert: string; sicher: boolean }[];
};

type EntwurfBody = {
  entwurfId: string;
  lied: Record<string, string>;
  dateien: { id: string; name: string }[];
} & Record<string, unknown>;

export type EntwurfSpeicher = {
  entwuerfe: Map<string, EntwurfBody>;
  lesungen: Map<string, Lesung>;
  verworfen: string[];
  gespeichert: number;
  /** Was der Server nach dem Schließen des Tabs zu Ende gelesen hat. */
  lesungSetzen: (entwurfId: string, dateiId: string, lesung: Lesung) => void;
};

export async function mockEntwuerfe(page: Page): Promise<EntwurfSpeicher> {
  const speicher: EntwurfSpeicher = {
    entwuerfe: new Map(),
    lesungen: new Map(),
    verworfen: [],
    gespeichert: 0,
    lesungSetzen: (entwurfId, dateiId, lesung) => {
      speicher.lesungen.set(`${entwurfId}:${dateiId}`, lesung);
    },
  };
  const nachId = (url: string) => url.split("/").pop()?.split("?")[0] ?? "";
  // "Angefangen" fragt seit #131 auch die Auftrittsentwürfe; hier gibt es keine.
  await page.route("**/api/auftritt-entwuerfe", (route) =>
    route.fulfill(json({ entwuerfe: [] })),
  );
  await page.route("**/api/lied-entwuerfe", (route) =>
    route.fulfill(
      json({
        entwuerfe: [...speicher.entwuerfe.values()].map((e) => ({
          entwurfId: e.entwurfId,
          titel: e.lied.title,
          dateien: e.dateien.length,
          aktualisiertAm: "2026-10-19T09:00:00.000Z",
        })),
      }),
    ),
  );
  await page.route("**/api/lied-entwuerfe/*", (route) => {
    const id = nachId(route.request().url());
    switch (route.request().method()) {
      case "PUT": {
        const body = route.request().postDataJSON() as EntwurfBody;
        // Die Lesungen führt der Server selbst, sie kommen nie vom Browser.
        if (body.dateien.some((datei) => "lesung" in datei)) {
          return route.fulfill(
            problem("Lesungen gehören nicht in den Entwurf.", 400),
          );
        }
        speicher.entwuerfe.set(id, body);
        speicher.gespeichert++;
        return route.fulfill(
          json({ aktualisiertAm: "2026-10-19T09:00:00.000Z" }),
        );
      }
      case "DELETE":
        speicher.entwuerfe.delete(id);
        speicher.verworfen.push(id);
        return route.fulfill({ status: 204 });
      default: {
        const entwurf = speicher.entwuerfe.get(id);
        if (!entwurf) return route.fulfill(problem("Nicht gefunden.", 404));
        return route.fulfill(
          json({
            entwurf: {
              ...entwurf,
              dateien: entwurf.dateien.map((datei) => ({
                ...datei,
                lesung: speicher.lesungen.get(`${id}:${datei.id}`) ?? null,
              })),
            },
          }),
        );
      }
    }
  });
  return speicher;
}
