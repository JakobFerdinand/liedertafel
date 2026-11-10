# Historical EF cases: frozen-head equivalents

Authority: maintainer decision [#218 comment 6760615509](https://github.com/JakobFerdinand/liedertafel/issues/218#issuecomment-6760615509),
2026-11-10. Rust starts at the verified frozen EF head. Historical transforms
below are **NOT executed by Rust**. These equivalents are deliberately not
reported as translated historical migration passes. Current SQL, constraints,
feature tests, schema parity and runtime grants remain required.

| Frozen file / historical assertion | Disposition and current equivalent |
| --- | --- |
| `AuftrittProgrammMigrationPostgresTests.cs::TheLatestRevisionBecomesTheProgrammeAndMarksFollowTheirEntries` — latest working revision wins over published revision; old revisions/confirmation/evidence removed; only mappable Zeitmarke survives and points to stable entry | Historical selection/deletion/remapping **NOT executed**. `historical_baseline` adopts independently restored frozen-head DDL/history with seeded current programme/items/marks, preserving all IDs, positions, notes, attribution, byte metadata/blob references/slots and identities across native `--migrate` and rerun. It asserts obsolete tables and publication columns absent and rejects incomplete pre-programme EF history without modifying seeded data. `auftritt_programm` and `lied_historie` exercise current HTTP reorder/guard/projections; SQL uniqueness/FK cases remain real PostgreSQL gates. |
| `OldModelContractPostgresTests.cs::TheDropKeepsAccountsAndTheNewModelAndRemovesTheOldOne` — obsolete catalogue chain/data removed while new-model rows/accounts/roles/passkey survive | Historical destructive drop/filter **NOT executed**. `historical_baseline` preserves current accounts/roles/membership/passkey, Lied/Datei/upload/event/programme/recording/mark rows during head adoption/rerun, asserts each obsolete table/column absent and safe older-history rejection. Current owner-check constraint is exercised directly. No assertion about selecting/deleting old-model rows is claimed. |
| `ReadingPostgresLiveTests.cs` historical migration over existing extraction rows | **#229** owns explicit equivalent: head adoption/preservation plus safe old-history rejection. Current reading SQL, budget sweep, ledger and constraints are not deferred by this disposition. |
| `ChatA2uiPostgresLiveTests.cs` historical migration with existing chat rows | **#232** owns explicit equivalent: head adoption/preservation plus safe old-history rejection. Current jsonb round-trip/resolvers/load-time authorization remain mandatory. |
| `RuntimeGrantsTests.cs` | No historical waiver: inherited `migrate_process::postgres_migration_contract` runs independent frozen-schema comparison and actual role/grant contracts. Canonical baseline/grants stay unchanged. |

Before changing adoption or these tests, read
`crates/archive-db/baseline/README.md`. A head fixture proves preservation of
already-transformed rows, not that the old EF transformation ran. Rejection
fixtures deliberately have older history; they do not add obsolete upgrade support.
Independent negative schema sentinels (`songs`, `programme_revisions`) also reject
with complete head history and retain their synthetic row. They prove fail-closed
schema handling, not a reconstructed full obsolete EF database or an upgrade.
