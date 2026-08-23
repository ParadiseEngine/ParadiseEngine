#!/usr/bin/env python3
"""One-shot: rewrite a committed level document from schema v3 to v4.

v3 kept an entity's material-slot overrides in a `Materials` list on the ENTITY, beside its
component list. v4 moves that list onto the `Renderable` component, where the mesh it indexes
against already lives.

Unlike v2 -> v3, this conversion is entirely mechanical: the value moves, nothing is derived and
nothing is looked up. The engine refuses v3 anyway, and that is worth understanding before
reaching for a shim instead. A v3 document does not fail to parse under a v4 reader -- it parses
PERFECTLY. The entity-level `Materials` matches no property, System.Text.Json drops it, and the
scene loads with every entity and every mesh in place and every material override gone. Nothing
reports it. The district simply renders in the GLBs' own colours. A version gate that refuses the
document is the only thing between that file and a silently wrong render.

    python3 tools/migrate_level_v3_to_v4.py <file.json> [<file.json> ...]

Prefer RE-EXPORTING from the editor that wrote the document; this exists for checkouts where that
is inconvenient, and as the thing to diff a fresh export against. When the v2 -> v3 migration was
done, converting with the script and re-exporting produced different results -- and the difference
was a real bug in the editor that no test caught, because every test read the committed JSON
rather than the scene. Do both and compare.

**This script is finished when those documents are converted.** It has no future use -- v3 will
not come back -- so delete it once every checkout is on v4.

Untouched on purpose:

- `LevelData.Materials`, the document-level list of material documents. A different field that
  happens to share a name; it stays where it is.
- `PrefabTemplateData.Materials`, on a prefab template. A template has no component list to hold
  it, so it keeps its own slot list.
"""

from __future__ import annotations

import json
import sys

#: The Renderable component's [Guid], from its record in LevelDocument.cs. Lowercase, hyphenated,
#: no braces -- the only shape System.Text.Json's Guid converter reads.
RENDERABLE = "f2c0357e-94dd-4a5a-9803-518066cb54b2"


def renderable_of(entity: dict) -> dict | None:
    """The entity's Renderable payload, or None. Matched by id, falling back to the CLR type name
    exactly as the engine's own reader does -- a document whose ids were regenerated still has a
    readable `Type`, and that is the case the fallback exists for."""
    for component in entity.get("Components") or []:
        if not isinstance(component, dict):
            continue
        if str(component.get("Id", "")).lower() == RENDERABLE:
            return component
        if component.get("Type") == "Paradise.Export.Data.RenderableComponentData":
            return component
    return None


def convert_entity(entity: dict, path: str) -> bool:
    """Move one entity's slots onto its Renderable. True when it had any to move."""
    slots = entity.pop("Materials", None)
    if not slots:
        # Nothing to carry. The key is still dropped if it was an empty list: v4 has no such
        # property, and leaving it would make the document fail a strict re-read for no reason.
        return False

    component = renderable_of(entity)
    if component is None:
        # Slots with nothing to index them against. This cannot be converted -- there is no
        # component to move them onto -- and dropping them would lose authored looks in silence,
        # which is the one outcome worth refusing over. No document in the workspace hits this;
        # the check is here because the day one does is the day it matters.
        raise SystemExit(
            f"REFUSED: {path}: entity {entity.get('Id')!r} has Materials but no Renderable "
            f"component. Nothing to move them onto -- fix the export, or delete the slots."
        )

    data = component.setdefault("Data", {})
    if not isinstance(data, dict):
        raise SystemExit(
            f"REFUSED: {path}: entity {entity.get('Id')!r} has a Renderable payload that is not "
            f"an object ({type(data).__name__})."
        )

    # After Mesh/MeshNode, matching the C# property order. System.Text.Json writes properties in
    # declaration order, so this keeps a converted document diffable against a fresh export.
    data["Materials"] = slots
    return True


def convert(path: str) -> None:
    with open(path, encoding="utf-8") as file:
        document = json.load(file)

    version = document.get("SchemaVersion")
    if version == 4:
        print(f"  {path}: already v4, skipped")
        return
    if version != 3:
        raise SystemExit(f"REFUSED: {path} is schema version {version}, not 3")

    entities = [e for e in (document.get("Entities") or []) if isinstance(e, dict)]
    moved = sum(convert_entity(entity, path) for entity in entities)
    document["SchemaVersion"] = 4

    with open(path, "w", encoding="utf-8") as file:
        json.dump(document, file, indent=2)
        file.write("\n")
    print(f"  {path}: v3 -> v4, {len(entities)} entities, {moved} with material slots")


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    for path in argv[1:]:
        convert(path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
