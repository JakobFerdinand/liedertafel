import assert from "node:assert/strict";
import { test } from "node:test";
import { liedFelder, liedFormularFelder } from "./reducer.ts";

// Die Feldliste ist die eine des Browser (#192): `liedFelder` ist der Vertrag
// über alle camelCase Formnamen des Feldkatalogs im Backend; dieselbe Liste
// hängt dort als Test (`tests/archive/backend/FieldCatalogTests.cs`)
// und als Feldliste der Anfrage-Verträge. Ändert eine Seite ihre Namen,
// fällt die andere auf.

test("liedFelder ist die Feldliste des Feldkatalogs im Backend", () => {
  assert.deepEqual(
    [...liedFelder],
    [
      "title",
      "composer",
      "arranger",
      "lyricist",
      "voiceConfiguration",
      "musicalKey",
      "language",
      "lyrics",
    ],
  );
});

test("das Anlegeformular meidet den Titel, der aus dem Dateinamen kommt", () => {
  assert.deepEqual(
    [...liedFormularFelder],
    [
      "composer",
      "arranger",
      "lyricist",
      "voiceConfiguration",
      "musicalKey",
      "language",
      "lyrics",
    ],
  );
});
