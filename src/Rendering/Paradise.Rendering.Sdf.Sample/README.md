# SDF garden

A window onto `Paradise.Rendering.Sdf`: an animated scene that uses every primitive and operation,
an orbit camera, and picking through the CPU field. Run from the repository root (requires
.NET 10 and a WebGPU-capable GPU):

```sh
dotnet run --project src/Rendering/Paradise.Rendering.Sdf.Sample
```

| Control | Effect |
| --- | --- |
| Drag | Orbit the camera |
| Scroll | Zoom |
| Click | Pick: trace the ray under the pointer on the CPU, mark the hit and print its position and albedo |
| Space | Pause the animation |
| S / O / T | Toggle marched shadows, ambient occlusion, ACES or linear tonemap |
| R / Esc | Reset the camera, quit |

## What to look at

| Shape | What it shows |
| --- | --- |
| Die on the left | A box intersected with a sphere, then bored by an orbiting cutter. The three are first in the list because a shape combines with everything before it. |
| Blobs in the middle | Five spheres in smooth union. They merge and split as they move, blend their colors across the seams, and draw the ground up toward them when they dip. |
| Gold ring | A metallic torus under a rotating transform, reflecting the sky gradient. |
| Leaning capsule | A smooth union with the ground: no crease where they meet. |
| Glowing marker | Where the last click landed. `SdfField` traced the same shapes with the renderer's stepping rule, so the marker sits on the surface that was drawn under the pointer. |

`SdfGarden.Update` rebuilds the shape list in place every frame; shapes are plain values and
the renderer retains nothing between frames.

## Options

| Option | Effect |
| --- | --- |
| `--size WIDTHxHEIGHT` | Window or headless target size; 1280x720 by default |
| `--stats` | Print average frame time every 120 frames |
| `--features LIST` | Feature overrides, such as `-rendering.sdfScene`; `PARADISE_FEATURES` is read too |
| `--list-features` | Print the SDF feature switches and exit, without acquiring a GPU |
| `--headless N` | Render N frames at 1/60 s steps against an offscreen target, then exit |
| `--screenshot PATH` | With `--headless`: write the last frame as a PNG |
| `--pick X,Y` | With `--headless`: click that pixel before the last frame |

For a reproducible smoke check and image:

```sh
dotnet run --project src/Rendering/Paradise.Rendering.Sdf.Sample -- --headless 12 --size 480x300 --pick 330,250 --screenshot /tmp/sdf-garden.png
```

It prints the picked point and the frame's passes; the screenshot shows the marker at that pixel.
Headless mode reads every frame back, so `--stats` there counts finished frames and includes the
readback. On a software adapter keep the size small: each frame is traced on CPU threads, and one
that outlasts the backend's readback wait fails. The wait is five seconds unless
`PARADISE_GPU_TIMEOUT_SECONDS` sets another.

## Why it lives here

Engine samples belong to [ParadiseSamples](https://github.com/ParadiseEngine/ParadiseSamples),
which builds only against published NuGet packages. `Paradise.Rendering.Sdf` is not published yet,
so this sample references the engine projects and stays beside the renderer. To move it once the
package ships, copy this directory and replace the three project references with package
references to `Paradise.Rendering.Sdf`, `Paradise.Rendering.WebGPU` and `Paradise.Windowing.Sdl`.
