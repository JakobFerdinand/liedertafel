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

// Öffnet den Werkbank-Aufklapper (er ist zu, bis die Redaktion ihn braucht).
async function materialVerwaltungAufklappen(page: Page) {
  await page.locator("details.material-verwaltung > summary").click();
  await expect(page.locator("details.material-verwaltung")).toHaveAttribute(
    "open",
    "",
  );
}

const viewUrl = "https://speicher.test/ansicht?sig=ansicht-1";
const downloadUrl = "https://speicher.test/laden?sig=laden-1";
const uploadUrl = "https://speicher.test/ubertragung?sig=upload-1";
const uploadMuster = /speicher\.test\/ubertragung/;

function gatedBlock() {
  let loslassen: () => void = () => {};
  const complete = new Promise<void>((aufloesen) => {
    loslassen = aufloesen;
  });
  return { complete, loslassen };
}

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
  const rahmen = page.locator("iframe[title^='Noten (PDF)']");
  await expect(rahmen).toBeVisible();
  await expect(rahmen).toHaveAttribute("src", viewUrl);
  const laden = page.getByRole("link", { name: "Herunterladen" });
  await expect(laden).toBeVisible();
  await expect(laden).toHaveAttribute("href", downloadUrl);
  const vollbild = page.getByRole("button", { name: "Vollbild" });
  await expect(vollbild).toBeVisible();
  // Kein echter Vollbildwechsel im Test; der Klick darf nur ohne
  // Seitenfehler durchlaufen.
  await vollbild.click();
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
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: 16,
      fileName: "noten.pdf",
    });
    return route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    );
  });
  await page.route(uploadMuster, (route) => {
    const anfrage = route.request();
    schritte.push("uebertragung");
    expect(anfrage.method()).toBe("PUT");
    if (anfrage.url().includes("comp=blocklist")) {
      expect(anfrage.headers()["content-type"]).toBe("application/xml");
      expect(anfrage.postData()).toBe(
        "<BlockList><Latest>MDAwMDAw</Latest></BlockList>",
      );
    } else {
      expect(anfrage.headers()["content-type"]).toBe("application/pdf");
    }
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    schritte.push("finalisierung");
    expect(route.request().method()).toBe("POST");
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: 16,
      fileName: "noten.pdf",
    });
    notenAssets = [asset];
    return route.fulfill(json({ assetId, ...revision }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  await expect(
    page.getByRole("button", { name: "Material hochladen" }),
  ).toBeVisible();

  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 wandern"),
  });

  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();
  await expect(
    page.getByText("Noten · Fassung 1 · 0,4 MB · PDF"),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Material hochladen" }),
  ).toBeVisible();
  expect(schritte).toEqual([
    "asset",
    "sitzung",
    "uebertragung",
    "uebertragung",
    "finalisierung",
  ]);

  expect(errors).toEqual([]);
});

test("Noten werden blockweise mit Fortschritt übertragen", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const inhalt = "%PDF-1.4 blockweise1";
  const blockGroesse = 8;
  const neueAssetId = "00000000-0000-0000-0000-00000000e00b";
  const sitzungsId = "00000000-0000-0000-0000-0000000000aa";
  const blockIds: string[] = [];
  const blockInhalte: string[] = [];
  let blocklistenInhalt = "";
  const awaitBlock = [null, gatedBlock(), gatedBlock()];

  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([]))),
  );
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) =>
    route.fulfill(
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
    ),
  );
  await page.route(`**/api/assets/${neueAssetId}/upload-session`, (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: inhalt.length,
      fileName: "noten.pdf",
    });
    return route.fulfill(
      json(
        {
          uploadSessionId: sitzungsId,
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: blockGroesse,
        },
        201,
      ),
    );
  });
  await page.route(uploadMuster, async (route) => {
    const anfrage = route.request();
    if (anfrage.url().includes("comp=blocklist")) {
      expect(anfrage.method()).toBe("PUT");
      expect(anfrage.headers()["content-type"]).toBe("application/xml");
      blocklistenInhalt = anfrage.postData() ?? "";
      return route.fulfill(json({}, 201));
    }
    expect(anfrage.method()).toBe("PUT");
    expect(anfrage.url()).toContain("comp=block&blockid=");
    const blockId = new URL(anfrage.url()).searchParams.get("blockid") ?? "";
    const index = Number(atob(blockId));
    blockIds[index] = blockId;
    blockInhalte[index] = anfrage.postData() ?? "";
    await awaitBlock[index]?.complete;
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    expect(route.request().method()).toBe("POST");
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: inhalt.length,
      fileName: "noten.pdf",
    });
    return route.fulfill(json({ assetId: neueAssetId, ...revision }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from(inhalt),
  });

  await page.getByRole("button", { name: "Material übertragen" }).click();
  const zeile = page.locator(".material-datei");
  await expect(zeile.getByText("wird übertragen … 40 %")).toBeVisible();
  awaitBlock[1]?.loslassen();
  await expect(zeile.getByText("wird übertragen … 80 %")).toBeVisible();
  awaitBlock[2]?.loslassen();
  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();

  expect(blockIds).toEqual(["MDAwMDAw", "MDAwMDAx", "MDAwMDAy"]);
  expect(blockInhalte).toEqual([
    inhalt.slice(0, 8),
    inhalt.slice(8, 16),
    inhalt.slice(16, 24),
  ]);
  expect(blocklistenInhalt).toBe(
    "<BlockList><Latest>MDAwMDAw</Latest><Latest>MDAwMDAx</Latest><Latest>MDAwMDAy</Latest></BlockList>",
  );

  expect(errors).toEqual([]);
});

test("Zu große Dateien werden nicht übertragen", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );
  await page.route(`**/api/assets/${assetId}`, (route) =>
    route.fulfill(json({ ...asset, currentRevision: null })),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 10,
          blockBytes: 5242880,
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
  await materialVerwaltungAufklappen(page);
  await page.locator("input[type='file']").setInputFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 viel zu große Notendatei"),
  });
  await page.getByRole("button", { name: "Material übertragen" }).click();
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
  await page.route(`**/api/assets/${assetId}`, (route) =>
    route.fulfill(json({ ...asset, currentRevision: null })),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
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
  await materialVerwaltungAufklappen(page);
  await page.locator("input[type='file']").setInputFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 wandern"),
  });
  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(
    page.getByText("Die Datei ist kein gültiges PDF."),
  ).toBeVisible();

  expect(errors).toEqual([]);
});

test("Unterbrochener Upload wird nach dem Neuladen fortgesetzt", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const inhalt = "%PDF-1.4 blockweise1";
  const sitzungsId = "00000000-0000-0000-0000-0000000000aa";
  const schluessel = `arc-upload-${assetId}`;
  let phase = 1;
  let erlaubt = false;
  const blockPuts: { phase: number; index: number }[] = [];
  let blocklistenAbfragen = 0;
  let verbindungen = 0;
  let verbindungsXml = "";
  let verlaengerungen = 0;
  let finalisierungen = 0;

  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );
  await page.route(`**/api/assets/${assetId}`, (route) =>
    route.fulfill(json({ ...asset, currentRevision: null })),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: sitzungsId,
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 8,
        },
        201,
      ),
    ),
  );
  await page.route(uploadMuster, async (route) => {
    const anfrage = route.request();
    if (anfrage.method() === "GET") {
      blocklistenAbfragen += 1;
      return route.fulfill({
        status: 200,
        contentType: "application/xml",
        body: `<BlockList><CommittedBlocks><Block><Name>MDAwMDAw</Name><Size>8</Size></Block></CommittedBlocks><UncommittedBlocks /></BlockList>`,
      });
    }
    if (anfrage.url().includes("comp=blocklist")) {
      verbindungen += 1;
      verbindungsXml = anfrage.postData() ?? "";
      return route.fulfill(json({}, 201));
    }
    const index = Number(
      atob(new URL(anfrage.url()).searchParams.get("blockid") ?? ""),
    );
    blockPuts.push({ phase, index });
    if (index !== 0 && !erlaubt) {
      return route.fulfill(problem("Übertragung unterbrochen.", 500));
    }
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/renew", (route) => {
    verlaengerungen += 1;
    return route.fulfill(
      json({
        uploadSessionId: sitzungsId,
        blobName: null,
        uploadUrl,
        expiresAt: "2026-09-19T12:30:00.000Z",
        maxBytes: 5242880,
        blockBytes: 8,
      }),
    );
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    finalisierungen += 1;
    expect(route.request().postDataJSON()).toEqual({
      sizeBytes: inhalt.length,
      fileName: "noten.pdf",
    });
    return route.fulfill(json({ assetId, ...revision }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from(inhalt),
  });
  await page.getByRole("button", { name: "Material übertragen" }).click();
  const zeile = page.locator(".material-datei");
  await expect(zeile.getByText("gescheitert")).toBeVisible();
  expect(blockPuts).toEqual([
    { phase: 1, index: 0 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
  ]);
  const eintrag = JSON.parse(
    (await page.evaluate((k) => window.localStorage.getItem(k), schluessel)) ??
      "null",
  ) as {
    uploadSessionId: string;
    fileName: string;
    sizeBytes: number;
    lastModified: number;
    blockBytes: number;
  };
  expect(eintrag).toEqual({
    uploadSessionId: sitzungsId,
    fileName: "noten.pdf",
    sizeBytes: inhalt.length,
    lastModified: eintrag.lastModified,
    blockBytes: 8,
  });
  expect(eintrag.lastModified).toBeGreaterThan(0);

  phase = 2;
  erlaubt = true;
  await page.reload();
  await materialVerwaltungAufklappen(page);
  await expect(page.getByRole("button", { name: "Fortsetzen" })).toBeVisible();
  await page.getByRole("button", { name: "Fortsetzen" }).click();
  await page.evaluate(
    ({ name, inhalt, lastModified }) => {
      const datei = new File([inhalt], name, {
        type: "application/pdf",
        lastModified,
      });
      const eingabe =
        document.querySelector<HTMLInputElement>("input[type='file']");
      if (!eingabe) throw new Error("Eingabe fehlt");
      const transport = new DataTransfer();
      transport.items.add(datei);
      eingabe.files = transport.files;
      eingabe.dispatchEvent(new Event("change", { bubbles: true }));
    },
    { name: "noten.pdf", inhalt, lastModified: eintrag.lastModified },
  );

  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();
  expect(verlaengerungen).toBe(1);
  expect(blocklistenAbfragen).toBe(1);
  expect(blockPuts).toEqual([
    { phase: 1, index: 0 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
    { phase: 1, index: 1 },
    { phase: 2, index: 1 },
    { phase: 2, index: 2 },
  ]);
  expect(verbindungen).toBe(1);
  expect(verbindungsXml).toBe(
    "<BlockList><Latest>MDAwMDAw</Latest><Latest>MDAwMDAx</Latest><Latest>MDAwMDAy</Latest></BlockList>",
  );
  expect(finalisierungen).toBe(1);
  expect(
    await page.evaluate((k) => window.localStorage.getItem(k), schluessel),
  ).toBeNull();

  expect(errors).toEqual([]);
});

test("Abweichende Datei beim Fortsetzen startet eine frische Übertragung", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const inhalt = "%PDF-1.4 blockweise1";
  const abweichend = "%PDF-1.4 anders";
  const schluessel = `arc-upload-${assetId}`;
  let erlaubt = false;
  const blockPuts: number[] = [];
  let verlaengerungen = 0;
  let abrechnungen = 0;
  let frischeSitzungen = 0;

  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );
  await page.route(`**/api/assets/${assetId}`, (route) =>
    route.fulfill(json({ ...asset, currentRevision: null })),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) => {
    frischeSitzungen += 1;
    return route.fulfill(
      json(
        {
          uploadSessionId: `sitzung-${frischeSitzungen}`,
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 8,
        },
        201,
      ),
    );
  });
  await page.route(uploadMuster, async (route) => {
    const anfrage = route.request();
    if (anfrage.method() === "GET") {
      return route.fulfill({
        status: 200,
        contentType: "application/xml",
        body: "<BlockList />",
      });
    }
    if (anfrage.url().includes("comp=blocklist")) {
      return route.fulfill(json({}, 201));
    }
    const index = Number(
      atob(new URL(anfrage.url()).searchParams.get("blockid") ?? ""),
    );
    blockPuts.push(index);
    if (index !== 0 && !erlaubt) {
      return route.fulfill(problem("Übertragung unterbrochen.", 500));
    }
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*/renew", (route) => {
    verlaengerungen += 1;
    return route.fulfill(json({}, 200));
  });
  await page.route("**/api/upload-sessions/*", (route) => {
    expect(route.request().method()).toBe("DELETE");
    abrechnungen += 1;
    return route.fulfill(json({}, 204));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(json({ assetId, ...revision })),
  );

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from(inhalt),
  });
  await page.getByRole("button", { name: "Material übertragen" }).click();
  const zeile = page.locator(".material-datei");
  await expect(zeile.getByText("gescheitert")).toBeVisible();
  const eintrag = JSON.parse(
    (await page.evaluate((k) => window.localStorage.getItem(k), schluessel)) ??
      "null",
  ) as { lastModified: number };
  expect(eintrag).not.toBeNull();

  erlaubt = true;
  await page.reload();
  await materialVerwaltungAufklappen(page);
  await expect(page.getByRole("button", { name: "Fortsetzen" })).toBeVisible();
  await page.getByRole("button", { name: "Fortsetzen" }).click();
  await page.evaluate(
    ({ name, inhalt, lastModified }) => {
      const datei = new File([inhalt], name, {
        type: "application/pdf",
        lastModified,
      });
      const eingabe =
        document.querySelector<HTMLInputElement>("input[type='file']");
      if (!eingabe) throw new Error("Eingabe fehlt");
      const transport = new DataTransfer();
      transport.items.add(datei);
      eingabe.files = transport.files;
      eingabe.dispatchEvent(new Event("change", { bubbles: true }));
    },
    {
      name: "noten.pdf",
      inhalt: abweichend,
      lastModified: eintrag.lastModified,
    },
  );

  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();
  expect(verlaengerungen).toBe(0);
  expect(abrechnungen).toBe(1);
  expect(frischeSitzungen).toBe(2);
  expect(blockPuts.filter((index) => index === 0)).toHaveLength(2);
  expect(
    await page.evaluate((k) => window.localStorage.getItem(k), schluessel),
  ).toBeNull();

  expect(errors).toEqual([]);
});

test("Abbrechen meldet die Uploadsitzung ab und bereinigt den Eintrag", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  const inhalt = "%PDF-1.4 blockweise1";
  const sitzungsId = "00000000-0000-0000-0000-0000000000aa";
  const schluessel = `arc-upload-${assetId}`;
  const awarten = gatedBlock();
  const blockPuts: number[] = [];
  let abrechnungen = 0;
  let finalisierungen = 0;

  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([leeresAsset]))),
  );
  await page.route(`**/api/assets/${assetId}`, (route) =>
    route.fulfill(json({ ...asset, currentRevision: null })),
  );
  await page.route(`**/api/assets/${assetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: sitzungsId,
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 8,
        },
        201,
      ),
    ),
  );
  await page.route(uploadMuster, async (route) => {
    const anfrage = route.request();
    if (anfrage.url().includes("comp=blocklist")) {
      return route.fulfill(json({}, 201));
    }
    const index = Number(
      atob(new URL(anfrage.url()).searchParams.get("blockid") ?? ""),
    );
    blockPuts.push(index);
    if (index === 1) {
      await awarten.complete;
    }
    return route.fulfill(json({}, 201));
  });
  await page.route("**/api/upload-sessions/*", (route) => {
    expect(route.request().method()).toBe("DELETE");
    abrechnungen += 1;
    return route.fulfill(json({}, 204));
  });
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    finalisierungen += 1;
    return route.fulfill(json({ assetId, ...revision }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  const chooser = await wahl;
  await chooser.setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from(inhalt),
  });
  await page.getByRole("button", { name: "Material übertragen" }).click();
  const zeile = page.locator(".material-datei");
  await expect(zeile.getByText("wird übertragen … 40 %")).toBeVisible();
  expect(blockPuts).toEqual([0, 1]);

  await zeile.getByRole("button", { name: "Abbrechen" }).click();
  await expect(zeile.getByText("abgebrochen")).toBeVisible();
  // Die Abmeldung läuft nebenläufig zum Statuswechsel: erst warten.
  await expect.poll(() => abrechnungen).toBe(1);

  await awarten.loslassen();
  await expect(page.getByText("0 von 1 Dateien gespeichert.")).toBeVisible();
  expect(finalisierungen).toBe(0);
  expect(
    await page.evaluate((k) => window.localStorage.getItem(k), schluessel),
  ).toBeNull();

  expect(errors).toEqual([]);
});

test("Parallele Uploads teilen sich ein Sicherheitstoken-Paar", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  // Jeder Abruf rotiert das Cookie; parallel hochgeladene Dateien dürfen das
  // Paar also nur einmal laden und müssen denselben Token verwenden.
  let antiforgeryAbrufe = 0;
  await page.route("**/api/antiforgery", (route) => {
    antiforgeryAbrufe += 1;
    return route.fulfill(json({ token: "test" }));
  });

  const tokens: string[] = [];
  const pdfId = "00000000-0000-0000-0000-00000000e10a";
  const audioId = "00000000-0000-0000-0000-00000000e10b";
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail([]))),
  );
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) => {
    tokens.push(route.request().headers()["x-csrf-token"]);
    const assetType = route.request().postDataJSON().assetType;
    return route.fulfill(
      json(
        {
          id: assetType === "score" ? pdfId : audioId,
          musicalVersionId: versionId,
          assetType,
          voiceLabel: null,
          createdAt: "2026-09-19T12:00:00.000Z",
          currentRevision: null,
        },
        201,
      ),
    );
  });
  await page.route("**/api/assets/*/upload-session", (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    ),
  );
  await page.route(uploadMuster, (route) => route.fulfill(json({}, 201)));
  await page.route("**/api/upload-sessions/*/finalize", (route) =>
    route.fulfill(json({ assetId, ...revision })),
  );

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  await (await wahl).setFiles([
    {
      name: "noten.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4 zwei"),
    },
    {
      name: "lied.mp3",
      mimeType: "audio/mpeg",
      buffer: Buffer.from("ID3 parallel"),
    },
  ]);
  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("2 von 2 Dateien gespeichert.")).toBeVisible();

  expect(antiforgeryAbrufe).toBe(1);
  expect(tokens).toEqual(["test", "test"]);
  expect(errors).toEqual([]);
});

test("Abgelehnter Sicherheitstoken wird ersetzt und die Anfrage wiederholt", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await mockSitzung(page, editorMe);

  let tokenAbrufe = 0;
  await page.route("**/api/antiforgery", (route) => {
    tokenAbrufe += 1;
    return route.fulfill(json({ token: tokenAbrufe === 1 ? "alt" : "neu" }));
  });

  const tokens: string[] = [];
  let assetVersuche = 0;
  const neueAssetId = "00000000-0000-0000-0000-00000000e00b";
  let notenAssets: unknown[] = [];
  await page.route(`**/api/songs/${songId}`, (route) =>
    route.fulfill(json(detail(notenAssets))),
  );
  await page.route(`**/api/musical-versions/${versionId}/assets`, (route) => {
    tokens.push(route.request().headers()["x-csrf-token"]);
    assetVersuche += 1;
    if (assetVersuche === 1) {
      return route.fulfill(problem("Ungültiger Sicherheitstoken.", 400));
    }
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
  await page.route(`**/api/assets/${neueAssetId}/upload-session`, (route) =>
    route.fulfill(
      json(
        {
          uploadSessionId: "00000000-0000-0000-0000-0000000000aa",
          blobName: null,
          uploadUrl,
          expiresAt: "2026-09-19T12:15:00.000Z",
          maxBytes: 5242880,
          blockBytes: 5242880,
        },
        201,
      ),
    ),
  );
  await page.route(uploadMuster, (route) => route.fulfill(json({}, 201)));
  await page.route("**/api/upload-sessions/*/finalize", (route) => {
    notenAssets = [asset];
    return route.fulfill(json({ assetId, ...revision }));
  });

  await page.goto(`/lied/?id=${songId}`);
  await materialVerwaltungAufklappen(page);
  const wahl = page.waitForEvent("filechooser");
  await page.getByRole("button", { name: "Material hochladen" }).click();
  await (await wahl).setFiles({
    name: "noten.pdf",
    mimeType: "application/pdf",
    buffer: Buffer.from("%PDF-1.4 token"),
  });
  await page.getByRole("button", { name: "Material übertragen" }).click();
  await expect(page.getByText("1 von 1 Dateien gespeichert.")).toBeVisible();

  expect(tokens).toEqual(["alt", "neu"]);
  expect(tokenAbrufe).toBe(2);
  expect(errors).toEqual([]);
});
