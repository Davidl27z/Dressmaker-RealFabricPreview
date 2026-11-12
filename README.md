# Dressmaker – Real Fabric Preview

A [BepInEx](https://github.com/BepInEx/BepInEx) mod for **Dressmaker** that shows fabrics on the
sketchbook **the way they really look** on the finished garment, instead of the game's hand-drawn
colour fill.

*[Español más abajo](#español)*

---

## English

### What it does
- Drag a fabric from your inventory and drop it on a piece of the sketch: that piece shows the
  real fabric, with its colour, print, weave, sheen and sequins.
- Each fabric is rendered once with **the game's own fabric material** (the same shader used on the
  mannequin) and at the **garment's scale**, so prints are the size they will be on the dress.
- The sketch's outlines, seams and folds stay visible on top.
- Pieces come from the game's own region map, so they always match the current bodice, skirt and
  sleeves. Swap a component and the matching panels keep their fabric.
- Fabrics are remembered per dress and per save slot. Nothing is written to your save file.

| Action | Result |
|---|---|
| Drop a fabric on a piece | The piece shows that fabric |
| Double-click a piece | Removes its fabric |
| Colour a piece with the pencil | Removes its fabric (your colour shows) |
| Eraser "erase all" | Removes all fabrics of the dress |
| **F9** | Toggle real fabric / original drawn look |

### Install
Download the latest zip from **[Releases](../../releases)** and follow `README - INSTALL.txt` inside.

- **No mods yet?** Copy everything inside **"1 - Full install"** next to `Dressmaker.exe`. It
  includes BepInEx 5.4.23.6.
- **Already have BepInEx 5?** Copy `RealFabricPreview` from **"2 - Mod only"** into `BepInEx\plugins`.

> ⚠️ Use **BepInEx 5.4.23.6 or newer**. Older 5.4.23.x builds crash Dressmaker (Unity 6.3) at
> startup before any mod loads. BepInEx 6 is not supported.
> Remove *Dressmaker Fabric Pattern Preview* if you have it, because both mods paint the sketch.

### Compatibility and safety
- Tested with **Dressmaker 0.7.0** and **BepInEx 5.4.23.6**.
- At startup the mod checks that everything it needs exists in the game:
  - **If something essential is missing** (e.g. after a game update), it switches itself off,
    patches nothing, and logs a clear message. The game runs normally.
  - **If an optional piece is missing**, only that feature turns off.
- Errors never propagate into the game. Repeated errors switch the mod off.
- Check `BepInEx\LogOutput.log` (search for "Real Fabric") if something looks wrong.

### Settings
`BepInEx\config\dressmaker.realfabricpreview.cfg` is created on first launch.

| Option | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Turn the mod on/off |
| `ToggleKey` | `F9` | Real fabric ↔ drawn look |
| `WholePiece` | `false` | `true`: a drop covers the whole piece (all skirt panels) |
| `DoubleClickErases` | `true` | Double-click removes fabric |
| `PencilOverrides` | `true` | The pencil removes fabric |
| `SeparatePerComponents` | `false` | `true`: each bodice/skirt/sleeve combination has its own fabrics |
| `RealShader` | `true` | Render with the game's material; `false` = flat colour × print × weave |
| `LightIntensity` | `1.2` | Brightness for `RealShader` |
| `PageMeters` | `2` | Metres of fabric the page spans; lower = bigger prints |
| `PrintDensity` / `WeaveDensity` | `1` | Extra scale multipliers |
| `TintSeams` | `true` | Draw seams over the fabric colour instead of white |
| `Opacity` | `1` | Fabric opacity |

### Build from source
Requirements: Windows, Dressmaker with BepInEx 5 installed. No SDK is needed: the build uses the
`csc.exe` that ships with Windows.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Game "C:\...\steamapps\common\Dressmaker"
# release zip:
powershell -ExecutionPolicy Bypass -File build.ps1 -Package -BepInExZip "C:\...\BepInEx_win_x64_5.4.23.6.zip"
```

### How it works (short)
- `SketchbookSprite._refRt` is the game's region map of the dress: **B** = piece, **G** = panel,
  **R** = lines. The mod reads it with `AsyncGPUReadback`.
- A transparent `RawImage` overlay above the sketch shows the fabric on assigned regions. Line
  pixels stay transparent.
- Each fabric is rendered once through an off-screen orthographic camera with its own
  `MannequinFabric` material, then tiled. Garment UVs are cutting-table world units (≈ metres;
  see `PatternPanel.UpdateUVs`).
- The game's colour layer under the fabric is flood-filled with the fabric's average colour, using
  the game's own fill. Seams are then drawn over that colour. These fills are not saved.

### Credits & license
- MIT License, see [LICENSE](LICENSE). Third-party components: see
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- Inspired by *Dressmaker Fabric Pattern Preview*. This is an independent rewrite.
- Not affiliated with the developers of Dressmaker. No game files are distributed.

---

## Español

### Qué hace
- Arrastra una tela del inventario y suéltala sobre una pieza del boceto: esa pieza muestra la
  tela real, con su color, estampado, tejido, brillo y lentejuelas.
- Cada tela se dibuja con **el material real del juego** (el mismo shader del maniquí) y a la
  **escala de la prenda**, así que los estampados tienen el tamaño que tendrán en el vestido.
- Las líneas, costuras y pliegues del boceto se siguen viendo encima.
- Las piezas salen del propio mapa de regiones del juego, así que siempre coinciden con el cuerpo,
  la falda y las mangas actuales. Si cambias una pieza, los paneles equivalentes conservan su tela.
- Las telas se recuerdan por vestido y por ranura de guardado. No se escribe nada en tu partida.

| Acción | Resultado |
|---|---|
| Soltar una tela sobre una pieza | La pieza muestra esa tela |
| Doble clic sobre una pieza | Quita su tela |
| Colorear con el lápiz | Quita la tela (se ve tu color) |
| Goma "borrar todo" | Quita todas las telas del vestido |
| **F9** | Alterna tela real y dibujo original |

### Instalación
Descarga el ZIP más reciente de **[Releases](../../releases)** y sigue `LEEME - INSTALAR.txt`.

- **¿No tienes mods?** Copia todo lo de **"1 - Instalacion completa"** al lado de `Dressmaker.exe`.
  Incluye BepInEx 5.4.23.6.
- **¿Ya tienes BepInEx 5?** Copia `RealFabricPreview` de **"2 - Solo el mod"** a `BepInEx\plugins`.

> ⚠️ Usa **BepInEx 5.4.23.6 o más nuevo**. Las 5.4.23.x anteriores hacen que Dressmaker
> (Unity 6.3) se cierre al arrancar. BepInEx 6 no es compatible.
> Si tienes *Dressmaker Fabric Pattern Preview*, quítalo: los dos pintan el boceto.

### Compatibilidad y seguridad
- Probado con **Dressmaker 0.7.0** y **BepInEx 5.4.23.6**.
- Si una actualización del juego cambia algo esencial, el mod se apaga solo con un mensaje claro
  en `BepInEx\LogOutput.log` y el juego funciona con normalidad.
- Si falta algo opcional, solo se apaga esa función.

### Ajustes
Ver la tabla en inglés de arriba. El archivo es `BepInEx\config\dressmaker.realfabricpreview.cfg`.

### Créditos y licencia
Licencia MIT. Inspirado en *Dressmaker Fabric Pattern Preview*, es una reescritura independiente.
No está afiliado con los desarrolladores de Dressmaker y no se distribuye ningún archivo del juego.
