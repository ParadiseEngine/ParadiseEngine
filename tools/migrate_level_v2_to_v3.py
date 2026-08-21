#!/usr/bin/env python3
"""One-shot: rewrite a committed level document from schema v2 to v3.

v2 gave an entity nine NAMED component slots plus a `Custom` list; v3 gives it one list of
`{Id, Type, Data}`. The engine deliberately has no compat path for this — a named slot cannot be
mapped back to the id it came from without the very table v3 deleted — so a v2 document is refused
on read and must be regenerated.

Regenerating normally means re-exporting from the editor that wrote it. This script exists for the
documents whose editor cannot be driven from a terminal: the Godot editor's committed scenes need
Godot itself, and its export is a GUI action. Everything the conversion needs is knowable here
(the table below is the one the engine dropped), and the result is verified by reading it back
through the engine's own reader.

    python3 tools/migrate_level_v2_to_v3.py <file.json> [<file.json> ...]

**This script is finished when those documents are converted.** It has no future use — v2 will not
come back — so delete it once every checkout is on v3.

Untouched on purpose: an entity's `Custom` entries. They already ARE `{Id, Type, Data}`; that is
the tier v3 kept and generalized. They are appended after the slots, so a game's components keep
their relative order.
"""

from __future__ import annotations

import json
import sys

#: Slot name -> (component [Guid], fully qualified CLR name). Transcribed from
#: ParadiseComponentIds.cs and the records in LevelDocument.cs. Ordered as the contract declared
#: the slots, so a converted document lists components in the order `Materialize` used to return
#: them and two runs cannot disagree.
SLOTS: list[tuple[str, str, str]] = [
    ("Renderable", "f2c0357e-94dd-4a5a-9803-518066cb54b2", "Paradise.Export.Data.RenderableComponentData"),
    ("Collider", "e1cd1bc8-86f2-4225-adc9-4a324c70ebf9", "Paradise.Export.Data.ColliderComponentData"),
    ("Rigidbody", "b7ab4dd8-c8da-4dc2-9e5e-192fd74deb11", "Paradise.Export.Data.RigidbodyComponentData"),
    ("Interactable", "0283ee5f-775b-412b-a91c-03ecd9b61165", "Paradise.Export.Data.EntityInteractableComponentData"),
    ("Agent", "5801915b-3d0c-4940-8970-7d1487b991cf", "Paradise.Export.Data.AgentComponentData"),
    ("SpriteAnimation", "d3e53cd4-89c6-4ca8-851e-7596da889c68", "Paradise.Export.Data.SpriteAnimationComponentData"),
    ("ParticleEmitter", "1b4d1bdd-dea1-4b86-9b6a-879c46346b9e", "Paradise.Export.Data.ParticleEmitterComponentData"),
    ("AudioEmitter", "e6ec7f42-df09-4ec9-af06-128ddf3eda8e", "Paradise.Export.Data.AudioEmitterComponentData"),
    ("Light", "fc886b84-c48c-4415-afd9-b03d6faf5ab7", "Paradise.Export.Data.SceneLightData"),
]

KNOWN_KEYS = {name for name, _, _ in SLOTS} | {"Custom"}


def convert_entity(entity: dict) -> int:
    """Rewrite one entity's Components in place. Returns how many entries it ended up with."""
    components = entity.get("Components")
    if components is None:
        entity["Components"] = []
        return 0
    if isinstance(components, list):
        return len(components)  # already v3

    unknown = set(components) - KNOWN_KEYS
    if unknown:
        # A key this table does not know is a slot added after the table was written. Converting
        # around it would drop authored data silently, which is the one outcome worth refusing.
        raise SystemExit(f"REFUSED: unknown component slot(s) {sorted(unknown)}")

    entries = []
    for name, guid, clr in SLOTS:
        payload = components.get(name)
        if payload is None:
            continue  # a null slot said "no such component"; absence says it now
        entries.append({"Id": guid, "Type": clr, "Data": payload})
    entries.extend(components.get("Custom") or [])

    entity["Components"] = entries
    return len(entries)


def convert(path: str) -> None:
    with open(path, encoding="utf-8") as file:
        document = json.load(file)

    version = document.get("SchemaVersion")
    if version == 3:
        print(f"  {path}: already v3, skipped")
        return
    if version != 2:
        raise SystemExit(f"REFUSED: {path} is schema version {version}, not 2")

    total = sum(convert_entity(entity) for entity in document.get("Entities") or [])
    document["SchemaVersion"] = 3

    with open(path, "w", encoding="utf-8") as file:
        json.dump(document, file, indent=2)
        file.write("\n")
    print(f"  {path}: v2 -> v3, {len(document.get('Entities') or [])} entities, {total} components")


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__)
        return 2
    for path in argv[1:]:
        convert(path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
