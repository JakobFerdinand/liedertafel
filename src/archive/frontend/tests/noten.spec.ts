import { expect, type Page, test } from "@playwright/test";

const editorMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000001",
  email: "redaktion@liedertafel.test",
  displayName: "Redaktion",
  roles: ["Editor"],
  verifiedAt: new Date().toISOString(),
};

const memberMe = {
  authenticated: true,
  accountId: "00000000-0000-0000-0000-000000000002",
  email: "mitglied@liedertafel.test",
  displayName: "Testmitglied",
  roles: ["Member"],
  verifiedAt: new Date().toISOString(),
};

const songId = "00000000-0000-0000-0000-00000000000a";
const arrangementId = "00000000-0000-0000-0000-00000000c00a";
const versionId = "00000000-0000-0000-0000-00000000d00a";
const assetId = "00000000-0000-0000-0000-00000000e00a";
const revisionId = "00000000-0000-0000-0000-00000000f00a";

const revision = {
  revisionId,
  revisionNumber: 1,
  contentType: "application/pdf",
  sizeBytes: 419430,
  createdAt: "2026-09-18T10:00:00.000Z",
};

const asset = {
  id: assetId,
  assetType: "score",
  voiceLabel: null,
  currentRevision: revision,
};

function json(body: unknown, status = 200) {
  return {
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  };
}

function problem(title: string, status: number) {
  return {
    status,
    contentType: "application/problem+json",
    body: JSON.stringify({ title }),
  };
}

function fassung(assets: unknown) {
  return {
    id: versionId,
    label: "Standardfassung",
    creator: "Josef Gabriel",
    musicalKey: null,
    assets,
  };
}

const leeresAsset = {
  id: assetId,
  assetType: "score",
  voiceLabel: null,
  currentRevision: null,
};

function detail(songAssets: unknown = []) {
  return {
    song: {
      id: songId,
      title: "Das Wandern ist des Müllers Lust",
      composer: "Carl Friedrich Zöllner",
      lyricist: "Wilhelm Müller",
      published: true,
      publishedAt: "2026-09-01T10:00:00.000Z",
      createdAt: "2026-08-20T08:00:00.000Z",
      updatedAt: "2026-09-01T10:00:00.000Z",
      arrangements: [
        {
          id: arrangementId,
          label: "Satz für gemischten Chor",
          arranger: null,
          voiceConfiguration: null,
          musicalVersions: [fassung(songAssets)],
        },
      ],
    },
  };
}

async function mockSitzung(page: Page, me: unknown) {
  await page.route("**/api/auth/me", (route) => route.fulfill(json(me)));
  await page.route("**/api/antiforgery", (route) =>
    route.fulfill(json({ token: "test" })),
  );
}

const viewUrl = "https://speicher.test/ansicht?sig=ansicht-1";
const downloadUrl = "https://speicher.test/laden?sig=laden-1";
const uploadUrl = "https://speicher.test/ubertragung?sig=upload-1";
const uploadMuster = /speicher\.test\/ubertragung/;

function zugriff() {
  return {
    assetId,
    revisionId,
    revisionNumber: 1,
    contentType: "application/pdf",
    sizeBytes: 419430,
    createdAt: "2026-09-18T10:00:00.000Z",
    viewUrl,
    downloadUrl,
    expiresAt: "2026-09-19T13:00:00.000Z",
  };
}

test("Mitglied sieht Noten und kann sie anzeigen und herunterladen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([asset]))),
  );
  let zugriffe = 0;
  await page.route(`**/api/assets/${assetId}/access`, (route) => {
    zugriffe += 1;
    return route.fulfill(json(zugriff()));
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByText("Noten · Fassung 1 · 0,4 MB · PDF"),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Noten anzeigen" }),
  ).toBeVisible();
  await expect(page.getByText("Noten hochladen")).toHaveCount(0);

  await page.getByRole("button", { name: "Noten anzeigen" }).click();
  const rahmen = page.locator("iframe[title='Noten (PDF)']");
  await expect(rahmen).toBeVisible();
  await expect(rahmen).toHaveAttribute("src", viewUrl);
  const laden = page.getByRole("link", { name: "Herunterladen" });
  await expect(laden).toBeVisible();
  await expect(laden).toHaveAttribute("href", downloadUrl);
  expect(zugriffe).toBe(1);

  expect(errors).toEqual([]);
});

test("Mitglied sieht nichts, wenn keine aktuellen Noten vorliegen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, memberMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByRole("button", { name: "Noten anzeigen" }),
  ).toHaveCount(0);
  await expect(page.locator(".noten-bereich")).toHaveCount(0);

  expect(errors).toEqual([]);
});

test("Redaktion lädt Noten hoch; Reihenfolge und Übertragung stimmen", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const schritte: string[] = [];
  const neueAssetId = "00000000-0000-0000-0000-00000000e00b";
  let notenAssets: unknown[] = [];
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(notenAssets))),
  );
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) => {
    schritte.push("asset");
    expect(route.request().method()).toBe("POST");
    expect(route.request().headers()["x-csrf-token"]).toBe("test");
    return route.fulfill(
      json(
        {
          id: neueAssetId,
          musicalVersionId: versionId,
          assetType: "score",
          voiceLabel: null,
          createdAt: "2026-09-19T12:00:00.000Z",
          currentRevision: null,
        },
        201,
      ),
    );
  });
  await page.route(`**/api/assets/${neueAssetId}/upload-session`, (route) => {
    schritte.push("sitzung");
    expect(route.request().method()).toBe("POST");
    return route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
        },
        201,
      ),
    );
  });
  await page.route(uploadMuster, (route) => {
    schritte.push("uebertragung");
    expect(route.request().method()).toBe("PUT");
    expect(route.request().headers()["content-type"]).toBe("application/pdf");
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    schritte.push("finalisierung");
    expect(route.request().method()).toBe("POST");
    notenAssets = [asset];
    return route.fulfill(json({ assetId, ...revision }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await expect(
    page.getByRole("button", { name: "Noten hochladen" }),
  ).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Noten hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 wandern"),
  });

  await expect(page.getByText("Noten veröffentlicht.")).toBeVisible();
  await expect(
    page.getByText("Noten · Fassung 1 · 0,4 MB · PDF"),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Neue Fassung hochladen" }),
  ).toBeVisible();
  expect(schritte).toEqual([
    "asset",
    "sitzung",
    "uebertragung",
    "finalisierung",
  ]);

  expect(errors).toEqual([]);
});

test("Zu große Dateien werden nicht übertragen", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 10,
        },
        201,
      ),
    ),
  );
  let uebertragen = 0;
  await page.route(uploadMuster, (route) => {
    uebertragen += 1;
    return route.fulfill(json({}, 201));
  });

  await page.goto(`/lied/?id=${songId}`);
  await page.locator("input[type='file']").setInputFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 viel zu große Notendatei"),
  });
  await expect(page.getByText("Die Datei ist zu groß.")).toBeVisible();
  expect(uebertragen).toBe(0);

  expect(errors).toEqual([]);
});

test("Fehlgeschlagener Abschluss zeigt eine verständliche Meldung", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
        },
        201,
      ),
    ),
  );
  await page.route(uploadMuster, (route) => route.fulfill(json({}, 201)));
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(problem("Die Datei ist kein gültiges PDF.", 422)),
  );

  await page.goto(`/lied/?id=${songId}`);
  await page.locator("input[type='file']").setInputFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 wandern"),
  });
  await expect(
    page.getByText("Die Datei ist kein gültiges PDF."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});
