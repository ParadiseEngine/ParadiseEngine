# Canonical parity corpus

Every file here was written by the C# `CanonicalTomlWriter` (the `.prefab` through
`PrefabDocumentSerializer`) from an in-memory model, and is a FIXED POINT of read → write on
both sides of the cross-language contract: `ParityCorpusTests` re-reads and re-emits each file
and compares bytes; the Blender addon's `paradise_assets/document` suite does the same over a
copy of these files (ParadiseEngine#209, ParadiseBlenderEditor#29).

Regenerate only when the writing spec changes, never by hand: a hand edit that still parses
would make the test pin a form the writer does not produce.

- `floats.toml` — every boundary of the CPython-repr float rule, float32 widening, `inf`/`nan`, integer extremes
- `strings.toml` — every escape, control characters, non-ASCII, key quoting including the empty key
- `structure.toml` — nested tables, arrays of tables with sub-tables, references and null slots in arrays, records in arrays, empty table `{}`, empty table array `[]`
- `prefab.prefab` — root, child with an empty name, an instance with a removed component, an override carrier with `Dropped`, transform from float32, materials with a null slot, a collider list of records
