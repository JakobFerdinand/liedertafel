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
