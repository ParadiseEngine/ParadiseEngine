# Canonical parity corpus

The C# `CanonicalTomlWriter` generated this corpus from in-memory models (`.prefab` uses
`PrefabDocumentSerializer`). `ParityCorpusTests` requires byte-identical read → write output.
The Blender addon's `tests/unit/test_parity_corpus.py` applies the same check to its copy at
`ParadiseBlenderEditor/tests/fixtures/parity/` (ParadiseEngine#209, ParadiseBlenderEditor#29).
Both writers must reproduce the same bytes, so either writer diverging fails parity.

Regenerate only when the writing spec changes; never hand-edit files into forms the writer
does not emit. Update the addon's copy in the same change, keeping both copies byte-identical.

- `floats.toml` — every boundary of the CPython-repr float rule, float32 widening, `inf`/`nan`, integer extremes
- `strings.toml` — every escape, control characters, non-ASCII, key quoting including the empty key
- `structure.toml` — nested tables, arrays of tables with sub-tables, references and null slots in arrays, records in arrays, empty table `{}`, empty table array `[]`
- `prefab.prefab` — root, child with an empty name, an instance with a removed component, an override carrier with `Dropped`, transform from float32, materials with a null slot, a collider list of records
