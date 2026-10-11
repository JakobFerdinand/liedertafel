import { expect, test } from "@playwright/test";
import { a2uiKomponentenNamen, leseA2uiOberflaechen } from "../lib/a2ui";
import { katalogId, oberflaeche } from "./chat-mock";

// ARC-022-4: was der Browser aus A2UI-Nachrichten überhaupt gelten lässt. Das
// ist reine Logik ohne Seite; die Anzeige prüft a2ui.spec.ts.

const liedId = "00000000-0000-0000-0000-0000000002a1";
const liedKarte = { component: "LiedKarte", liedId, titel: "Lied" };

test("Der Katalog hat genau die sechs Komponenten des Archivs", () => {
  expect([...a2uiKomponentenNamen].sort()).toEqual(
    [
      "AenderungsVorschau",
      "AuftrittKarte",
      "LiedKarte",
      "ProgrammListe",
      "Quellen",
      "VorschlagKarte",
    ].sort(),
  );
});

test("Eine vollständige Oberfläche wird gelesen", () => {
  const gelesen = leseA2uiOberflaechen(oberflaeche(liedKarte));
  expect(gelesen).toHaveLength(1);
  expect(gelesen[0].surfaceId).toBe("s1");
});

const ungueltig: Array<[string, unknown]> = [
  ["kein Array", { version: "v0.9" }],
  ["null und Zahlen", [null, 1, "x", []]],
  [
    "Basis-Katalog-Komponente Text",
    oberflaeche({ component: "Text", text: "x" }),
  ],
  [
    "Basis-Katalog-Komponente Button",
    oberflaeche({ component: "Button", child: "t" }),
  ],
  ["Komponente ohne Namen", oberflaeche({ liedId, titel: "Lied" })],
  [
    "zusätzliche Eigenschaft href",
    oberflaeche({ ...liedKarte, href: "https://x.example" }),
  ],
  [
    "zusätzliche Eigenschaft url",
    oberflaeche({ ...liedKarte, url: "https://x.example" }),
  ],
  [
    "ID keine UUID",
    oberflaeche({ ...liedKarte, liedId: "https://x.example/lied" }),
  ],
  ["leerer Titel", oberflaeche({ ...liedKarte, titel: "" })],
  ["Titel zu lang", oberflaeche({ ...liedKarte, titel: "x".repeat(201) })],
  ["Quellen leer", oberflaeche({ component: "Quellen", quellen: [] })],
  [
    "Quellen mit Adresse",
    oberflaeche({
      component: "Quellen",
      quellen: [{ liedId, titel: "Lied", url: "https://x.example" }],
    }),
  ],
  [
    "Wurzel nicht root",
    [
      {
        version: "v0.9",
        createSurface: { surfaceId: "s1", catalogId: katalogId },
      },
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "karte", ...liedKarte }],
        },
      },
    ],
  ],
  [
    "zwei Komponenten",
    [
      {
        version: "v0.9",
        createSurface: { surfaceId: "s1", catalogId: katalogId },
      },
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [
            { id: "root", ...liedKarte },
            { id: "zweite", ...liedKarte },
          ],
        },
      },
    ],
  ],
  [
    "fremder Katalog",
    [
      {
        version: "v0.9",
        createSurface: { surfaceId: "s1", catalogId: "https://a2ui.org/basic" },
      },
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "root", ...liedKarte }],
        },
      },
    ],
  ],
  [
    "falsche Version",
    [
      {
        version: "v0.8",
        createSurface: { surfaceId: "s1", catalogId: katalogId },
      },
      {
        version: "v0.8",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "root", ...liedKarte }],
        },
      },
    ],
  ],
  [
    "Aktualisierung ohne Erstellung",
    [
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "root", ...liedKarte }],
        },
      },
    ],
  ],
  [
    "Aktualisierung vor Erstellung",
    [
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "root", ...liedKarte }],
        },
      },
      {
        version: "v0.9",
        createSurface: { surfaceId: "s1", catalogId: katalogId },
      },
    ],
  ],
  [
    "Erstellung ohne Komponente",
    [
      {
        version: "v0.9",
        createSurface: { surfaceId: "s1", catalogId: katalogId },
      },
    ],
  ],
  [
    "Datenmodell statt Komponente",
    [
      {
        version: "v0.9",
        createSurface: { surfaceId: "s1", catalogId: katalogId },
      },
      {
        version: "v0.9",
        updateDataModel: { surfaceId: "s1", path: "/", value: { a: 1 } },
      },
    ],
  ],
  [
    "Oberflächen-ID außerhalb des Musters",
    oberflaeche(liedKarte, "../../boese"),
  ],
  [
    "Erstellung mit Zusatzfeldern (Theme, Datenmodell)",
    [
      {
        version: "v0.9",
        createSurface: {
          surfaceId: "s1",
          catalogId: katalogId,
          theme: { primaryColor: "#ff0000" },
          sendDataModel: true,
        },
      },
      {
        version: "v0.9",
        updateComponents: {
          surfaceId: "s1",
          components: [{ id: "root", ...liedKarte }],
        },
      },
    ],
  ],
];

for (const [fall, nachrichten] of ungueltig) {
  test(`Ungültig und verworfen: ${fall}`, () => {
    expect(leseA2uiOberflaechen(nachrichten)).toEqual([]);
  });
}

test("Zwei Erstellungen derselben Oberfläche machen sie unbrauchbar", () => {
  const nachrichten = [...oberflaeche(liedKarte), ...oberflaeche(liedKarte)];
  expect(leseA2uiOberflaechen(nachrichten)).toEqual([]);
});

test("Gültige Oberflächen bleiben, wenn andere ungültig sind", () => {
  const nachrichten = [
    ...oberflaeche({ component: "Button" }, "s1"),
    ...oberflaeche(liedKarte, "s2"),
    ...oberflaeche({ ...liedKarte, href: "https://x.example" }, "s3"),
  ];
  const gelesen = leseA2uiOberflaechen(nachrichten);
  expect(gelesen.map((o) => o.surfaceId)).toEqual(["s2"]);
});

test("Höchstens vier Oberflächen werden gelesen", () => {
  const nachrichten = ["s1", "s2", "s3", "s4"].flatMap((id) =>
    oberflaeche(liedKarte, id),
  );
  nachrichten.push(...oberflaeche(liedKarte, "s5"));
  expect(leseA2uiOberflaechen(nachrichten).map((o) => o.surfaceId)).toEqual([
    "s1",
    "s2",
    "s3",
    "s4",
  ]);
});
