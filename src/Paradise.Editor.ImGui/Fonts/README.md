# Editor fonts

Two committed TrueType files, both embedded into `Paradise.Editor.ImGui` and merged into one
ImGui font at startup: **Inter** for text, **Material Symbols Rounded** for icons.

Both are **glyf-outline TrueType, not CFF**. That is a hard requirement, not a preference:
Hexa's cimgui build rasterizes through stb_truetype, which asserts on CFF outlines — so an `.otf`
here would fail at load with no useful message. `UiFonts.IsStbLoadableTrueType` is the engine's
gate for it, and `EditorFontsTests` runs these two files through it.

| File | Upstream | Licence |
|---|---|---|
| `Inter-Regular.ttf` | `google/fonts` `ofl/inter/Inter[opsz,wght].ttf`, instanced | SIL OFL 1.1 — `Inter-OFL.txt` |
| `MaterialSymbolsRounded-Editor.ttf` | `google/material-design-icons` `variablefont/MaterialSymbolsRounded[FILL,GRAD,opsz,wght].ttf`, instanced and subset | Apache 2.0 — `MaterialSymbols-LICENSE.txt` |

## Why they are instanced and subset

Upstream both are **variable** fonts, and stb_truetype does not read variation axes — it renders
the default instance and ignores the rest, so shipping the variable file would mean carrying
megabytes of axis data that can never be used. Instancing pins the axes and drops `fvar`/`gvar`.

The icon font matters most: upstream it is **15 MB** for 3,700 icons. Subsetting to the 48 the
editor actually draws gives **10 KB**. Inter is kept whole at its instanced weight (341 KB,
~2,850 codepoints) rather than subset to ASCII, because asset names are not the editor's to
predict — Latin, Greek and Cyrillic all render.

CJK is deliberately NOT here. Inter has no CJK coverage and a font that did would be tens of
megabytes; the host merges a system CJK font when it finds one (`UiFonts.FindCjkFont`), which is
what E0 already did.

## Regenerating

`icons.txt` is the list of Material Symbols names the editor draws, and `icon-map.txt` is that
list resolved to codepoints — regenerate both together when an icon is added, or the new name
will silently render as a missing glyph.

Needs `fonttools` (`pip install fonttools`). From a scratch directory:

```sh
# Inter: pin weight and optical size, keep every codepoint.
curl -sLo inter.ttf 'https://raw.githubusercontent.com/google/fonts/main/ofl/inter/Inter%5Bopsz%2Cwght%5D.ttf'
fonttools varLib.instancer inter.ttf wght=400 opsz=14 -o Inter-Regular.ttf

# Icons: pin the axes, then keep only the codepoints in icons.txt.
curl -sLo ms.ttf 'https://raw.githubusercontent.com/google/material-design-icons/master/variablefont/MaterialSymbolsRounded%5BFILL%2CGRAD%2Copsz%2Cwght%5D.ttf'
curl -sLo ms.codepoints 'https://raw.githubusercontent.com/google/material-design-icons/master/variablefont/MaterialSymbolsRounded%5BFILL%2CGRAD%2Copsz%2Cwght%5D.codepoints'
fonttools varLib.instancer ms.ttf FILL=0 GRAD=0 opsz=24 wght=400 -o ms-static.ttf
# unicodes.txt: "U+<hex>," for each name in icons.txt, looked up in ms.codepoints
pyftsubset ms-static.ttf --unicodes-file=unicodes.txt --no-hinting --layout-features='' \
  --name-IDs='*' --drop-tables+=DSIG --output-file=MaterialSymbolsRounded-Editor.ttf
```

The icons live in the Private Use Area (U+E000–U+F8FF), which is why merging them over Inter
cannot collide with any text glyph.
