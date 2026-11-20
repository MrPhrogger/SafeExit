// Reads gasSim.CurrentState when it exists, otherwise the private _esp32.State of GasSafetySimulation (by reflection).
using System.Reflection;
using UnityEngine;

public class BuildingAccessSimulation : MonoBehaviour
{
    [Header("Link")]
    [SerializeField] private GasSafetySimulation gasSim;

    [Header("Simulation")]
    public int peopleInside = 3;
    public int maxPeople = 99;
    public bool autoTraffic;
    public float autoInterval = 2.5f;

    [Header("Scene objects (built by 'Build Access Scene')")]
    [SerializeField] private GameObject _accessRoot;
    [SerializeField] private TextMesh[] _lcdLines = new TextMesh[4];
    [SerializeField] private Renderer _lcdScreen;
    [SerializeField] private Renderer _readerLed;
    [SerializeField] private Transform _card;
    [SerializeField] private Vector3 _cardHome, _cardTap;

    private bool _menuOpen = true;
    private string _lastEvent = "READY - SCAN CARD";
    private string _state;
    private bool _hazard;
    private float _scanT = 1f, _autoTimer, _nextFind;
    private bool _scanEntry, _scanApplied, _scanOk;
    private bool _resolved;
    private PropertyInfo _stateProp;
    private FieldInfo _stateField, _espField, _espStateField;
    private Material _matSource;

    sealed class Mats
    {
        public Material Platform, Panel, Board, PcbBlue, PcbBlack, Silver, Gold, Black, Gray, White, Tan, Stand,
                        LedRed, LedBlue, Bezel, Card, CardStripe, WireRed, WireBlack, WireYellow, WireGreen, WireWhite, WireBlue, WireOrange;
    }

    // ───────────── Lifecycle ─────────────

    void Reset()
    {
        gasSim = FindFirstObjectByType<GasSafetySimulation>();
        BuildAccessScene();
    }

    void Start()
    {
        if (gasSim == null) gasSim = FindFirstObjectByType<GasSafetySimulation>();
        bool missing = _accessRoot == null || _lcdScreen == null || _readerLed == null || _card == null;
        if (!missing)
            for (int i = 0; i < 4; i++)
                if (_lcdLines == null || _lcdLines.Length < 4 || _lcdLines[i] == null) missing = true;
        if (missing) BuildAccessScene();
    }

    void Update()
    {
        _state = ReadState();
        _hazard = _state == "Fire" || _state == "GasLeak" || _state == "SmokeDetected";
        StepAuto();
        StepScan();
        UpdateLcd();
    }

    // ───────────── Link to GasSafetySimulation ─────────────

    string ReadState()
    {
        if (gasSim == null)
        {
            if (Time.unscaledTime < _nextFind) return null;
            _nextFind = Time.unscaledTime + 1f;
            gasSim = FindFirstObjectByType<GasSafetySimulation>();
            _resolved = false;
            if (gasSim == null) return null;
        }
        const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (!_resolved)
        {
            _resolved = true;
            var t = gasSim.GetType();
            _stateProp = t.GetProperty("CurrentState", f);
            _stateField = _stateProp == null ? t.GetField("CurrentState", f) : null;
            _espField = (_stateProp == null && _stateField == null) ? t.GetField("_esp32", f) : null;
            _espStateField = null;
        }
        object v = null;
        if (_stateProp != null) v = _stateProp.GetValue(gasSim);
        else if (_stateField != null) v = _stateField.GetValue(gasSim);
        else if (_espField != null)
        {
            object esp = _espField.GetValue(gasSim);
            if (esp != null)
            {
                if (_espStateField == null) _espStateField = esp.GetType().GetField("State", f);
                if (_espStateField != null) v = _espStateField.GetValue(esp);
            }
        }
        return v != null ? v.ToString() : null;
    }

    static string Pretty(string s)
    {
        switch (s)
        {
            case null: return "NO LINK";
            case "Ok": return "OK";
            case "Malfunction": return "MALFUNCTION";
            case "GasLeak": return "GAS LEAK";
            case "SmokeDetected": return "SMOKE";
            case "Fire": return "FIRE";
            default: return s.ToUpperInvariant();
        }
    }

    // ───────────── Scans ─────────────

    public void TriggerScan(bool entry)
    {
        if (_scanT < 1f) return;
        _scanEntry = entry;
        _scanApplied = false;
        _scanT = 0f;
    }

    void StepAuto()
    {
        if (!autoTraffic) return;
        _autoTimer += Time.deltaTime;
        if (_autoTimer < (_hazard ? autoInterval * 0.6f : autoInterval)) return;
        _autoTimer = 0f;
        if (_hazard && peopleInside == 0) return;
        bool entry = !_hazard && (peopleInside < 2 || (peopleInside < 10 && Random.value < 0.55f));
        TriggerScan(entry);
    }

    void StepScan()
    {
        if (_scanT < 1f) _scanT = Mathf.Min(1f, _scanT + Time.deltaTime / 1.4f);

        if (!_scanApplied && _scanT >= 0.45f)
        {
            _scanApplied = true;
            if (_scanEntry)
            {
                _scanOk = !_hazard;
                if (_scanOk)
                {
                    peopleInside = Mathf.Min(maxPeople, peopleInside + 1);
                    _lastEvent = "LAST: ENTRY";
                }
            }
            else
            {
                _scanOk = peopleInside > 0;
                if (_scanOk)
                {
                    peopleInside--;
                    _lastEvent = "LAST: EXIT";
                }
            }
        }

        if (_card != null && _accessRoot != null)
        {
            float k = _scanT < 0.4f ? Smooth(_scanT / 0.4f) : _scanT < 0.6f ? 1f : Smooth(1f - (_scanT - 0.6f) / 0.4f);
            if (_scanT >= 1f) k = 0f;
            _card.localPosition = Vector3.Lerp(_cardHome, _cardTap, k);
            _card.localRotation = Quaternion.Euler(0f, -20f * (1f - k), 0f);
        }

        if (_readerLed != null)
        {
            bool active = _scanT >= 0.4f && _scanT < 0.85f;
            Color c = _scanOk ? new Color(0.1f, 1f, 0.2f) : new Color(1f, 0.1f, 0.08f);
            if (!_scanApplied) c = new Color(0.1f, 1f, 0.2f);
            SetGlow(_readerLed, c, active ? 1f : 0.15f);
        }
    }

    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

    static void SetGlow(Renderer r, Color baseColor, float level)
    {
        var m = r.material;
        m.EnableKeyword("_EMISSION");
        m.color = Color.Lerp(baseColor * 0.15f, baseColor, level);
        Color e = baseColor * (level * 2.5f);
        e.a = 1f;
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", e);
        if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", e);
    }

    // ───────────── LCD ─────────────

    void SetLine(int i, string text)
    {
        if (_lcdLines == null || i >= _lcdLines.Length || _lcdLines[i] == null) return;
        if (_lcdLines[i].text != text) _lcdLines[i].text = text;
    }

    void UpdateLcd()
    {
        string l1, l2;
        Color back;
        if (_hazard)
        {
            if (peopleInside > 0)
            {
                bool textOn = Mathf.Repeat(Time.time, 1f) < 0.6f;
                bool red = Mathf.Repeat(Time.time, 0.6f) < 0.3f;
                l1 = textOn ? "EVACUATE NOW!" : "";
                l2 = "INSIDE: " + peopleInside + " - EXIT!";
                back = red ? new Color(0.95f, 0.1f, 0.05f) : new Color(1f, 0.65f, 0.05f);
            }
            else
            {
                l1 = "AREA SAFE";
                l2 = "NO ONE INSIDE";
                back = new Color(0.85f, 0.95f, 0.15f);
            }
        }
        else
        {
            l1 = _lastEvent;
            l2 = "INSIDE: " + peopleInside + " PEOPLE";
            back = new Color(0.1f, 0.9f, 0.65f);
        }

        string l4 = _hazard ? (peopleInside > 0 ? "GATE: EXIT ONLY" : "GATE: LOCKED") : "GATE: OPEN";
        SetLine(0, l1);
        SetLine(1, l2);
        SetLine(2, "GAS SYS: " + Pretty(_state));
        SetLine(3, l4);
        if (_lcdScreen != null) SetGlow(_lcdScreen, back, 0.8f);
    }

    // ───────────── GUI ─────────────

    void OnGUI()
    {
        GUI.skin.button.fontSize = 13;
        if (!_menuOpen)
        {
            if (GUI.Button(new Rect(Screen.width - 160, 10, 150, 30), "Show access menu")) _menuOpen = true;
            return;
        }

        var rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 13 };

        GUILayout.BeginArea(new Rect(Screen.width - 340, 10, 330, 360), GUI.skin.box);
        if (GUILayout.Button("Hide menu")) _menuOpen = false;
        GUILayout.Label("<b>Building access (ESP32 gate)</b>", rich);
        string col = _hazard ? "#ff5555" : _state == "Malfunction" ? "#ffe033" : "#44ff44";
        GUILayout.Label($"Gas system: <color={col}>{Pretty(_state)}</color>", rich);
        GUILayout.Label($"Inside: <b>{peopleInside}</b>     {_lastEvent}", rich);

        GUILayout.Space(4);
        if (GUILayout.Button("Simulate Entry Scan", GUILayout.Height(28))) TriggerScan(true);
        if (GUILayout.Button("Simulate Exit Scan", GUILayout.Height(28))) TriggerScan(false);
        if (GUILayout.Button("Toggle Auto-Traffic Demo: " + (autoTraffic ? "ON" : "OFF"), GUILayout.Height(28))) autoTraffic = !autoTraffic;

        GUILayout.Space(4);
        GUILayout.Label("Occupancy: " + peopleInside, rich);
        peopleInside = Mathf.RoundToInt(GUILayout.HorizontalSlider(peopleInside, 0, 30));
        if (GUILayout.Button("Reset occupancy (3)")) peopleInside = 3;

        var hint = new GUIStyle(rich) { fontSize = 11 };
        hint.normal.textColor = new Color(0.78f, 0.78f, 0.78f);
        GUILayout.Label("Entries are refused while a hazard is active.", hint);
        GUILayout.EndArea();
    }

    // ───────────── Scene building ─────────────

    [ContextMenu("Build Access Scene")]
    public void BuildAccessScene()
    {
        ClearAccessScene();

        var rootGo = new GameObject("AccessScene");
        rootGo.transform.SetParent(transform, true);
        rootGo.transform.position = transform.position + new Vector3(7.5f, 0f, 0f);
        _accessRoot = rootGo;
        Transform root = rootGo.transform;
        var m = MakeMats();

        Part(PrimitiveType.Cube, "Platform", root, new Vector3(0f, -0.1f, 0f), new Vector3(5.6f, 0.2f, 4f), m.Platform);
        Part(PrimitiveType.Cube, "BackPanel", root, new Vector3(0f, 1.8f, 1.95f), new Vector3(5.6f, 3.6f, 0.1f), m.Panel);

        Vector3 esp = new Vector3(-1.5f, 0f, -0.6f);
        Vector3 rfid = new Vector3(1.3f, 0f, -0.2f);
        BuildEsp32(root, esp, m);
        BuildRfid(root, rfid, m);
        BuildLcd(root, m, out Vector3[] lcdPins);
        BuildCard(root, rfid, m);
        BuildWires(root, esp, rfid, lcdPins, m);

        Label("ESP32 (Access Control)", root, esp + new Vector3(0f, 0.75f, 0f));
        Label("RFID-RC522", root, rfid + new Vector3(0f, 1.65f, 0f));
        Label("LCD Display", root, new Vector3(0f, 3.3f, 1.8f));
        Label("RFID Keycard", root, _cardHome + new Vector3(0f, 0.45f, 0f));

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.RegisterCreatedObjectUndo(rootGo, "Build access scene");
            UnityEditor.EditorUtility.SetDirty(this);
            if (gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif
    }

    [ContextMenu("Clear Access Scene")]
    public void ClearAccessScene()
    {
        if (_accessRoot != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.DestroyObjectImmediate(_accessRoot);
            else Destroy(_accessRoot);
#else
            Destroy(_accessRoot);
#endif
        }
        _accessRoot = null;
        _lcdLines = new TextMesh[4];
        _lcdScreen = null;
        _readerLed = null;
        _card = null;
    }

    static void Kill(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    Material NewMat(string matName, Color color, float metallic, float smoothness, bool glow = false)
    {
        if (_matSource == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _matSource = tmp.GetComponent<Renderer>().sharedMaterial;
            Kill(tmp);
        }
        var mat = new Material(_matSource) { name = matName, color = color };
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (glow)
        {
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", color * 2f);
        }
        return mat;
    }

    Mats MakeMats()
    {
        return new Mats
        {
            Platform = NewMat("Acc_Platform", new Color(0.25f, 0.25f, 0.28f), 0f, 0.3f),
            Panel = NewMat("Acc_Panel", new Color(0.16f, 0.17f, 0.2f), 0f, 0.3f),
            Board = NewMat("Acc_Breadboard", new Color(0.9f, 0.9f, 0.86f), 0f, 0.35f),
            PcbBlue = NewMat("Acc_PCB_Blue", new Color(0.05f, 0.2f, 0.55f), 0f, 0.7f),
            PcbBlack = NewMat("Acc_PCB_Black", new Color(0.03f, 0.03f, 0.04f), 0f, 0.65f),
            Silver = NewMat("Acc_Silver", new Color(0.78f, 0.78f, 0.8f), 1f, 0.75f),
            Gold = NewMat("Acc_Gold", new Color(0.9f, 0.72f, 0.2f), 1f, 0.7f),
            Black = NewMat("Acc_Black", new Color(0.02f, 0.02f, 0.025f), 0f, 0.45f),
            Gray = NewMat("Acc_Gray", new Color(0.35f, 0.36f, 0.38f), 0f, 0.4f),
            White = NewMat("Acc_White", new Color(0.92f, 0.92f, 0.9f), 0f, 0.3f),
            Tan = NewMat("Acc_Tan", new Color(0.72f, 0.56f, 0.28f), 0f, 0.4f),
            Stand = NewMat("Acc_Dark", new Color(0.1f, 0.1f, 0.12f), 0f, 0.4f),
            LedRed = NewMat("Acc_LedRed", new Color(1f, 0.1f, 0.1f), 0f, 0.8f, true),
            LedBlue = NewMat("Acc_LedBlue", new Color(0.2f, 0.4f, 1f), 0f, 0.8f, true),
            Bezel = NewMat("Acc_Bezel", new Color(0.04f, 0.04f, 0.05f), 0f, 0.5f),
            Card = NewMat("Acc_Card", new Color(0.93f, 0.93f, 0.96f), 0f, 0.6f),
            CardStripe = NewMat("Acc_CardStripe", new Color(0.1f, 0.35f, 0.9f), 0f, 0.6f),
            WireRed = NewMat("Acc_WireRed", new Color(0.85f, 0.1f, 0.1f), 0f, 0.5f),
            WireBlack = NewMat("Acc_WireBlack", new Color(0.05f, 0.05f, 0.05f), 0f, 0.5f),
            WireYellow = NewMat("Acc_WireYellow", new Color(0.95f, 0.8f, 0.1f), 0f, 0.5f),
            WireGreen = NewMat("Acc_WireGreen", new Color(0.1f, 0.7f, 0.2f), 0f, 0.5f),
            WireWhite = NewMat("Acc_WireWhite", new Color(0.9f, 0.9f, 0.9f), 0f, 0.5f),
            WireBlue = NewMat("Acc_WireBlue", new Color(0.15f, 0.35f, 0.95f), 0f, 0.5f),
            WireOrange = NewMat("Acc_WireOrange", new Color(0.95f, 0.5f, 0.1f), 0f, 0.5f),
        };
    }

    GameObject Part(PrimitiveType type, string pName, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = pName;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        var col = go.GetComponent<Collider>();
        if (col != null) Kill(col);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    GameObject PartZ(string pName, Transform parent, Vector3 localPos, float diameter, float length, Material mat)
    {
        var go = Part(PrimitiveType.Cylinder, pName, parent, localPos, new Vector3(diameter, length * 0.5f, diameter), mat);
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        return go;
    }

    void Segment(string sName, Transform parent, Vector3 a, Vector3 b, Material mat)
    {
        const float thick = 0.02f;
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.001f) return;
        var go = Part(PrimitiveType.Cube, sName, parent, (a + b) * 0.5f, new Vector3(thick, thick, len + thick), mat);
        go.transform.localRotation = Quaternion.LookRotation(d);
    }

    TextMesh MakeText(string tName, Transform parent, Vector3 localPos, string text, float charSize, Color color, TextAnchor anchor)
    {
        var go = new GameObject(tName);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 64;
        tm.characterSize = charSize;
        tm.anchor = anchor;
        tm.alignment = TextAlignment.Center;
        tm.color = color;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        return tm;
    }

    void Label(string text, Transform parent, Vector3 localPos)
    {
        var go = new GameObject("Label_" + text.Replace(" ", ""));
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        const float size = 0.032f, outline = 0.01f;
        for (int k = 0; k < 8; k++)
        {
            float ang = k * Mathf.PI / 4f;
            var oc = new GameObject("Outline" + k);
            oc.transform.SetParent(go.transform, false);
            oc.transform.localPosition = new Vector3(Mathf.Cos(ang) * outline, Mathf.Sin(ang) * outline, 0.02f);
            var ot = oc.AddComponent<TextMesh>();
            ot.text = text; ot.font = font; ot.fontSize = 64; ot.characterSize = size;
            ot.anchor = TextAnchor.LowerCenter; ot.alignment = TextAlignment.Center;
            ot.color = new Color(0.02f, 0.02f, 0.03f);
            oc.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
        var tm = go.AddComponent<TextMesh>();
        tm.text = text; tm.font = font; tm.fontSize = 64; tm.characterSize = size;
        tm.anchor = TextAnchor.LowerCenter; tm.alignment = TextAlignment.Center;
        tm.color = Color.white;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
    }

    // ───────────── Components ─────────────

    const float EspPinX0 = -0.6f, EspPinStep = 0.0857f;
    static float EspPinX(Vector3 esp, int i) { return esp.x + EspPinX0 + EspPinStep * i; }

    void BuildEsp32(Transform root, Vector3 b, Mats m)
    {
        var t = new GameObject("ESP32").transform;
        t.SetParent(root, false);
        t.localPosition = b;
        Vector3 P(float x, float h, float z) { return new Vector3(x, 0.12f + h * 0.5f, z); }

        Part(PrimitiveType.Cube, "ESP32_Breadboard", t, new Vector3(0f, 0.04f, 0f), new Vector3(1.9f, 0.08f, 1.0f), m.Board);
        Part(PrimitiveType.Cube, "ESP32_BreadboardGroove", t, new Vector3(0f, 0.081f, 0f), new Vector3(1.8f, 0.004f, 0.05f), m.Stand);
        Part(PrimitiveType.Cube, "ESP32_PCB", t, new Vector3(0f, 0.10f, 0f), new Vector3(1.45f, 0.04f, 0.72f), m.PcbBlack);

        Part(PrimitiveType.Cube, "ESP32_Shield", t, P(0.2f, 0.05f, 0f), new Vector3(0.5f, 0.05f, 0.42f), m.Silver);
        Part(PrimitiveType.Cube, "ESP32_AntennaBoard", t, P(0.575f, 0.012f, 0f), new Vector3(0.25f, 0.012f, 0.42f), m.PcbBlack);
        for (int i = 0; i < 5; i++)
            Part(PrimitiveType.Cube, "ESP32_AntennaTrace" + i, t, P(0.575f + ((i % 2 == 0) ? 0.02f : -0.02f), 0.02f, -0.16f + 0.08f * i),
                 new Vector3(0.16f, 0.004f, 0.018f), m.Gold);
        var shieldText = MakeText("ESP32_ShieldText", t, new Vector3(0.2f, 0.173f, 0f), "ESP-WROOM-32", 0.011f, new Color(0.1f, 0.1f, 0.12f), TextAnchor.MiddleCenter);
        shieldText.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        Part(PrimitiveType.Cube, "ESP32_MicroUSB", t, P(-0.68f, 0.07f, 0f), new Vector3(0.2f, 0.07f, 0.28f), m.Silver);
        Part(PrimitiveType.Cube, "ESP32_MicroUSB_Opening", t, P(-0.78f, 0.04f, 0f), new Vector3(0.01f, 0.035f, 0.18f), m.Black);
        Part(PrimitiveType.Cube, "ESP32_UsbChip", t, P(-0.42f, 0.03f, 0.1f), new Vector3(0.14f, 0.03f, 0.14f), m.Black);
        for (int i = 0; i < 2; i++)
        {
            float z = (i == 0) ? -0.25f : 0.25f;
            Part(PrimitiveType.Cube, "ESP32_Button" + i, t, P(-0.55f, 0.04f, z), new Vector3(0.1f, 0.04f, 0.1f), m.Black);
            Part(PrimitiveType.Cylinder, "ESP32_ButtonCap" + i, t, new Vector3(-0.55f, 0.172f, z), new Vector3(0.06f, 0.012f, 0.06f), m.Gray);
        }
        Part(PrimitiveType.Cube, "ESP32_Regulator", t, P(-0.3f, 0.04f, -0.2f), new Vector3(0.1f, 0.04f, 0.08f), m.Black);
        for (int i = 0; i < 3; i++)
            Part(PrimitiveType.Cube, "ESP32_Capacitor" + i, t, P(-0.3f + 0.09f * i, 0.04f, 0.2f), new Vector3(0.05f, 0.04f, 0.05f), m.Tan);
        Part(PrimitiveType.Cube, "ESP32_LedPower", t, P(-0.2f, 0.02f, 0.28f), new Vector3(0.05f, 0.02f, 0.05f), m.LedRed);
        Part(PrimitiveType.Cube, "ESP32_LedStatus", t, P(-0.12f, 0.02f, 0.28f), new Vector3(0.05f, 0.02f, 0.05f), m.LedBlue);

        for (int i = 0; i < 15; i++)
        {
            float x = EspPinX0 + EspPinStep * i;
            Part(PrimitiveType.Cube, "ESP32_PinFront" + i, t, P(x, 0.07f, -0.31f), new Vector3(0.03f, 0.07f, 0.03f), m.Gold);
            Part(PrimitiveType.Cube, "ESP32_PinBack" + i, t, P(x, 0.07f, 0.31f), new Vector3(0.03f, 0.07f, 0.03f), m.Gold);
        }
    }

    static float RfidPinX(Vector3 r, int n) { return r.x - 0.35f + 0.1f * n; }

    void BuildRfid(Transform root, Vector3 r, Mats m)
    {
        var t = new GameObject("RFID_RC522").transform;
        t.SetParent(root, false);
        t.localPosition = r;
        Vector3 c = new Vector3(0f, 0.82f, 0f);

        Part(PrimitiveType.Cube, "RFID_FootL", t, new Vector3(-0.35f, 0.07f, 0f), new Vector3(0.1f, 0.14f, 0.22f), m.Stand);
        Part(PrimitiveType.Cube, "RFID_FootR", t, new Vector3(0.35f, 0.07f, 0f), new Vector3(0.1f, 0.14f, 0.22f), m.Stand);
        Part(PrimitiveType.Cube, "RFID_PCB", t, c, new Vector3(0.9f, 1.3f, 0.04f), m.PcbBlue);

        for (int k = 0; k < 4; k++)
        {
            float w = 0.7f - 0.12f * k, h = 1.0f - 0.12f * k;
            Vector3 cc = c + new Vector3(0f, 0.05f, -0.03f);
            Part(PrimitiveType.Cube, "RFID_Coil" + k + "_L", t, cc + new Vector3(-w * 0.5f, 0f, 0f), new Vector3(0.03f, h, 0.012f), m.White);
            Part(PrimitiveType.Cube, "RFID_Coil" + k + "_R", t, cc + new Vector3(w * 0.5f, 0f, 0f), new Vector3(0.03f, h, 0.012f), m.White);
            Part(PrimitiveType.Cube, "RFID_Coil" + k + "_T", t, cc + new Vector3(0f, h * 0.5f, 0f), new Vector3(w, 0.03f, 0.012f), m.White);
            Part(PrimitiveType.Cube, "RFID_Coil" + k + "_B", t, cc + new Vector3(0f, -h * 0.5f, 0f), new Vector3(w, 0.03f, 0.012f), m.White);
        }

        Part(PrimitiveType.Cube, "RFID_Chip_MFRC522", t, c + new Vector3(-0.25f, -0.5f, -0.03f), new Vector3(0.22f, 0.12f, 0.03f), m.Black);
        Part(PrimitiveType.Cube, "RFID_Crystal", t, c + new Vector3(0.1f, -0.5f, -0.03f), new Vector3(0.16f, 0.07f, 0.03f), m.Silver);
        var ledMat = NewMat("Acc_ReaderLed", new Color(0.1f, 1f, 0.2f), 0f, 0.8f, true);
        _readerLed = Part(PrimitiveType.Cube, "RFID_Led", t, c + new Vector3(0.33f, -0.5f, -0.03f), new Vector3(0.08f, 0.06f, 0.02f), ledMat).GetComponent<Renderer>();

        Part(PrimitiveType.Cube, "RFID_Header", t, c + new Vector3(0f, -0.62f, -0.04f), new Vector3(0.8f, 0.08f, 0.08f), m.Black);
        for (int n = 0; n < 8; n++)
            Part(PrimitiveType.Cube, "RFID_Pin" + n, t, new Vector3(-0.35f + 0.1f * n, 0.11f, -0.04f), new Vector3(0.025f, 0.18f, 0.025f), m.Gold);

        var silk = MakeText("RFID_Silkscreen", t, c + new Vector3(0f, 0.6f, -0.025f), "RFID-RC522", 0.014f, Color.white, TextAnchor.MiddleCenter);
        silk.name = "RFID_Silkscreen";
    }

    void BuildCard(Transform root, Vector3 rfid, Mats m)
    {
        _cardHome = new Vector3(2.15f, 0.9f, -1.5f);
        _cardTap = new Vector3(rfid.x, 0.87f, rfid.z - 0.09f);

        var t = new GameObject("RFID_Keycard").transform;
        t.SetParent(root, false);
        t.localPosition = _cardHome;
        _card = t;

        Part(PrimitiveType.Cube, "Card_Body", t, Vector3.zero, new Vector3(0.85f, 0.54f, 0.02f), m.Card);
        Part(PrimitiveType.Cube, "Card_Stripe", t, new Vector3(0f, 0.17f, -0.002f), new Vector3(0.85f, 0.12f, 0.022f), m.CardStripe);
        Part(PrimitiveType.Cube, "Card_Chip", t, new Vector3(-0.22f, -0.03f, -0.002f), new Vector3(0.14f, 0.1f, 0.024f), m.Gold);
        MakeText("Card_Text", t, new Vector3(0.1f, -0.15f, -0.014f), "ACCESS CARD", 0.011f, new Color(0.1f, 0.1f, 0.15f), TextAnchor.MiddleCenter);
    }

    void BuildLcd(Transform root, Mats m, out Vector3[] pins)
    {
        var t = new GameObject("LCD_2004").transform;
        t.SetParent(root, false);
        t.localPosition = Vector3.zero;
        Vector3 c = new Vector3(0f, 2.35f, 0f);

        Part(PrimitiveType.Cube, "LCD_Bezel", t, c + new Vector3(0f, 0f, 1.86f), new Vector3(3.7f, 1.75f, 0.08f), m.Bezel);
        var screenMat = NewMat("Acc_LcdScreen", new Color(0.1f, 0.9f, 0.65f), 0f, 0.9f, true);
        _lcdScreen = Part(PrimitiveType.Cube, "LCD_Screen", t, c + new Vector3(0f, 0f, 1.81f), new Vector3(3.3f, 1.4f, 0.03f), screenMat).GetComponent<Renderer>();

        float[] rowY = { 0.45f, 0.15f, -0.15f, -0.45f };
        var textColor = new Color(0.02f, 0.1f, 0.03f);
        for (int i = 0; i < 4; i++)
            _lcdLines[i] = MakeText("LCD_Line" + i, t, c + new Vector3(0f, rowY[i], 1.785f), "", 0.037f, textColor, TextAnchor.MiddleCenter);

        for (int i = 0; i < 4; i++)
        {
            float sx = (i % 2 == 0) ? -1.75f : 1.75f, sy = (i < 2) ? 0.78f : -0.78f;
            Part(PrimitiveType.Sphere, "LCD_Screw" + i, t, c + new Vector3(sx, sy, 1.815f), Vector3.one * 0.08f, m.Gold);
        }

        pins = new Vector3[4];
        Part(PrimitiveType.Cube, "LCD_I2C_Header", t, c + new Vector3(-0.6f, -0.9f, 1.83f), new Vector3(0.8f, 0.06f, 0.07f), m.Black);
        for (int i = 0; i < 4; i++)
        {
            float x = -0.9f + 0.2f * i;
            Part(PrimitiveType.Cube, "LCD_I2C_Pin" + i, t, new Vector3(x, c.y - 0.98f, 1.83f), new Vector3(0.03f, 0.14f, 0.03f), m.Gold);
            pins[i] = new Vector3(x, c.y - 1.0f, 1.83f);
        }
    }

    void BuildWires(Transform root, Vector3 esp, Vector3 rfid, Vector3[] lcdPins, Mats m)
    {
        var wires = new GameObject("Wires").transform;
        wires.SetParent(root, false);
        wires.localPosition = Vector3.zero;
        const float pinTop = 0.19f, floor = 0.012f;
        float espFront = esp.z - 0.31f, espBack = esp.z + 0.31f;

        // RFID -> ESP32 front header (SDA, SCK, MOSI, MISO, GND, RST, 3V3; IRQ unconnected)
        int[] srcPin = { 0, 1, 2, 3, 5, 6, 7 };
        string[] names = { "SDA", "SCK", "MOSI", "MISO", "GND", "RST", "3V3" };
        Material[] mats = { m.WireYellow, m.WireGreen, m.WireBlue, m.WireOrange, m.WireBlack, m.WireWhite, m.WireRed };
        for (int j = 0; j < srcPin.Length; j++)
        {
            float xs = RfidPinX(rfid, srcPin[j]);
            float xd = EspPinX(esp, 3 + (srcPin.Length - 1 - j));
            float lane = -1.75f + 0.05f * (srcPin.Length - 1 - j);
            Vector3 p0 = new Vector3(xs, 0.03f, rfid.z - 0.04f);
            Vector3 p1 = new Vector3(xs, floor, lane);
            Vector3 p2 = new Vector3(xd, floor, lane);
            Vector3 p3 = new Vector3(xd, pinTop, lane);
            Vector3 p4 = new Vector3(xd, pinTop, espFront);
            string n = "Wire_RFID_" + names[j];
            Segment(n + "_1", wires, p0, p1, mats[j]);
            Segment(n + "_2", wires, p1, p2, mats[j]);
            Segment(n + "_3", wires, p2, p3, mats[j]);
            Segment(n + "_4", wires, p3, p4, mats[j]);
        }

        // ESP32 back header -> LCD I2C pins (GND, VCC, SDA, SCL)
        string[] lcdNames = { "GND", "VCC", "SDA", "SCL" };
        Material[] lcdMats = { m.WireBlack, m.WireRed, m.WireYellow, m.WireGreen };
        for (int k = 0; k < 4; k++)
        {
            float xe = EspPinX(esp, 6 + k);
            float xl = lcdPins[k].x;
            float lane = 0.7f - 0.12f * k;
            Vector3 p0 = new Vector3(xe, pinTop, espBack);
            Vector3 p1 = new Vector3(xe, pinTop, lane);
            Vector3 p2 = new Vector3(xl, pinTop, lane);
            Vector3 p3 = new Vector3(xl, pinTop, 1.83f);
            Vector3 p4 = new Vector3(xl, lcdPins[k].y, 1.83f);
            string n = "Wire_LCD_" + lcdNames[k];
            Segment(n + "_1", wires, p0, p1, lcdMats[k]);
            Segment(n + "_2", wires, p1, p2, lcdMats[k]);
            Segment(n + "_3", wires, p2, p3, lcdMats[k]);
            Segment(n + "_4", wires, p3, p4, lcdMats[k]);
        }
    }
}
