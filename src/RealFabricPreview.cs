// Dressmaker Real Fabric Preview  (standalone)
// Copyright (c) 2026 Davidl27z - MIT License (see LICENSE)
//
// Drop a fabric swatch on a piece of the sketch and that piece shows the fabric the way it
// really looks (the fabric's own material: colour, print, weave, sheen, sequins), instead of
// the game's "drawn" colour fill.
//
// How it works
//  - The game keeps a region map of the dress in SketchbookSprite._refRt:
//      B channel = garment piece, G channel = panel inside the piece, R channel = lines.
//  - When a fabric drag ends over the sketch we read the region under the cursor and remember
//    (design, region) -> fabric. Regions come from the game itself, so they always match the
//    current bodice / skirt / sleeves.
//  - An overlay RawImage above the sketch shows the real fabric on assigned regions. Line pixels
//    stay transparent so the game's own outlines and folds stay visible.
//
// Written in C# 5 so it builds with the csc.exe that ships with Windows.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace DressmakerRealFabric
{
    [BepInPlugin(GUID, "Dressmaker Real Fabric Preview", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "dressmaker.realfabricpreview";

        // ------------------------------------------------------------------ config
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<KeyCode> CfgToggleKey;
        internal static ConfigEntry<float> CfgOpacity;
        internal static ConfigEntry<bool> CfgWholePiece;
        internal static ConfigEntry<float> CfgPrintScale;
        internal static ConfigEntry<float> CfgWeaveScale;
        internal static ConfigEntry<bool> CfgFillGameLayer;
        internal static ConfigEntry<bool> CfgRealShader;
        internal static ConfigEntry<float> CfgPageMeters;
        internal static ConfigEntry<float> CfgLightIntensity;
        internal static ConfigEntry<float> CfgWeaveStrength;
        internal static ConfigEntry<bool> CfgDoubleClickErase;
        internal static ConfigEntry<bool> CfgPencilOverrides;
        internal static ConfigEntry<bool> CfgPerComponents;
        internal static ConfigEntry<bool> CfgDebugKeys;
        internal static ConfigEntry<bool> CfgVerbose;

        internal static Plugin Instance;
        internal static volatile bool RefDirty = true;
        internal static volatile bool NeedRebuild = true;

        private Harmony _harmony;

        // ------------------------------------------------------------------ game reflection
        private static readonly FieldInfo FImage = AccessTools.Field(typeof(SketchbookSprite), "_image");
        private static readonly FieldInfo FRefRt = AccessTools.Field(typeof(SketchbookSprite), "_refRt");
        private static readonly FieldInfo FPrevClicks = AccessTools.Field(typeof(SketchbookSprite), "previousClicks");
        private static readonly FieldInfo FIsDragging = AccessTools.Field(typeof(SketchbookFabric), "_isDragging");
        private static readonly FieldInfo FQuestTitle = AccessTools.Field(typeof(Sketchbook), "questTitle");
        private static readonly string[] DesignFields = {
            "_selectedBodice", "_selectedBodiceVariant", "_selectedCollar", "_selectedCollarVariant",
            "_selectedSleeve", "_selectedSleeveVariant", "_selectedSkirt", "_selectedSkirtVariant" };

        private SketchbookSprite _sprite;
        private Sketchbook _sketchbook;
        private RawImage _image;
        private float _nextFind;

        // ------------------------------------------------------------------ overlay
        private RawImage _overlay;
        private Texture2D _overlayTex;
        private Color32[] _outPx;
        private bool[] _filled;
        private bool _userHidden;

        // region map (from _refRt)
        private Color32[] _refPx;
        private int _refW, _refH;
        private bool _refPending, _haveRef;

        // ------------------------------------------------------------------ assignments
        // designKey -> (regionKey -> fabric asset name)
        private readonly Dictionary<string, Dictionary<int, string>> _store = new Dictionary<string, Dictionary<int, string>>();
        private string _storePath;
        private string _designKey = "";
        private float _nextDesignCheck;

        // drag tracking
        private readonly Dictionary<int, bool> _wasDragging = new Dictionary<int, bool>();
        private SketchbookFabric[] _swatches = new SketchbookFabric[0];
        private float _nextSwatchScan;
        private float _lastClickTime;
        private Vector2 _lastClickPos;

        // fabric pixel cache
        private class FabricTex
        {
            public Color32 tint;
            public Color average;
            public Color32[] printSrc; public int psw, psh;  // _Albedo source pixels (null = plain)
            public float[] weaveSrc; public int wsw, wsh;    // _Detail source luminance
            public float printRepX, printRepY, weaveRep;     // repeats across the page
            // tiles resampled to their on-page size (box filtered, so small prints don't alias)
            public Color32[] print; public int pw, ph;
            public float[] weave; public int ww, wh;
            public int builtForW = -1;
            // fabric rendered with its real material (MannequinFabric shader + light), one world unit
            public Color32[] litSrc; public int lsw;
            public float unitsPerPage;
            public Color32[] lit; public int lw, lh;
        }

        private static Color32[] BoxResample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            Color32[] dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int sy = y0; sy < y1; sy++)
                        for (int sx = x0; sx < x1; sx++) { Color32 c = src[sy * sw + sx]; r += c.r; g += c.g; b += c.b; n++; }
                    dst[y * dw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
            return dst;
        }

        // Render one world unit of the fabric with its own material, lit by a soft key light,
        // through an off-screen orthographic camera. The garment's UVs are in the same world units,
        // so this is exactly the fabric the mannequin shows (colour, print, weave, normal map, sheen,
        // sequins), minus folds and the room's lighting.
        private const int CaptureLayer = 31;
        private Color32[] RenderLitSwatch(Material mat, int size)
        {
            GameObject root = null;
            Mesh mesh = null;
            RenderTexture rt = null;
            Light prevSun = RenderSettings.sun;
            try
            {
                root = new GameObject("RealFabricCapture");
                root.hideFlags = HideFlags.HideAndDontSave;
                root.transform.position = new Vector3(5000f, 5000f, 5000f);

                // 2x2 quad, UV -0.5..1.5, so the camera (which sees the middle 1x1) never sees its edges
                mesh = new Mesh();
                mesh.hideFlags = HideFlags.HideAndDontSave;
                mesh.vertices = new Vector3[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
                Vector2[] uv = { new Vector2(-0.5f, -0.5f), new Vector2(1.5f, -0.5f), new Vector2(-0.5f, 1.5f), new Vector2(1.5f, 1.5f) };
                mesh.uv = uv; mesh.uv2 = uv;
                mesh.normals = new Vector3[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                mesh.tangents = new Vector4[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) };
                mesh.colors = new Color[] { Color.white, Color.white, Color.white, Color.white };
                mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
                GameObject quad = new GameObject("quad");
                quad.hideFlags = HideFlags.HideAndDontSave;
                quad.layer = CaptureLayer;
                quad.transform.SetParent(root.transform, false);
                quad.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = quad.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                GameObject lgo = new GameObject("light");
                lgo.hideFlags = HideFlags.HideAndDontSave;
                lgo.transform.SetParent(root.transform, false);
                lgo.transform.rotation = Quaternion.LookRotation(new Vector3(0.25f, -0.35f, 1f)); // from top-left-front
                Light light = lgo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = Color.white;
                light.intensity = CfgLightIntensity.Value;
                light.shadows = LightShadows.None;
                light.cullingMask = 1 << CaptureLayer;
                RenderSettings.sun = light;

                GameObject cgo = new GameObject("cam");
                cgo.hideFlags = HideFlags.HideAndDontSave;
                cgo.transform.SetParent(root.transform, false);
                cgo.transform.localPosition = new Vector3(0, 0, -2f);
                cgo.transform.rotation = Quaternion.identity;
                Camera cam = cgo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = 0.5f;
                cam.aspect = 1f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 10f;
                cam.cullingMask = 1 << CaptureLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(1f, 0f, 1f, 1f);
                cam.allowHDR = false;
                cam.allowMSAA = false;
                rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D t2 = new Texture2D(size, size, TextureFormat.RGBA32, false);
                t2.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                t2.Apply();
                RenderTexture.active = prev;
                Color32[] px = t2.GetPixels32();
                Destroy(t2);

                // sanity: reject a failed render (all background or all black)
                int magenta = 0, black = 0;
                for (int i = 0; i < px.Length; i += 37)
                {
                    Color32 c = px[i];
                    if (c.r > 240 && c.g < 15 && c.b > 240) magenta++;
                    if (c.r < 3 && c.g < 3 && c.b < 3) black++;
                }
                int samples = px.Length / 37;
                if (magenta > samples / 10)
                {
                    // the quad was not drawn at all: the render pipeline doesn't support this here
                    _featLit = false;
                    Logger.LogWarning("Real-shader render is not supported on this setup; using flat colours from now on.");
                    return null;
                }
                if (black > samples * 9 / 10)
                {
                    // pitch-black result: maybe a very dark fabric, maybe a lighting problem -> flat for this fabric
                    Logger.LogWarning("Real-shader render came out black for this fabric; using flat colours for it.");
                    return null;
                }
                return px;
            }
            catch (Exception e)
            {
                _featLit = false;
                Logger.LogWarning("Real-shader render failed, using flat colours from now on: " + e.Message);
                return null;
            }
            finally
            {
                RenderSettings.sun = prevSun;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (root != null) DestroyImmediate(root);
                if (mesh != null) DestroyImmediate(mesh); // meshes are assets: destroying the GameObject doesn't free them
            }
        }

        // Box-filter a source image down/up to exactly one on-page tile.
        private static void BuildTiles(FabricTex ft, int W, int H)
        {
            if (ft.builtForW == W) return;
            ft.builtForW = W;
            if (ft.litSrc != null)
            {
                ft.lw = ft.lh = Mathf.Clamp(Mathf.RoundToInt(W / ft.unitsPerPage), 2, 1024);
                ft.lit = BoxResample(ft.litSrc, ft.lsw, ft.lsw, ft.lw, ft.lh);
            }
            if (ft.printSrc != null)
            {
                ft.pw = Mathf.Clamp(Mathf.RoundToInt(W / ft.printRepX), 2, 1024);
                ft.ph = Mathf.Clamp(Mathf.RoundToInt(H / ft.printRepY), 2, 1024);
                ft.print = new Color32[ft.pw * ft.ph];
                for (int y = 0; y < ft.ph; y++)
                {
                    int y0 = y * ft.psh / ft.ph, y1 = Math.Max(y0 + 1, (y + 1) * ft.psh / ft.ph);
                    for (int x = 0; x < ft.pw; x++)
                    {
                        int x0 = x * ft.psw / ft.pw, x1 = Math.Max(x0 + 1, (x + 1) * ft.psw / ft.pw);
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int sy = y0; sy < y1; sy++)
                            for (int sx = x0; sx < x1; sx++)
                            { Color32 c = ft.printSrc[sy * ft.psw + sx]; r += c.r; g += c.g; b += c.b; n++; }
                        ft.print[y * ft.pw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                    }
                }
            }
            if (ft.weaveSrc != null)
            {
                ft.ww = Mathf.Clamp(Mathf.RoundToInt(W / ft.weaveRep), 2, 512);
                ft.wh = ft.ww;
                ft.weave = new float[ft.ww * ft.wh];
                double sum = 0;
                for (int y = 0; y < ft.wh; y++)
                {
                    int y0 = y * ft.wsh / ft.wh, y1 = Math.Max(y0 + 1, (y + 1) * ft.wsh / ft.wh);
                    for (int x = 0; x < ft.ww; x++)
                    {
                        int x0 = x * ft.wsw / ft.ww, x1 = Math.Max(x0 + 1, (x + 1) * ft.wsw / ft.ww);
                        float s = 0; int n = 0;
                        for (int sy = y0; sy < y1; sy++)
                            for (int sx = x0; sx < x1; sx++) { s += ft.weaveSrc[sy * ft.wsw + sx]; n++; }
                        ft.weave[y * ft.ww + x] = s / n;
                        sum += s / n;
                    }
                }
                float mean = (float)(sum / ft.weave.Length);
                if (mean < 0.01f) mean = 1f;
                for (int i = 0; i < ft.weave.Length; i++) ft.weave[i] /= mean;
            }
        }
        private readonly Dictionary<string, FabricTex> _fabTex = new Dictionary<string, FabricTex>();
        private Dictionary<string, Fabric> _fabricsByName;

        // ================================================================== lifecycle
        private void Awake()
        {
            Instance = this;
            CfgEnabled = Config.Bind("General", "Enabled", true, "Show real fabric on the sketch / Mostrar la tela real en el boceto.");
            CfgToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F9, "Toggle real fabric vs. the game's drawn look / Alternar tela real y dibujo.");
            CfgWholePiece = Config.Bind("General", "WholePiece", false,
                "true = a dropped fabric covers the whole piece (all skirt panels); false = only the panel under the cursor, like the pencil.");
            CfgDoubleClickErase = Config.Bind("General", "DoubleClickErases", true, "Double-click a piece to remove its fabric / Doble clic para quitar la tela.");
            CfgPerComponents = Config.Bind("General", "SeparatePerComponents", false,
                "false = fabrics are remembered per dress and carry over when you swap bodice/skirt/sleeves; true = each component combination has its own fabrics.");
            CfgPencilOverrides = Config.Bind("General", "PencilOverrides", true, "Colouring a piece with the pencil removes its fabric.");
            CfgOpacity = Config.Bind("Look", "Opacity", 1f, new ConfigDescription("Fabric opacity (0-1).", new AcceptableValueRange<float>(0f, 1f)));
            CfgPrintScale = Config.Bind("Look", "PrintDensity", 1f, new ConfigDescription(
                "1 = prints at the same size as the game's fabric swatch. Higher = smaller/denser print, lower = bigger print.",
                new AcceptableValueRange<float>(0.1f, 8f)));
            CfgWeaveScale = Config.Bind("Look", "WeaveDensity", 1f, new ConfigDescription(
                "1 = weave texture at the swatch's size. Higher = finer weave.", new AcceptableValueRange<float>(0.1f, 8f)));
            CfgPageMeters = Config.Bind("Look", "PageMeters", 2.0f, new ConfigDescription(
                "How many metres of fabric the sketch page spans (garment scale). Lower = bigger prints.",
                new AcceptableValueRange<float>(0.5f, 8f)));
            CfgRealShader = Config.Bind("Look", "RealShader", true,
                "true = render each fabric with the game's own fabric material and a light (sheen, sequins, normal maps, like the mannequin); false = flat colour x print x weave.");
            CfgLightIntensity = Config.Bind("Look", "LightIntensity", 1.2f, new ConfigDescription(
                "Brightness of the light used for RealShader.", new AcceptableValueRange<float>(0.2f, 4f)));
            CfgFillGameLayer = Config.Bind("Look", "TintSeams", true,
                "Also paint the game's own colour layer with the fabric's average colour, so seams and edges are drawn over the fabric colour instead of white.");
            CfgWeaveStrength = Config.Bind("Look", "WeaveStrength", 0.6f, new ConfigDescription("How visible the weave texture is (0 = flat colour).", new AcceptableValueRange<float>(0f, 1f)));
            CfgDebugKeys = Config.Bind("Debug", "DebugKeys", false, "F10 dump images/info, F11 open sketchbook, F12 test-paint.");
            CfgVerbose = Config.Bind("Debug", "Verbose", false, "Extra logging.");
            _simulateMissing = Config.Bind("Debug", "SimulateMissing", "",
                "Testing only: comma-separated game members to treat as missing (e.g. SketchbookSprite.ExecuteFill).").Value;

            EventHandler rebuild = delegate { NeedRebuild = true; _fabTex.Clear(); };
            CfgOpacity.SettingChanged += rebuild;
            CfgPrintScale.SettingChanged += rebuild;
            CfgWeaveScale.SettingChanged += rebuild;
            CfgFillGameLayer.SettingChanged += rebuild;
            CfgRealShader.SettingChanged += rebuild;
            CfgPageMeters.SettingChanged += rebuild;
            CfgLightIntensity.SettingChanged += rebuild;
            CfgWeaveStrength.SettingChanged += rebuild;

            LogEnvironment();
            if (!CheckCompatibility())
            {
                _disabled = true;
                return; // nothing is patched: the game runs exactly as without the mod
            }

            _storePath = Path.Combine(Path.Combine(Paths.ConfigPath, "RealFabricPreview"), "assignments.txt");
            LoadStore();

            _harmony = new Harmony(GUID);
            _featRefHook = TryPatch(typeof(SketchbookSprite), "UseNewDressTextures", "OnNewDressTextures", "refresh when the dress changes (will poll instead)");
            _featEraseAll = TryPatch(typeof(SketchbookSprite), "EraseAllFills", "OnEraseAll", "eraser 'erase all' removes fabrics");
            if (_featPencil) _featPencil = TryPatch(typeof(SketchbookSprite), "FillPoint", "OnFillPoint", "pencil removes fabric");
            if (_featSeams) _featSeams = TryPatch(typeof(SketchbookSprite), "ClearColourTexture", "OnClearColour", "tinted seams");

            Logger.LogInfo("Real Fabric Preview " + Version + " loaded (" + CountAssignments() + " saved assignments). Toggle: " + CfgToggleKey.Value);
        }

        public const string Version = "2.2.0";
        // Game build the mod was last tested with (Application.version). Other builds usually work;
        // the startup check below decides, this is only used for the log message.
        public const string TestedGameVersion = "0.7.0";

        // ------------------------------------------------------------------ compatibility
        private bool _disabled;
        private static string _simulateMissing;
        private bool _featRefHook = true, _featEraseAll = true, _featPencil = true, _featSeams = true, _featLit = true, _featDesignKey = true;

        private void LogEnvironment()
        {
            string bep = "?", game = "?", unity = "?";
            try
            {
                Version v = typeof(BaseUnityPlugin).Assembly.GetName().Version;
                bep = v.ToString();
                if (v < new Version(5, 4, 23, 6))
                    Logger.LogWarning("BepInEx " + bep + " is older than 5.4.23.6. With Unity 6.3 older versions can crash the game at startup; " +
                                      "please update. / BepInEx antiguo: actualiza a 5.4.23.6 o superior.");
            }
            catch (Exception) { }
            try { game = Application.version; unity = Application.unityVersion; } catch (Exception) { }
            Logger.LogInfo("Environment: BepInEx " + bep + ", Unity " + unity + ", Dressmaker " + game + " (tested with " + TestedGameVersion + ")");
            if (game != TestedGameVersion)
                Logger.LogWarning("Dressmaker version differs from the tested one. If something looks wrong, check this log. / " +
                                  "La versión de Dressmaker es distinta de la probada; si algo falla, revisa este registro.");
        }

        private static string Missing(Type t, string member, MemberTypes kind)
        {
            if (t == null) return "type";
            if (!string.IsNullOrEmpty(_simulateMissing) && ("," + _simulateMissing.Replace(" ", "") + ",").Contains("," + t.Name + "." + member + ","))
                return t.Name + "." + member + " (simulated)";
            BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            MemberInfo[] m = t.GetMember(member, kind, bf);
            return (m != null && m.Length > 0) ? null : t.Name + "." + member;
        }

        // Verify everything the mod uses inside the game. Core members missing -> the mod switches
        // itself off cleanly. Optional members missing -> only that feature is switched off.
        private bool CheckCompatibility()
        {
            List<string> core = new List<string>();
            try
            {
                Type sprite = typeof(SketchbookSprite), swatch = typeof(SketchbookFabric), fabric = typeof(Fabric);
                foreach (string s in new string[] {
                    Missing(sprite, "_image", MemberTypes.Field), Missing(sprite, "_refRt", MemberTypes.Field),
                    Missing(swatch, "_isDragging", MemberTypes.Field), Missing(swatch, "fabric", MemberTypes.Field),
                    Missing(fabric, "material", MemberTypes.Field) })
                    if (s != null) core.Add(s);
            }
            catch (Exception e) { core.Add("game types (" + e.GetType().Name + ")"); }

            if (core.Count > 0)
            {
                Logger.LogError("Real Fabric Preview is OFF: this Dressmaker version is not compatible (missing: " + string.Join(", ", core.ToArray()) +
                                "). The game is not affected. / Mod DESACTIVADO: esta versión de Dressmaker no es compatible. El juego funciona normal.");
                return false;
            }

            List<string> off = new List<string>();
            Type sp = typeof(SketchbookSprite);
            if (Missing(sp, "ExecuteFill", MemberTypes.Method) != null || Missing(sp, "ClearColourTexture", MemberTypes.Method) != null)
            { _featSeams = false; off.Add("tinted seams"); }
            if (Missing(sp, "previousClicks", MemberTypes.Field) != null)
            { _featPencil = false; off.Add("pencil removes fabric"); }
            try
            {
                if (Missing(typeof(Sketchbook), "questTitle", MemberTypes.Field) != null
                    || Missing(typeof(PlayerProgress), "Current", MemberTypes.Property) != null
                    || Missing(typeof(PlayerProgress), "currentQuestState", MemberTypes.Property) != null
                    || Missing(typeof(PlayerProgress), "activeCommissions", MemberTypes.Field) != null
                    || Missing(typeof(QuestState), "thumbnailGuid", MemberTypes.Field) != null)
                { _featDesignKey = false; off.Add("per-dress memory (fabrics shared per save slot)"); }
            }
            catch (Exception) { _featDesignKey = false; off.Add("per-dress memory"); }
            if (off.Count > 0)
                Logger.LogWarning("Some features are off for this game version: " + string.Join(", ", off.ToArray()));
            return true;
        }

        private bool TryPatch(Type t, string method, string postfix, string feature)
        {
            try
            {
                MethodInfo m = Missing(t, method, MemberTypes.Method) == null ? AccessTools.Method(t, method) : null;
                if (m == null) { Logger.LogWarning("Feature off (" + feature + "): " + t.Name + "." + method + " not found"); return false; }
                _harmony.Patch(m, null, new HarmonyMethod(typeof(Plugin), postfix));
                return true;
            }
            catch (Exception e) { Logger.LogWarning("Feature off (" + feature + "): could not patch " + t.Name + "." + method + ": " + e.Message); return false; }
        }

        // Errors never escape into the game. A burst of repeated errors switches the mod off.
        private int _errorCount;
        private float _errorWindowStart;
        private void ReportError(string where, Exception e)
        {
            if (Time.unscaledTime - _errorWindowStart > 10f) { _errorWindowStart = Time.unscaledTime; _errorCount = 0; }
            _errorCount++;
            if (_errorCount <= 3) Logger.LogError("Error in " + where + ": " + e);
            if (_errorCount >= 30 && !_disabled)
            {
                _disabled = true;
                try { if (_overlay != null) _overlay.enabled = false; } catch (Exception) { }
                Logger.LogError("Real Fabric Preview switched itself OFF after repeated errors (the game keeps working). / " +
                                "Mod desactivado tras errores repetidos; el juego sigue funcionando.");
            }
        }

        // ------------------------------------------------------------------ harmony postfixes
        // Postfixes run inside the game's own methods: they must never throw.
        public static void OnNewDressTextures()
        {
            RefDirty = true; NeedRebuild = true;
        }

        public static void OnEraseAll()
        {
            if (Instance == null || Instance._disabled) return;
            try { Instance.ClearCurrentDesign(); }
            catch (Exception e) { Instance.ReportError("erase all", e); }
        }

        public static void OnFillPoint(SketchbookSprite __instance)
        {
            if (Instance == null || Instance._disabled || !CfgPencilOverrides.Value) return;
            try
            {
                IList clicks = FPrevClicks.GetValue(__instance) as IList;
                if (clicks == null || clicks.Count == 0) return;
                object last = clicks[clicks.Count - 1];
                Vector2 p = (Vector2)last.GetType().GetField("Item1").GetValue(last);
                Instance.RemoveAtPixel(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), "pencil");
            }
            catch (Exception e) { Instance.Logger.LogWarning("Pencil hook: " + e.Message); }
        }

        // ================================================================== update loop
        private float _nextRefPoll;

        private void Update()
        {
            if (_disabled) return;
            try { UpdateInner(); }
            catch (Exception e) { ReportError("Update", e); }
        }

        private void UpdateInner()
        {
            bool toggle = false;
            try { toggle = Input.GetKeyDown(CfgToggleKey.Value); } catch (Exception) { }
            if (toggle)
            {
                _userHidden = !_userHidden;
                Logger.LogInfo(_userHidden ? "Real fabric hidden" : "Real fabric shown");
            }
            if (CfgDebugKeys.Value) DebugKeys();

            if (!FindSketch()) return;

            EnsureOverlay();
            bool visible = CfgEnabled.Value && _image.isActiveAndEnabled;
            _overlay.enabled = visible && !_userHidden && _overlayTex != null;
            if (!visible) return;
            _overlay.uvRect = _image.uvRect;

            if (Time.unscaledTime >= _nextDesignCheck)
            {
                _nextDesignCheck = Time.unscaledTime + 0.25f;
                string k = ComputeDesignKey();
                if (k != _designKey)
                {
                    if (CfgVerbose.Value) Logger.LogInfo("Design: " + k);
                    _designKey = k;
                    NeedRebuild = true;
                }
            }

            if (!_featRefHook && Time.unscaledTime >= _nextRefPoll)
            {
                _nextRefPoll = Time.unscaledTime + 1.5f; // no hook on this game version: poll for dress changes
                RefDirty = true;
            }

            RenderTexture refRt = FRefRt.GetValue(_sprite) as RenderTexture;
            if (RefDirty && !_refPending && refRt != null)
            {
                RefDirty = false;
                _refPending = true;
                AsyncGPUReadback.Request(refRt, 0, TextureFormat.RGBA32, OnRefReadback);
            }

            DetectDrops(); // also while hidden with F9, so drops are never lost

            if (_retryRebuildAt > 0f && Time.unscaledTime >= _retryRebuildAt)
            {
                _retryRebuildAt = -1f;
                NeedRebuild = true; // a fabric was missing: try again
            }

            if (NeedRebuild && _haveRef && !_refPending)
            {
                NeedRebuild = false;
                Rebuild();
            }
            if (NeedColourApply && _haveRef && !_refPending && !NeedRebuild)
            {
                NeedColourApply = false;
                ApplyGameColourLayer();
            }
        }

        private bool FindSketch()
        {
            if (_sprite == null || _image == null)
            {
                if (Time.unscaledTime < _nextFind) return false;
                _nextFind = Time.unscaledTime + 1f;
                _sprite = UnityEngine.Object.FindAnyObjectByType<SketchbookSprite>(FindObjectsInactive.Include);
                if (_sprite == null) return false;
                _image = FImage.GetValue(_sprite) as RawImage;
                if (_image == null) { _sprite = null; return false; }
                _sketchbook = _sprite.GetComponentInParent<Sketchbook>(true);
                if (_sketchbook == null) _sketchbook = UnityEngine.Object.FindAnyObjectByType<Sketchbook>(FindObjectsInactive.Include);
                _overlay = null;
                _haveRef = false;
                RefDirty = true;
                NeedRebuild = true;
                _fabricsByName = null;
            }
            return true;
        }

        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            GameObject go = new GameObject("RealFabricOverlay", typeof(RectTransform));
            go.layer = _image.gameObject.layer;
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(_image.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.SetAsFirstSibling(); // right above the sketch, below swatches placed on it
            _overlay = go.AddComponent<RawImage>();
            _overlay.raycastTarget = false;
            _overlay.enabled = false;
            if (_overlayTex != null) _overlay.texture = _overlayTex;
        }

        private void OnRefReadback(AsyncGPUReadbackRequest req)
        {
            _refPending = false;
            if (_disabled) return;
            try
            {
                if (req.hasError) { RefDirty = true; return; }
                NativeArray<Color32> data = req.GetData<Color32>();
                // unchanged map (e.g. when polling): nothing to do
                if (_haveRef && _refPx != null && _refPx.Length == data.Length && _refW == req.width && SameData(data, _refPx)) return;
                _refW = req.width; _refH = req.height;
                if (_refPx == null || _refPx.Length != data.Length) _refPx = new Color32[data.Length];
                data.CopyTo(_refPx);
                _haveRef = true;
                NeedRebuild = true;
            }
            catch (Exception e) { ReportError("region map readback", e); }
        }

        private static bool SameData(NativeArray<Color32> a, Color32[] b)
        {
            for (int i = 0; i < b.Length; i += 61)
            {
                Color32 x = a[i], y = b[i];
                if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a) return false;
            }
            for (int i = 0; i < b.Length; i++)
            {
                Color32 x = a[i], y = b[i];
                if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a) return false;
            }
            return true;
        }

        // ================================================================== regions
        // Interior pixel of a piece: opaque and not a line (R channel is used for lines).
        private static bool IsInterior(Color32 c) { return c.a >= 128 && c.r < 128; }

        private static int RegionKey(Color32 c, bool wholePiece)
        {
            int b = (c.b + 2) / 4;
            if (wholePiece) return b;
            int g = (c.g + 2) / 4;
            return (b << 8) | g;
        }

        private bool PixelAt(int x, int y, out Color32 c)
        {
            c = new Color32();
            if (_refPx == null || x < 0 || y < 0 || x >= _refW || y >= _refH) return false;
            c = _refPx[y * _refW + x];
            return true;
        }

        // Find the region at a pixel, looking around a little if it's on a line.
        private bool RegionAtPixel(int px, int py, bool wholePiece, out int key)
        {
            // Majority vote over a small window, so anti-aliased pixels next to a line
            // (which carry in-between colours) never win over the real panel.
            key = 0;
            Color32 c;
            for (int radius = 4; radius <= 16; radius += 6)
            {
                Dictionary<int, int> votes = new Dictionary<int, int>();
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (!PixelAt(px + dx, py + dy, out c) || !IsInterior(c)) continue;
                        int k = RegionKey(c, wholePiece);
                        int n; votes.TryGetValue(k, out n); votes[k] = n + 1;
                    }
                int best = 0;
                foreach (KeyValuePair<int, int> v in votes) if (v.Value > best) { best = v.Value; key = v.Key; }
                if (best >= 5) return true;
            }
            return false;
        }

        private bool ScreenToPixel(Vector2 screen, out int px, out int py)
        {
            px = py = -1;
            RectTransform rt = _image.rectTransform;
            Canvas canvas = _image.canvas;
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screen, cam, out local)) return false;
            Rect r = rt.rect;
            float u = (local.x - r.xMin) / r.width;
            float v = (local.y - r.yMin) / r.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            Rect uv = _image.uvRect;
            u = uv.x + u * uv.width; v = uv.y + v * uv.height;
            px = Mathf.Clamp((int)(u * _refW), 0, _refW - 1);
            py = Mathf.Clamp((int)(v * _refH), 0, _refH - 1);
            return true;
        }

        // ================================================================== drops / erase
        private void DetectDrops()
        {
            if (Time.unscaledTime >= _nextSwatchScan)
            {
                _nextSwatchScan = Time.unscaledTime + 0.3f;
                _swatches = UnityEngine.Object.FindObjectsByType<SketchbookFabric>(FindObjectsSortMode.None);
            }
            bool anyDragging = false;
            for (int i = 0; i < _swatches.Length; i++)
            {
                SketchbookFabric sf = _swatches[i];
                if (sf == null) continue;
                int id = sf.GetInstanceID();
                bool dragging = (bool)FIsDragging.GetValue(sf);
                bool was;
                _wasDragging.TryGetValue(id, out was);
                _wasDragging[id] = dragging;
                if (dragging) anyDragging = true;
                if (was && !dragging && sf.fabric != null && sf.isActiveAndEnabled)
                    AssignAtScreen(Input.mousePosition, sf.fabric);
            }

            if (CfgDoubleClickErase.Value && !anyDragging)
            {
                bool down = false;
                try { down = Input.GetMouseButtonDown(0); } catch (Exception) { }
                if (down)
                {
                    Vector2 m = Input.mousePosition;
                    if (Time.unscaledTime - _lastClickTime < 0.35f && Vector2.Distance(m, _lastClickPos) < 12f)
                    {
                        int px, py;
                        if (_haveRef && PointerOnSketch(m) && ScreenToPixel(m, out px, out py)) RemoveAtPixel(px, py, "double-click");
                        _lastClickTime = 0f;
                    }
                    else { _lastClickTime = Time.unscaledTime; _lastClickPos = m; }
                }
            }
        }

        // False when the pointer is over a button, a swatch or another control drawn on top of the
        // sketch (double-clicking those must not remove fabric).
        private readonly List<UnityEngine.EventSystems.RaycastResult> _hits = new List<UnityEngine.EventSystems.RaycastResult>();
        private bool PointerOnSketch(Vector2 screen)
        {
            try
            {
                UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null) return true;
                UnityEngine.EventSystems.PointerEventData ped = new UnityEngine.EventSystems.PointerEventData(es);
                ped.position = screen;
                _hits.Clear();
                es.RaycastAll(ped, _hits);
                if (_hits.Count == 0 || _hits[0].gameObject == null) return true;
                GameObject top = _hits[0].gameObject;
                if (top.GetComponentInParent<Selectable>() != null) return false;
                if (top.GetComponentInParent<SketchbookFabric>() != null) return false;
                return true;
            }
            catch (Exception) { return true; }
        }

        private void AssignAtScreen(Vector2 screen, Fabric fabric)
        {
            int px, py, key;
            if (!_haveRef || !ScreenToPixel(screen, out px, out py)) return;
            if (!RegionAtPixel(px, py, CfgWholePiece.Value, out key))
            {
                if (CfgVerbose.Value) Logger.LogInfo("Drop outside the dress at " + px + "," + py);
                return;
            }
            Dictionary<int, string> map = CurrentMap(true);
            if (CfgWholePiece.Value)
            {
                // store every panel of the piece so switching the option later keeps working
                HashSet<int> keys = new HashSet<int>();
                for (int i = 0; i < _refPx.Length; i += 3)
                {
                    Color32 c = _refPx[i];
                    if (IsInterior(c) && RegionKey(c, true) == key) keys.Add(RegionKey(c, false));
                }
                foreach (int k in keys) map[k] = fabric.name;
            }
            else map[key] = fabric.name;
            Logger.LogInfo("Fabric " + fabric.name + " -> region " + key + " at " + px + "," + py);
            SaveStore();
            NeedRebuild = true;
        }

        private void RemoveAtPixel(int px, int py, string why)
        {
            int key;
            if (!_haveRef || !RegionAtPixel(px, py, false, out key)) return;
            Dictionary<int, string> map = CurrentMap(false);
            if (map == null) return;
            bool changed = false;
            if (CfgWholePiece.Value)
            {
                int piece = key >> 8;
                List<int> rm = new List<int>();
                foreach (int k in map.Keys) if ((k >> 8) == piece) rm.Add(k);
                foreach (int k in rm) { map.Remove(k); changed = true; }
            }
            else changed = map.Remove(key);
            if (!changed) return;
            Logger.LogInfo("Fabric removed from region " + key + " (" + why + ")");
            SaveStore();
            NeedRebuild = true;
        }

        private void ClearCurrentDesign()
        {
            if (_store.Remove(_designKey))
            {
                Logger.LogInfo("Fabrics cleared for this design (erase all)");
                SaveStore();
            }
            NeedRebuild = true;
        }

        // ================================================================== design key / store
        private static readonly FieldInfo FCurrentSlot = AccessTools.Field(typeof(PlayerProgress), "CurrentSlot");

        // Key of the dress shown in the sketchbook:
        //   slot<N> | dress:<thumbnailGuid>  or  #<index in active commissions> | <title> [| components]
        // The slot keeps save files apart; the guid/index keeps dresses with the same title apart
        // (all "Off rack dress" share a title).
        private string ComputeDesignKey()
        {
            string slot = "?", id = "", title = "?", comps = "";
            try
            {
                object s = FCurrentSlot != null ? FCurrentSlot.GetValue(null) : null;
                if (s != null) slot = s.ToString();
            }
            catch (Exception) { }
            if (!_featDesignKey) return Clean("slot" + slot + "|shared");
            try { id = DressId(); }
            catch (Exception) { }
            try
            {
                if (_sketchbook != null)
                {
                    object qt = FQuestTitle != null ? FQuestTitle.GetValue(_sketchbook) : null;
                    PropertyInfo tp = qt != null ? qt.GetType().GetProperty("text") : null;
                    string t = tp != null ? tp.GetValue(qt, null) as string : null;
                    if (t != null) title = t;
                    if (CfgPerComponents.Value)
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (string f in DesignFields)
                        {
                            FieldInfo fi = AccessTools.Field(typeof(Sketchbook), f);
                            object v = fi != null ? fi.GetValue(_sketchbook) : null;
                            UnityEngine.Object uo = v as UnityEngine.Object;
                            sb.Append('|').Append(uo != null ? uo.name : (v != null ? v.ToString() : "-"));
                        }
                        comps = sb.ToString();
                    }
                }
            }
            catch (Exception) { }
            string key = Clean("slot" + slot + "|" + id + "|" + title + comps);

            // only adopt old keys once a save is really loaded (id known), never from the transient
            // key computed on the title screen
            if (id.Length > 0 && !_store.ContainsKey(key))
            {
                // Adopt fabrics saved under an older key format for this same dress:
                //  - v2.0 keys had no slot/index: "|<title>" or "dress:<guid>|<title>"
                //  - the index key of this dress, once the game gives it a thumbnail guid
                List<string> legacy = new List<string>();
                legacy.Add(Clean("|" + title + comps));
                if (id.StartsWith("dress:")) legacy.Add(Clean(id + "|" + title + comps));
                if (id.StartsWith("dress:") && _prevKeySlotTitle == slot + "|" + title + comps && _prevKey.Contains("|#"))
                    legacy.Add(_prevKey);
                foreach (string old in legacy)
                {
                    if (old == key || !_store.ContainsKey(old)) continue;
                    _store[key] = _store[old];
                    _store.Remove(old);
                    SaveStore();
                    Logger.LogInfo("Fabrics moved from '" + old + "' to '" + key + "'");
                    break;
                }
            }
            _prevKey = key;
            _prevKeySlotTitle = slot + "|" + title + comps;
            return key;
        }
        private string _prevKey = "", _prevKeySlotTitle = "";

        // Direct game-field access lives in its own method: if a game update removes one of these
        // members, only this call fails (and is caught), not the whole caller.
        private static string DressId()
        {
            PlayerProgress pp = PlayerProgress.Current;
            QuestState qs = pp != null ? pp.currentQuestState : null;
            if (qs == null) return "";
            if (!string.IsNullOrEmpty(qs.thumbnailGuid)) return "dress:" + qs.thumbnailGuid;
            if (pp.activeCommissions != null) return "#" + pp.activeCommissions.IndexOf(qs);
            return "";
        }

        private static Texture FabricTextureField(Fabric f) { return f.texture; }

        private static string Clean(string s) { return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        private Dictionary<int, string> CurrentMap(bool create)
        {
            Dictionary<int, string> m;
            if (!_store.TryGetValue(_designKey, out m) && create)
            {
                m = new Dictionary<int, string>();
                _store[_designKey] = m;
            }
            return m;
        }

        private int CountAssignments()
        {
            int n = 0;
            foreach (Dictionary<int, string> m in _store.Values) n += m.Count;
            return n;
        }

        private void LoadStore()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                foreach (string line in File.ReadAllLines(_storePath, Encoding.UTF8))
                {
                    string[] p = line.Split('\t');
                    if (p.Length != 3) continue;
                    int k;
                    if (!int.TryParse(p[1], out k)) continue;
                    Dictionary<int, string> m;
                    if (!_store.TryGetValue(p[0], out m)) { m = new Dictionary<int, string>(); _store[p[0]] = m; }
                    m[k] = p[2];
                }
            }
            catch (Exception e) { Logger.LogWarning("Could not load assignments: " + e.Message); }
        }

        private void SaveStore()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath));
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, Dictionary<int, string>> d in _store)
                    foreach (KeyValuePair<int, string> e in d.Value)
                        sb.Append(d.Key).Append('\t').Append(e.Key).Append('\t').Append(e.Value).Append('\n');
                File.WriteAllText(_storePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { Logger.LogWarning("Could not save assignments: " + e.Message); }
        }

        // ================================================================== fabric pixels
        private Fabric FindFabric(string name)
        {
            Fabric r = null;
            if (_fabricsByName == null) _fabricsByName = new Dictionary<string, Fabric>();
            if (_fabricsByName.TryGetValue(name, out r) && r != null) return r;
            // rescan loaded fabrics (new ones appear when bought / loaded later); keep earlier hits
            foreach (Fabric f in Resources.FindObjectsOfTypeAll<Fabric>())
            {
                if (f == null) continue;
                Fabric old;
                // replace entries whose asset was unloaded/destroyed meanwhile
                if (!_fabricsByName.TryGetValue(f.name, out old) || old == null) _fabricsByName[f.name] = f;
            }
            if (_fabricsByName.TryGetValue(name, out r) && r != null) return r;
            // not loaded yet: ask the Fabric "enum" (static members like __cottonBlack / CottonBlack)
            try
            {
                foreach (MemberInfo mi in typeof(Fabric).GetMembers(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string mn = mi.Name.TrimStart('_');
                    if (!mn.Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) continue;
                    object v = null;
                    FieldInfo fi = mi as FieldInfo;
                    PropertyInfo pi = mi as PropertyInfo;
                    if (pi != null && pi.PropertyType == typeof(Fabric) && pi.GetIndexParameters().Length == 0) v = pi.GetValue(null, null);
                    else if (fi != null && fi.FieldType == typeof(Fabric)) v = fi.GetValue(null);
                    r = v as Fabric;
                    if (r != null) { _fabricsByName[name] = r; _fabricsByName[r.name] = r; return r; }
                }
            }
            catch (Exception e) { Logger.LogWarning("Fabric lookup " + name + ": " + e.Message); }
            return null;
        }

        private FabricTex GetFabricTex(string name)
        {
            FabricTex ft;
            if (_fabTex.TryGetValue(name, out ft)) return ft;
            ft = null;
            // a fabric that was not loaded yet is retried every few seconds instead of staying blank
            float retryAt;
            if (_fabMissingUntil.TryGetValue(name, out retryAt) && Time.unscaledTime < retryAt) return null;
            Fabric fab = FindFabric(name);
            Material m = fab != null ? fab.material : null;
            if (m != null)
            {
                ft = new FabricTex();
                Color tint = m.HasProperty("_Color") ? m.GetColor("_Color") : m.color;
                ft.tint = tint;

                // Scale: garment UVs are the panel's position on the cutting table in world units
                // (PatternPanel.UpdateUVs), and 1 unit ~ 1 metre. A floor-length sketch dress covers
                // ~70% of the page, so the page is ~2 m tall. Repeats per page = page metres * scale.
                float pageInSwatches = CfgPageMeters.Value;

                Texture print = null; Vector2 printScale = Vector2.one;
                if (m.HasProperty("_Albedo")) { print = m.GetTexture("_Albedo"); if (print != null) printScale = m.GetTextureScale("_Albedo"); }
                if (print == null && m.mainTexture != null) { print = m.mainTexture; printScale = m.mainTextureScale; }
                if (print == null)
                {
                    try { print = FabricTextureField(fab); } catch (Exception) { }
                }
                ft.printRepX = Mathf.Max(0.25f, pageInSwatches * Mathf.Abs(printScale.x) * CfgPrintScale.Value);
                ft.printRepY = Mathf.Max(0.25f, pageInSwatches * Mathf.Abs(printScale.y) * CfgPrintScale.Value);

                Texture weave = m.HasProperty("_Detail") ? m.GetTexture("_Detail") : null;
                Vector2 weaveScale = weave != null ? m.GetTextureScale("_Detail") : Vector2.one;
                ft.weaveRep = Mathf.Max(1f, pageInSwatches * Mathf.Abs(weaveScale.x) * CfgWeaveScale.Value);

                ft.printSrc = print != null ? ReadTexture(print, 512, out ft.psw, out ft.psh) : null;
                if (weave != null)
                {
                    Color32[] w = ReadTexture(weave, 256, out ft.wsw, out ft.wsh);
                    ft.weaveSrc = new float[w.Length];
                    for (int i = 0; i < w.Length; i++) ft.weaveSrc[i] = (w[i].r + w[i].g + w[i].b) / 765f;
                }

                // average colour (used to paint the game's own colour layer under the seams)
                float ar = 1f, ag = 1f, ab = 1f;
                if (ft.printSrc != null)
                {
                    double sr = 0, sg = 0, sb2 = 0;
                    foreach (Color32 p in ft.printSrc) { sr += p.r; sg += p.g; sb2 += p.b; }
                    int cnt = Math.Max(1, ft.printSrc.Length);
                    ar = (float)(sr / cnt / 255.0); ag = (float)(sg / cnt / 255.0); ab = (float)(sb2 / cnt / 255.0);
                }
                ft.average = new Color(tint.r * ar, tint.g * ag, tint.b * ab, 1f);

                // Real look: render with the game's own fabric material.
                // One world unit = pageInSwatches / density on the page (same scale as the flat mode).
                ft.unitsPerPage = Mathf.Max(0.1f, pageInSwatches * CfgPrintScale.Value);
                if (CfgRealShader.Value && _featLit)
                {
                    ft.lsw = 1024;
                    ft.litSrc = RenderLitSwatch(m, ft.lsw);
                    if (ft.litSrc != null && CfgDebugKeys.Value)
                    {
                        try
                        {
                            string dir = Path.Combine(Paths.BepInExRootPath, "RealFabricDebug");
                            Directory.CreateDirectory(dir);
                            Texture2D t2 = new Texture2D(ft.lsw, ft.lsw, TextureFormat.RGBA32, false);
                            t2.SetPixels32(ft.litSrc);
                            File.WriteAllBytes(Path.Combine(dir, "lit_" + name.Replace(' ', '_') + ".png"), ImageConversion.EncodeToPNG(t2));
                            Destroy(t2);
                        }
                        catch (Exception) { }
                    }
                    if (ft.litSrc != null)
                    {
                        double sr = 0, sg = 0, sb2 = 0;
                        for (int i = 0; i < ft.litSrc.Length; i += 13) { sr += ft.litSrc[i].r; sg += ft.litSrc[i].g; sb2 += ft.litSrc[i].b; }
                        double cnt = ft.litSrc.Length / 13;
                        ft.average = new Color((float)(sr / cnt / 255), (float)(sg / cnt / 255), (float)(sb2 / cnt / 255), 1f);
                    }
                }

                if (CfgVerbose.Value)
                    Logger.LogInfo("Fabric " + name + ": tint=" + tint + " print=" + (print != null ? print.name + " scale=" + printScale : "none")
                        + " weave=" + (weave != null ? weave.name + " scale=" + weaveScale : "none")
                        + " repeats/page=" + ft.printRepX.ToString("0.0") + "x" + ft.printRepY.ToString("0.0") + " weaveRep=" + ft.weaveRep.ToString("0.0"));
            }
            if (ft == null)
            {
                int tries;
                _fabMissingTries.TryGetValue(name, out tries);
                _fabMissingTries[name] = ++tries;
                if (tries == 1) Logger.LogWarning("Fabric not found (will retry): " + name);
                if (tries < 6)
                {
                    _fabMissingUntil[name] = Time.unscaledTime + 3f;
                    _retryRebuildAt = Time.unscaledTime + 3.1f;
                }
                else _fabMissingUntil[name] = Time.unscaledTime + 60f; // give up for a while (no stutter)
                return null;
            }
            _fabMissingUntil.Remove(name);
            _fabMissingTries.Remove(name);
            _fabTex[name] = ft;
            return ft;
        }
        private readonly Dictionary<string, float> _fabMissingUntil = new Dictionary<string, float>();
        private readonly Dictionary<string, int> _fabMissingTries = new Dictionary<string, int>();
        private float _retryRebuildAt = -1f;

        private static Color32[] ReadTexture(Texture tex, int maxSize, out int w, out int h)
        {
            w = Mathf.Min(tex.width, maxSize);
            h = Mathf.Min(tex.height, maxSize);
            RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, tmp);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = tmp;
            Texture2D t2 = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t2.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t2.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            Color32[] px = t2.GetPixels32();
            Destroy(t2);
            return px;
        }

        // ================================================================== overlay build
        private void Rebuild()
        {
            if (_refPx == null || _overlay == null) return;
            int W = _refW, H = _refH, n = W * H;
            if (_outPx == null || _outPx.Length != n) _outPx = new Color32[n];
            if (_overlayTex == null || _overlayTex.width != W || _overlayTex.height != H)
            {
                if (_overlayTex != null) Destroy(_overlayTex);
                _overlayTex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                _overlayTex.name = "RealFabricOverlayTex";
                _overlayTex.wrapMode = TextureWrapMode.Clamp;
            }

            Dictionary<int, string> map = CurrentMap(false);
            Color32 clear = new Color32(0, 0, 0, 0);
            if (map == null || map.Count == 0)
            {
                for (int i = 0; i < n; i++) _outPx[i] = clear;
            }
            else
            {
                Dictionary<int, FabricTex> byKey = new Dictionary<int, FabricTex>();
                foreach (KeyValuePair<int, string> e in map)
                {
                    FabricTex ft = GetFabricTex(e.Value);
                    if (ft != null) byKey[e.Key] = ft;
                }
                foreach (FabricTex ft in byKey.Values) BuildTiles(ft, W, H);
                byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(CfgOpacity.Value) * 255f);
                float weaveK = CfgWeaveStrength.Value;
                int lastKey = int.MinValue; FabricTex cur = null;
                int painted = 0;
                for (int y = 0; y < H; y++)
                {
                    float v = (float)y / H;
                    int row = y * W;
                    for (int x = 0; x < W; x++)
                    {
                        int i = row + x;
                        Color32 c = _refPx[i];
                        if (!IsInterior(c)) { _outPx[i] = clear; continue; }
                        int key = RegionKey(c, false);
                        if (key != lastKey) { lastKey = key; if (!byKey.TryGetValue(key, out cur)) cur = null; }
                        if (cur == null) { _outPx[i] = clear; continue; }
                        if (cur.lit != null)
                        {
                            Color32 l = cur.lit[(y % cur.lh) * cur.lw + (x % cur.lw)];
                            _outPx[i] = new Color32(l.r, l.g, l.b, alpha);
                            painted++;
                            continue;
                        }
                        float r = cur.tint.r, g = cur.tint.g, b = cur.tint.b;
                        if (cur.print != null)
                        {
                            Color32 p = cur.print[(y % cur.ph) * cur.pw + (x % cur.pw)];
                            r = r * p.r / 255f; g = g * p.g / 255f; b = b * p.b / 255f;
                        }
                        if (cur.weave != null && weaveK > 0f)
                        {
                            int wx = x % cur.ww;
                            int wy = y % cur.wh;
                            float f = 1f + (cur.weave[wy * cur.ww + wx] - 1f) * weaveK;
                            r *= f; g *= f; b *= f;
                        }
                        _outPx[i] = new Color32((byte)Mathf.Min(255f, r), (byte)Mathf.Min(255f, g), (byte)Mathf.Min(255f, b), alpha);
                        painted++;
                    }
                }
                // Grow the fabric 2 px into anti-aliased edge pixels of the same piece (they carry
                // in-between region colours and would otherwise show as a white fringe). Line pixels
                // (R channel) are left alone so the game's outlines stay visible.
                if (alpha > 0)
                {
                    if (_filled == null || _filled.Length != n) _filled = new bool[n];
                    for (int i = 0; i < n; i++) _filled[i] = _outPx[i].a != 0;
                    List<int> grown = new List<int>();
                    for (int pass = 0; pass < 2; pass++)
                    {
                        grown.Clear();
                        for (int y = 1; y < H - 1; y++)
                        {
                            int row = y * W;
                            for (int x = 1; x < W - 1; x++)
                            {
                                int i = row + x;
                                if (_filled[i]) continue;
                                Color32 c = _refPx[i];
                                if (c.a < 128 || c.r >= 128) continue;
                                int src = _filled[i - 1] ? i - 1 : _filled[i + 1] ? i + 1 : _filled[i - W] ? i - W : _filled[i + W] ? i + W : -1;
                                if (src < 0) continue;
                                _outPx[i] = _outPx[src];
                                grown.Add(i);
                            }
                        }
                        foreach (int i in grown) _filled[i] = true; // only grow 1 px per pass
                    }
                }
                if (CfgVerbose.Value) Logger.LogInfo("Overlay rebuilt: " + map.Count + " regions, " + painted + " px");
            }
            SyncGameColourLayer(map);
            _overlayTex.SetPixels32(_outPx);
            _overlayTex.Apply(false);
            _overlay.texture = _overlayTex;
        }

        // ================================================================== game colour layer
        // The game draws seams, folds and edges over its own colour layer (_colourRt). We fill the
        // fabric regions there with the fabric's average colour using the game's own flood fill, so
        // those details are drawn over the fabric colour instead of over white paper.
        // These fills are NOT added to the game's click list, so nothing extra is saved in the save file.
        private static readonly MethodInfo MExecuteFill = AccessTools.Method(typeof(SketchbookSprite), "ExecuteFill");
        private static readonly MethodInfo MClearColour = AccessTools.Method(typeof(SketchbookSprite), "ClearColourTexture");
        private readonly List<KeyValuePair<Vector2Int, Color>> _seeds = new List<KeyValuePair<Vector2Int, Color>>();
        private bool _seedsApplied;
        private static bool _inColourSync;
        internal static volatile bool NeedColourApply;

        public static void OnClearColour()
        {
            if (_inColourSync || Instance == null || Instance._disabled) return;
            NeedColourApply = true; // the game wiped its layer: put our fabric colours back
        }

        private void SyncGameColourLayer(Dictionary<int, string> map)
        {
            _seeds.Clear();
            if (CfgFillGameLayer.Value && _featSeams && map != null && map.Count > 0)
            {
                // one seed per connected component of every assigned region (left and right
                // sleeve share a region id but are separate areas)
                int W = _refW, H = _refH;
                int[] label = new int[W * H];
                Stack<int> stack = new Stack<int>();
                int comp = 0;
                for (int start = 0; start < label.Length; start++)
                {
                    if (label[start] != 0) continue;
                    Color32 c0 = _refPx[start];
                    if (!IsInterior(c0)) { label[start] = -1; continue; }
                    int key = RegionKey(c0, false);
                    string fab;
                    if (!map.TryGetValue(key, out fab)) { label[start] = -1; continue; }
                    comp++;
                    int size = 0, best = -1, bestScore = -1;
                    stack.Push(start); label[start] = comp;
                    while (stack.Count > 0)
                    {
                        int i = stack.Pop(); size++;
                        int x = i % W, y = i / W;
                        // prefer a seed well inside the area
                        if (size % 7 == 0 && x > 3 && y > 3 && x < W - 4 && y < H - 4)
                        {
                            int score = 0;
                            for (int d = 1; d <= 3; d++)
                            {
                                if (SameKey(i + d, key)) score++; if (SameKey(i - d, key)) score++;
                                if (SameKey(i + d * W, key)) score++; if (SameKey(i - d * W, key)) score++;
                            }
                            if (score > bestScore) { bestScore = score; best = i; }
                        }
                        if (x > 0) Visit(i - 1, key, comp, label, stack);
                        if (x < W - 1) Visit(i + 1, key, comp, label, stack);
                        if (y > 0) Visit(i - W, key, comp, label, stack);
                        if (y < H - 1) Visit(i + W, key, comp, label, stack);
                    }
                    if (size < 150 || best < 0) continue;
                    FabricTex ft = GetFabricTex(fab);
                    if (ft == null) continue;
                    _seeds.Add(new KeyValuePair<Vector2Int, Color>(new Vector2Int(best % W, best / W), ft.average));
                }
            }
            if (_seeds.Count > 0 || _seedsApplied) NeedColourApply = true;
        }

        private bool SameKey(int i, int key)
        {
            if (i < 0 || i >= _refPx.Length) return false;
            Color32 c = _refPx[i];
            return IsInterior(c) && RegionKey(c, false) == key;
        }

        private void Visit(int i, int key, int comp, int[] label, Stack<int> stack)
        {
            if (label[i] != 0) return;
            Color32 c = _refPx[i];
            if (!IsInterior(c) || RegionKey(c, false) != key) return; // other pixels stay 0: they may start their own component
            label[i] = comp;
            stack.Push(i);
        }

        private void ApplyGameColourLayer()
        {
            if (!_featSeams || _sprite == null || MExecuteFill == null || MClearColour == null) return;
            _inColourSync = true;
            try
            {
                // reset to the game's own fills (pencil colours), then add the fabric colours
                MClearColour.Invoke(_sprite, new object[] { false });
                foreach (KeyValuePair<Vector2Int, Color> s in _seeds)
                    MExecuteFill.Invoke(_sprite, new object[] { s.Key.x, s.Key.y, s.Value });
                _seedsApplied = _seeds.Count > 0;
            }
            catch (Exception e) { Logger.LogWarning("Colour layer sync: " + e.Message); }
            finally { _inColourSync = false; }
        }

        // ================================================================== debug
        private float _nextCmdCheck;

        // Test commands from BepInEx\RealFabricDebug\cmd.txt (one per line):
        //   open | paint | dump | clear | toggle | drop <FabricAssetName> <screenX> <screenY> | erase <screenX> <screenY>
        private void DebugCommands()
        {
            if (Time.unscaledTime < _nextCmdCheck) return;
            _nextCmdCheck = Time.unscaledTime + 0.5f;
            string path = Path.Combine(Path.Combine(Paths.BepInExRootPath, "RealFabricDebug"), "cmd.txt");
            if (!File.Exists(path)) return;
            string[] lines;
            try { lines = File.ReadAllLines(path); File.Delete(path); } catch (Exception) { return; }
            foreach (string raw in lines)
            {
                string[] a = raw.Trim().Split(' ');
                if (a.Length == 0 || a[0].Length == 0) continue;
                Logger.LogInfo("cmd: " + raw.Trim());
                try
                {
                    switch (a[0])
                    {
                        case "open": DebugOpen(); break;
                        case "paint": DebugPaint(); break;
                        case "dump": DebugDump(); break;
                        case "clear": ClearCurrentDesign(); break;
                        case "toggle": _userHidden = !_userHidden; break;
                        case "drop":
                            {
                                Fabric f = FindFabric(a[1]);
                                if (f == null) { Logger.LogWarning("no fabric " + a[1]); break; }
                                AssignAtScreen(new Vector2(float.Parse(a[2]), float.Parse(a[3])), f);
                                break;
                            }
                        case "erase":
                            {
                                int px, py;
                                if (ScreenToPixel(new Vector2(float.Parse(a[1]), float.Parse(a[2])), out px, out py)) RemoveAtPixel(px, py, "cmd");
                                break;
                            }
                    }
                }
                catch (Exception e) { Logger.LogError("cmd failed: " + e); }
            }
        }

        private void DebugOpen()
        {
            try
            {
                GameManager gm = UnityEngine.Object.FindAnyObjectByType<GameManager>(FindObjectsInactive.Include);
                if (gm != null) AccessTools.Method(typeof(GameManager), "SetSceneSketchbook").Invoke(gm, null);
            }
            catch (Exception e) { Logger.LogError("Debug open: " + e); }
        }

        private void DebugKeys()
        {
            DebugCommands();
            bool dump = false, open = false, paint = false;
            try { dump = Input.GetKeyDown(KeyCode.F10); open = Input.GetKeyDown(KeyCode.F11); paint = Input.GetKeyDown(KeyCode.F12); } catch (Exception) { }
            if (open) DebugOpen();
            if (paint) DebugPaint();
            if (dump) DebugDump();
        }

        private void DebugPaint()
        {
            if (_haveRef)
            {
                // assign the inventory fabrics to every region, alternating
                List<Fabric> fabs = new List<Fabric>();
                foreach (InventoryItem it in Resources.FindObjectsOfTypeAll<InventoryItem>())
                {
                    if (it == null || !it.gameObject.scene.IsValid()) continue;
                    foreach (FieldInfo fi in typeof(InventoryItem).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        Fabric f = fi.FieldType == typeof(Fabric) ? fi.GetValue(it) as Fabric : null;
                        if (f != null && !fabs.Contains(f)) fabs.Add(f);
                    }
                }
                if (fabs.Count == 0) { Logger.LogWarning("No inventory fabrics"); return; }
                SortedDictionary<int, int> keys = new SortedDictionary<int, int>();
                for (int i = 0; i < _refPx.Length; i += 2) if (IsInterior(_refPx[i])) { int k = RegionKey(_refPx[i], false); int c; keys.TryGetValue(k, out c); keys[k] = c + 1; }
                Dictionary<int, string> map = CurrentMap(true);
                int j = 0;
                foreach (KeyValuePair<int, int> kv in keys)
                {
                    if (kv.Value < 200) continue; // skip anti-aliasing noise
                    map[kv.Key] = fabs[j++ % fabs.Count].name;
                }
                Logger.LogInfo("Debug paint: " + j + " regions with " + fabs.Count + " fabrics");
                SaveStore();
                NeedRebuild = true;
            }
        }

        private void DebugDump()
        {
            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "RealFabricDebug");
                Directory.CreateDirectory(dir);
                if (_sprite != null) SavePng(FRefRt.GetValue(_sprite) as Texture, Path.Combine(dir, "refRt.png"));
                if (_overlayTex != null) SavePng(_overlayTex, Path.Combine(dir, "overlay.png"));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("design=" + _designKey);
                Dictionary<int, string> map = CurrentMap(false);
                if (map != null) foreach (KeyValuePair<int, string> e in map) sb.AppendLine("  region " + e.Key + " -> " + e.Value);
                File.WriteAllText(Path.Combine(dir, "info.txt"), sb.ToString());
                Logger.LogInfo("Debug dump written to " + dir);
            }
            catch (Exception e) { Logger.LogError("Debug dump failed: " + e); }
        }

        private static void SavePng(Texture tex, string path)
        {
            if (tex == null) return;
            int w, h;
            Color32[] px = ReadTexture(tex, 2048, out w, out h);
            Texture2D t2 = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t2.SetPixels32(px);
            File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t2));
            Destroy(t2);
        }

        private void OnDestroy()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }
    }
}
