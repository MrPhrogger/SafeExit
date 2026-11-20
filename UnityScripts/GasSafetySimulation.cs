// ESP32 Gas & Fire Safety Prototype - Unity 6 version
// Setup: delete the old script, add this file to Assets/ (keep the name GasSafetySimulation.cs),
// create an empty GameObject, attach "GasSafetySimulation" to it, press Play.
// Everything (status light, valves, buzzer) is created automatically at runtime.
//
// Simulated hardware (virtual ESP32):
//   - MQ-5   : simulated methane / combustible-gas channel
//   - MQ-135 : simulated smoke-index channel
//   - CO2    : independent simulated carbon-dioxide channel
//   - DHT11  : temperature (0-50 °C) + humidity (20-90 %)
//   - Status LEDs: GREEN = ok | YELLOW steady = malfunction/smoke | YELLOW flickering = gas hazard | RED = fire
//   - Buzzer + "cerveau moteur" (motor brain driving the 2 valve motors)

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using SMath = System.Math;

namespace Esp32GasSafety
{
    // ───────────────────────── Basic types ─────────────────────────

    public enum SystemState { Ok, Malfunction, GasLeak, SmokeDetected, Fire }

    public enum GasLine { Methane, Co2 }

    public struct Reading
    {
        public double? Value;     // null = sensor disconnected
        public Reading(double? value) { Value = value; }
    }

    // ───────────────────────── Simulated environment ─────────────────────────

    public class SimEnvironment
    {
        public double Mq5Ppm;
        public double Mq135Ppm = 400;          // smoke-index baseline
        public double Co2Ppm = 420;             // outdoor CO2 baseline
        public double TemperatureC = 24;
        public double HumidityPct = 50;

        // Fault injection
        public bool Mq5Disconnected;
        public bool Mq135Disconnected;
        public bool Co2Disconnected;
        public bool DhtDisconnected;
        public double? Mq5StuckAt;
        public bool MethaneMotorJam;
        public bool Co2MotorJam;

        public void Clean()
        {
            Mq5Ppm = 0; Mq135Ppm = 400; Co2Ppm = 420; TemperatureC = 24; HumidityPct = 50;
            Mq5Disconnected = Mq135Disconnected = Co2Disconnected = DhtDisconnected = false;
            Mq5StuckAt = null; MethaneMotorJam = Co2MotorJam = false;
        }
    }

    // ───────────────────────── Sensors ─────────────────────────

    public abstract class GasSensor
    {
        protected static readonly System.Random Rng = new System.Random();
        public double MinValid, MaxValid;

        protected GasSensor(double min, double max) { MinValid = min; MaxValid = max; }

        public abstract Reading Read(SimEnvironment env);

        public bool IsValid(Reading r) =>
            r.Value.HasValue && r.Value.Value >= MinValid && r.Value.Value <= MaxValid;

        protected static double Noise(double amplitude) => (Rng.NextDouble() - 0.5) * 2 * amplitude;
    }

    public class Mq5Sensor : GasSensor
    {
        public Mq5Sensor() : base(0, 10000) { }

        public override Reading Read(SimEnvironment env)
        {
            if (env.Mq5Disconnected) return new Reading(null);
            if (env.Mq5StuckAt.HasValue) return new Reading(env.Mq5StuckAt.Value);
            return new Reading(SMath.Max(0, env.Mq5Ppm + Noise(15)));
        }
    }

    public class Mq135Sensor : GasSensor
    {
        public Mq135Sensor() : base(10, 10000) { }

        public override Reading Read(SimEnvironment env) =>
            env.Mq135Disconnected ? new Reading(null)
                                  : new Reading(SMath.Max(10, env.Mq135Ppm + Noise(15)));
    }

    public class Co2Sensor : GasSensor
    {
        public Co2Sensor() : base(0, 10000) { }

        public override Reading Read(SimEnvironment env) =>
            env.Co2Disconnected ? new Reading(null)
                               : new Reading(SMath.Max(0, env.Co2Ppm + Noise(15)));
    }

    /// <summary>DHT11: integer resolution, valid 0-50 °C and 20-90 % RH.</summary>
    public class Dht11Sensor
    {
        public const double TempMin = 0, TempMax = 50, HumMin = 20, HumMax = 90;
        private static readonly System.Random Rng = new System.Random();

        public void Read(SimEnvironment env, out double? temp, out double? hum)
        {
            if (env.DhtDisconnected) { temp = null; hum = null; return; }
            temp = SMath.Round(env.TemperatureC + (Rng.NextDouble() - 0.5) * 0.8);
            hum = SMath.Round(env.HumidityPct + (Rng.NextDouble() - 0.5) * 2);
        }

        public static bool TempValid(double? t) => t.HasValue && t >= TempMin && t <= TempMax;
        public static bool HumValid(double? h) => h.HasValue && h >= HumMin && h <= HumMax;
    }

    // ───────────────────────── Motor brain (cerveau moteur) ─────────────────────────

    public class ValveMotor
    {
        private const double StepPerCycle = 50;   // % of travel per cycle

        public string Name;
        public GasLine Line;
        public double Position = 100;             // 100 = open, 0 = closed
        public double Target = 100;
        public bool Jammed;

        public ValveMotor(string name, GasLine line) { Name = name; Line = line; }

        public string Step()
        {
            if (SMath.Abs(Position - Target) < 0.01) return null;
            if (Jammed) return $"{Name}: motor JAMMED at {Position:F0}% (limit switch not reached)";

            double dir = Target > Position ? 1 : -1;
            Position = SMath.Min(100, SMath.Max(0, Position + dir * StepPerCycle));

            if (Position <= 0.01) return $"{Name}: fully CLOSED (limit switch reached)";
            if (Position >= 99.99) return $"{Name}: fully OPEN (limit switch reached)";
            return $"{Name}: moving ({Position:F0}%)";
        }

        public bool IsStalled => Jammed && SMath.Abs(Position - Target) > 0.01;
        public bool IsMoving => !Jammed && SMath.Abs(Position - Target) > 0.01;

        public string StateText =>
            IsStalled ? $"JAMMED@{Position:F0}%" :
            Position <= 0.01 ? "CLOSED" :
            Position >= 99.99 ? "OPEN" : $"MOVING {Position:F0}%";
    }

    public class MotorBrain
    {
        public readonly ValveMotor Methane = new ValveMotor("Valve_Methane (methane valve)", GasLine.Methane);
        public readonly ValveMotor Co2 = new ValveMotor("Valve_CO2 (carbon dioxide valve)", GasLine.Co2);

        public IEnumerable<ValveMotor> Motors { get { yield return Methane; yield return Co2; } }

        public ValveMotor Get(GasLine line) => line == GasLine.Methane ? Methane : Co2;

        public void SyncHardware(SimEnvironment env)
        {
            Methane.Jammed = env.MethaneMotorJam;
            Co2.Jammed = env.Co2MotorJam;
        }

        public IEnumerable<string> Faults() =>
            Motors.Where(m => m.Jammed).Select(m => m.Name + " motor jammed");

        /// <summary>True if this is a new close command (valves are latching).</summary>
        public bool RequestClose(GasLine line)
        {
            var m = Get(line);
            if (m.Target <= 0.01) return false;
            m.Target = 0;
            return true;
        }

        public List<string> Tick() =>
            Motors.Select(m => m.Step()).Where(e => e != null).ToList();

        public void ResetAll() { foreach (var m in Motors) m.Target = 100; }
    }

    // ───────────────────────── ESP32 controller ─────────────────────────

    public class Esp32Controller
    {
        const double MethanePpm = 1000;       // MQ-5 simulated methane channel
        const double SmokeHazardPpm = 1500;   // MQ-135 smoke index
        const double Co2HazardPpm = 1500;     // independent simulated CO2 channel
        const double HotTempC = 45;           // DHT11 maxes out at 50 °C
        const double RapidRiseC = 3;          // °C per cycle
        const double LowHumidity = 30;

        private readonly Mq5Sensor _mq5 = new Mq5Sensor();
        private readonly Mq135Sensor _mq135 = new Mq135Sensor();
        private readonly Co2Sensor _co2 = new Co2Sensor();
        private readonly Dht11Sensor _dht = new Dht11Sensor();
        public readonly MotorBrain Motors = new MotorBrain();

        // Outputs / state exposed to the scene and the GUI
        public SystemState State = SystemState.Ok;
        public Reading Mq5, Mq135, Co2;
        public double? Temp, Hum;
        public bool BuzzerOn;
        public string BuzzerPattern = "off";
        public string Diagnosis = "Starting...";
        public List<string> Faults = new List<string>();
        public System.Action<string> Log = _ => { };

        private double? _prevTemp, _lastMq5;
        private int _mq5StuckCount;
        private SystemState? _lastNotified;
        private string _lastFaultSig = "";

        public void ResetValves() => Motors.ResetAll();

        public void Cycle(SimEnvironment env, int tick)
        {
            // 1. Gather data
            Motors.SyncHardware(env);
            Mq5 = _mq5.Read(env);
            Mq135 = _mq135.Read(env);
            Co2 = _co2.Read(env);
            _dht.Read(env, out Temp, out Hum);

            // 2. Hardware health check
            var faults = new List<string>();
            if (!_mq5.IsValid(Mq5)) faults.Add("MQ-5 sensor fault");
            if (!_mq135.IsValid(Mq135)) faults.Add("MQ-135 sensor fault");
            if (!_co2.IsValid(Co2)) faults.Add("CO2 sensor fault");

            bool tempOverRange = Temp.HasValue && Temp > Dht11Sensor.TempMax;
            if (!Dht11Sensor.TempValid(Temp)) faults.Add("DHT11 temperature fault");
            if (!Dht11Sensor.HumValid(Hum)) faults.Add("DHT11 humidity fault");

            if (Mq5.Value.HasValue && _lastMq5.HasValue && Mq5.Value == _lastMq5) _mq5StuckCount++;
            else _mq5StuckCount = 0;
            _lastMq5 = Mq5.Value;
            if (_mq5StuckCount >= 3) faults.Add("MQ-5 frozen (stuck value)");

            faults.AddRange(Motors.Faults());
            Faults = faults;

            bool mq5Ok = _mq5.IsValid(Mq5) && _mq5StuckCount < 3;
            bool mq135Ok = _mq135.IsValid(Mq135);
            bool co2Ok = _co2.IsValid(Co2);
            bool tempOk = Dht11Sensor.TempValid(Temp);
            bool humOk = Dht11Sensor.HumValid(Hum);

            double methane = mq5Ok ? Mq5.Value.Value : 0;
            double smoke = mq135Ok ? Mq135.Value.Value : 0;
            double co2 = co2Ok ? Co2.Value.Value : 0;
            double t = tempOk ? Temp.Value : double.NaN;
            double h = humOk ? Hum.Value : double.NaN;

            double rise = (tempOk && _prevTemp.HasValue) ? t - _prevTemp.Value : 0;
            if (tempOk) _prevTemp = t;

            // 3. Diagnosis (priority: fire > methane/CO2 > smoke > malfunction > OK)
            var actions = new List<string>();
            bool newCommand = false;
            SystemState state;

            bool smokeHigh = mq135Ok && smoke >= SmokeHazardPpm;
            bool co2High = co2Ok && co2 >= Co2HazardPpm;
            bool hot = tempOk && t >= HotTempC;
            bool rapidRise = tempOk && rise >= RapidRiseC;
            bool dry = humOk && h <= LowHumidity;

            int fireScore = 0;
            if (smokeHigh) fireScore += 2;
            if (hot) fireScore += 2;
            if (rapidRise) fireScore += 1;
            if (dry && (hot || smokeHigh)) fireScore += 1;
            if (tempOverRange && smokeHigh) fireScore += 2;    // DHT11 saturated = very hot

            bool methaneLeak = mq5Ok && methane >= MethanePpm;
            bool co2Leak = co2High;
            bool fireConditions = smokeHigh && (hot || rapidRise || tempOverRange)
                               || hot && rapidRise;

            if (fireScore >= 3 && fireConditions)
            {
                state = SystemState.Fire;
                string tText = tempOk ? $"{t:F0}°C (+{rise:F0}/cycle)" : "temperature over DHT11 range";
                Diagnosis = $"FIRE detected (smoke index {smoke:F0} ppm, {tText}). EVACUATE.";
                foreach (var line in new[] { GasLine.Methane, GasLine.Co2 })
                    if (Motors.RequestClose(line)) { actions.Add($"Close command sent to {Motors.Get(line).Name}"); newCommand = true; }
                actions.Add("Evacuation alarm triggered");
            }
            else if (methaneLeak || co2Leak)
            {
                state = SystemState.GasLeak;
                var leaking = new List<string>();
                if (methaneLeak)
                {
                    leaking.Add($"methane ({methane:F0} ppm)");
                    if (Motors.RequestClose(GasLine.Methane)) { actions.Add("Close command sent to Methane valve"); newCommand = true; }
                }
                if (co2Leak)
                {
                    leaking.Add($"CO2 ({co2:F0} ppm)");
                    if (Motors.RequestClose(GasLine.Co2)) { actions.Add("Close command sent to CO2 valve"); newCommand = true; }
                }
                if (smokeHigh) leaking.Add($"smoke ({smoke:F0} ppm index)");
                Diagnosis = "GAS HAZARD: " + string.Join(" + ", leaking) + ". No flames or switches!";
                foreach (var f in Motors.Faults()) actions.Add($"WARNING: {f} - manual shut-off required");
            }
            else if (smokeHigh)
            {
                state = SystemState.SmokeDetected;
                Diagnosis = $"SMOKE detected (smoke index {smoke:F0} ppm); no fire conditions confirmed.";
                actions.Add("Investigate smoke source; fire alarm remains ready");
            }
            else if (faults.Count > 0)
            {
                state = SystemState.Malfunction;
                Diagnosis = "Hardware malfunction: " + string.Join(", ", faults);
                actions.Add("Maintenance required; monitoring continues with remaining sensors");
            }
            else
            {
                state = SystemState.Ok;
                Diagnosis = "All systems normal.";
            }
            State = state;

            // 4. Buzzer pattern
            switch (state)
            {
                case SystemState.Fire: BuzzerOn = true; BuzzerPattern = "CONTINUOUS"; break;
                case SystemState.GasLeak: BuzzerOn = tick % 2 == 0; BuzzerPattern = "intermittent beeps"; break;
                case SystemState.SmokeDetected: BuzzerOn = tick % 3 == 0; BuzzerPattern = "smoke alert"; break;
                case SystemState.Malfunction: BuzzerOn = tick % 5 == 0; BuzzerPattern = "short chirp"; break;
                default: BuzzerOn = false; BuzzerPattern = "off"; break;
            }

            // 5. Notify the building only when something changes
            string faultSig = string.Join("|", faults.OrderBy(f => f));
            if (_lastNotified != state || faultSig != _lastFaultSig || newCommand)
            {
                string channel = state == SystemState.Fire ? "PA SYSTEM + SMS + FIRE BRIGADE"
                               : state == SystemState.GasLeak ? "PA SYSTEM + SMS to maintenance"
                               : state == SystemState.SmokeDetected ? "Smoke alert + building dashboard"
                               : state == SystemState.Malfunction ? "Maintenance dashboard" : "Dashboard";
                Log($"[BUILDING via {channel}] {Diagnosis}");
                foreach (var a in actions) Log("    Action: " + a);
                _lastNotified = state;
                _lastFaultSig = faultSig;
            }

            // 6. Motors move
            foreach (var e in Motors.Tick()) Log("[MOTOR] " + e);
        }
    }

    // ───────────────────────── Demo scenarios ─────────────────────────

    public class Scenario
    {
        public string Name;
        public int Cycles;
        public System.Action<SimEnvironment, int> Apply;
        public bool ResetValvesFirst;

        public Scenario(string name, int cycles, System.Action<SimEnvironment, int> apply, bool resetValves = false)
        { Name = name; Cycles = cycles; Apply = apply; ResetValvesFirst = resetValves; }
    }
}

// ───────────────────────── Unity component ─────────────────────────

public class GasSafetySimulation : MonoBehaviour
{
    [Header("Simulation")]
    [Tooltip("Seconds between two ESP32 sensor cycles")]
    public float cycleSeconds = 1f;
    public bool startInDemoMode = true;
    public bool loopDemo = true;
    [Tooltip("Place the Main Camera so the whole scene is visible")]
    public bool setupCamera = true;
    [Tooltip("Size of the on-screen menu (bigger = easier to read)")]
    [Range(0.5f, 2.5f)] public float menuScale = 0.9f;

    private Esp32GasSafety.SimEnvironment _env = new Esp32GasSafety.SimEnvironment();
    private Esp32GasSafety.Esp32Controller _esp32 = new Esp32GasSafety.Esp32Controller();
    public Esp32GasSafety.SystemState CurrentState => _esp32.State;
    private List<Esp32GasSafety.Scenario> _scenarios;
    private int _scIdx, _cycleInSc, _tick;
    private bool _demoMode;
    private float _timer;
    private Vector2 _menuScroll;
    private bool _menuOpen = true;

    // Hazard buttons: the environment ramps toward these targets (null = nothing to do)
    private double? _tgtMq5, _tgtMq135, _tgtCo2, _tgtTemp;

    // Scene objects (serialized: they are created once in the editor and stay in the scene)
    [Header("Scene objects (built by 'Build Scene Objects')")]
    [SerializeField] private GameObject _sceneRoot;
    // Status LEDs: index 0 = green (OK), 1 = yellow (gas leak flickers / malfunction steady), 2 = red (fire)
    [SerializeField] private Renderer[] _ledRenderers = new Renderer[3];
    [SerializeField] private Light[] _ledLights = new Light[3];
    private readonly float[] _ledLevel = new float[3];
    private readonly float[] _ledTarget = new float[3];
    [SerializeField] private Renderer _buzzerRenderer;
    [SerializeField] private AudioSource _audio;

    [System.Serializable]
    private class ValveVisual
    {
        public Renderer Pipe;
        public Transform HandlePivot;
        [System.NonSerialized] public double Shown = 100;
    }
    [SerializeField] private ValveVisual _combVisual = new ValveVisual();
    [SerializeField] private ValveVisual _toxVisual = new ValveVisual();

    // Leak simulator (valve that ejects gas / fire), gauges and event screen
    [SerializeField] private ParticleSystem _gasFx;
    [SerializeField] private ParticleSystem _fireFx;
    [SerializeField] private Light _fireLight;
    [SerializeField] private Transform _leakHandle;
    [SerializeField] private Transform _tempNeedle;
    [SerializeField] private Transform _humNeedle;
    [SerializeField] private TextMesh _tempText;
    [SerializeField] private TextMesh _humText;
    [SerializeField] private Renderer _eventScreen;
    [SerializeField] private TextMesh _eventTitle;
    [SerializeField] private TextMesh _eventSub;
    [SerializeField] private TextMesh _eventReadings;
    private float _leakOpen, _tempAngle = 135f, _humAngle = 135f;

    // ───────────── Unity lifecycle ─────────────

    void Awake()
    {
        BuildScenarios();
        _esp32.Log = AddLog;
    }

    // Called by Unity in the editor when the component is added (or Reset): builds the scene objects once.
    void Reset()
    {
        BuildScene();
    }

    void Start()
    {
        // Safety net: if the objects were never built (or were deleted), build them now.
        bool missing = LedsMissing() || _buzzerRenderer == null
                    || _audio == null || _combVisual.Pipe == null || _toxVisual.Pipe == null
                    || _gasFx == null || _fireFx == null || _tempNeedle == null || _humNeedle == null
                    || _eventTitle == null || _leakHandle == null;
        if (missing) BuildScene();
        if (_gasFx != null) _gasFx.Play();
        if (_fireFx != null) _fireFx.Play();

        EnsureBuzzerSound();
        _demoMode = startInDemoMode;
        if (!_demoMode) AddLog("Manual mode: use the sliders on the left.");
    }

    void Update()
    {
        StepHazards();
        _timer += Time.deltaTime;
        if (_timer >= cycleSeconds)
        {
            _timer -= cycleSeconds;
            RunCycle();
        }
        UpdateVisuals();
    }

    void RunCycle()
    {
        if (_demoMode) StepDemo();
        _esp32.Cycle(_env, _tick++);
    }

    // ───────────── Demo ─────────────

    void BuildScenarios()
    {
        _scenarios = new List<Esp32GasSafety.Scenario>
        {
            new Esp32GasSafety.Scenario("1. Normal conditions", 4, (e, i) => { }),

            new Esp32GasSafety.Scenario("2. Combustible gas leak (MQ-5 rising)", 5, (e, i) =>
                e.Mq5Ppm = 400 + i * 400),

            new Esp32GasSafety.Scenario("3. Technician resets valves, back to normal", 4, (e, i) => e.Clean(), true),

            new Esp32GasSafety.Scenario("4. CO2 leak (no heat)", 5, (e, i) =>
                e.Co2Ppm = 600 + i * 500),

            new Esp32GasSafety.Scenario("5. Back to normal", 4, (e, i) => e.Clean(), true),

            new Esp32GasSafety.Scenario("6. FIRE (smoke + fast temperature rise + low humidity)", 6, (e, i) =>
            {
                e.Mq135Ppm = 800 + i * 900;
                e.TemperatureC = 26 + i * 5;                       // last step exceeds DHT11 range
                e.HumidityPct = SMathMax(20, 50 - i * 8);
            }, true),

            new Esp32GasSafety.Scenario("7. Recovery after fire", 4, (e, i) => e.Clean(), true),

            new Esp32GasSafety.Scenario("8. Hardware fault: MQ-135 disconnected", 4, (e, i) =>
            {
                e.Clean(); e.Mq135Disconnected = true;
            }),

            new Esp32GasSafety.Scenario("9. Repaired, then DHT11 fails and MQ-5 freezes", 6, (e, i) =>
            {
                e.Clean();
                e.DhtDisconnected = i >= 2;
                e.Mq5StuckAt = i >= 3 ? (double?)120 : null;
            }),

            new Esp32GasSafety.Scenario("10. Methane leak with a jammed valve motor", 5, (e, i) =>
            {
                e.Clean(); e.Mq5Ppm = 1600; e.MethaneMotorJam = true;
            }, true),
        };
    }

    static double SMathMax(double a, double b) => a > b ? a : b;

    void StepDemo()
    {
        if (_scIdx >= _scenarios.Count)
        {
            if (loopDemo) { _scIdx = 0; _cycleInSc = 0; _env.Clean(); _esp32.ResetValves(); }
            else { _demoMode = false; AddLog("Demo finished. Switched to manual mode."); return; }
        }

        var sc = _scenarios[_scIdx];
        if (_cycleInSc == 0)
        {
            AddLog($"--- {sc.Name} ---");
            if (sc.ResetValvesFirst) _esp32.ResetValves();
        }

        sc.Apply(_env, _cycleInSc);
        _cycleInSc++;
        if (_cycleInSc >= sc.Cycles) { _scIdx++; _cycleInSc = 0; }
    }

    void AddLog(string message)
    {
        // History lives only in the Console
        Debug.Log("[GasSafety] " + message);
    }

    // ───────────── Scene construction ─────────────

    /// <summary>
    /// Creates all scene objects as children of this GameObject. Run it in the editor
    /// (right-click the component header > "Build Scene Objects"): the objects then stay in the scene.
    /// Running it again rebuilds them from scratch.
    /// </summary>
    [ContextMenu("Build Scene Objects")]
    public void BuildScene()
    {
        ClearScene();

        var rootGo = new GameObject("GasSafetyScene");
        rootGo.transform.SetParent(transform, false);
        _sceneRoot = rootGo;
        Transform root = rootGo.transform;
        Vector3 o = transform.position;

        // Floor
        MakePrimitive(PrimitiveType.Cube, "Floor", root, o + new Vector3(-0.5f, -0.1f, 0),
                      new Vector3(10f, 0.2f, 4f), new Color(0.25f, 0.25f, 0.28f));

        // Status LEDs: a small signal post with three LEDs (red on top, yellow, green at the bottom)
        MakePrimitive(PrimitiveType.Cylinder, "StatusPost", root, o + new Vector3(0f, 0.625f, 0f),
                      new Vector3(0.14f, 0.625f, 0.14f), new Color(0.2f, 0.2f, 0.22f));
        MakePrimitive(PrimitiveType.Cube, "StatusHousing", root, o + new Vector3(0f, 2.3f, 0f),
                      new Vector3(0.9f, 2.15f, 0.5f), new Color(0.05f, 0.05f, 0.06f));
        string[] ledNames = { "Green", "Yellow", "Red" };
        float[] ledY = { 1.6f, 2.3f, 3.0f };
        var bezelMat = NewMat("Led_Bezel", new Color(0.12f, 0.12f, 0.14f), 0.5f, 0.5f);
        for (int i = 0; i < 3; i++)
        {
            Color on = LedColors[i];
            Color startColor = (i == 0) ? on : DimLed(on);   // green is lit until Play starts
            var ledMat = NewMat("Led_" + ledNames[i], startColor, 0f, 0.9f, i == 0);
            PartZ("StatusLed_" + ledNames[i] + "_Bezel", root, o + new Vector3(0f, ledY[i], -0.27f), 0.62f, 0.04f, bezelMat);
            var led = Part(PrimitiveType.Sphere, "StatusLed_" + ledNames[i], root, o + new Vector3(0f, ledY[i], -0.22f),
                           new Vector3(0.5f, 0.5f, 0.3f), ledMat);
            _ledRenderers[i] = led.GetComponent<Renderer>();

            // Real light so the LED lights up the scene
            var lg = new GameObject("StatusLedLight_" + ledNames[i]);
            lg.transform.SetParent(root, false);
            lg.transform.position = o + new Vector3(0f, ledY[i], -0.7f);
            var light = lg.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = on;
            light.range = 6f;
            light.intensity = (i == 0) ? 3f : 0f;
            light.enabled = i == 0;
            _ledLights[i] = light;
            _ledLevel[i] = (i == 0) ? 1f : 0f;
        }

        // Buzzer (visual + audio source; the tone itself is generated when Play starts)
        var buzzerGo = MakePrimitive(PrimitiveType.Cube, "Buzzer", root, o + new Vector3(3f, 0.5f, 0),
                                     new Vector3(0.8f, 0.8f, 0.8f), new Color(0.1f, 0.1f, 0.1f));
        _buzzerRenderer = buzzerGo.GetComponent<Renderer>();
        _audio = buzzerGo.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.loop = true;
        _audio.volume = 0f;
        _audio.spatialBlend = 0f;

        // Valves
        _combVisual = BuildValve("Valve_Methane", root, o + new Vector3(-4f, 0.5f, 0));
        _toxVisual = BuildValve("Valve_CO2", root, o + new Vector3(-1.5f, 0.5f, 0));

        // Hardware components, each built from several parts (named after the real parts)
        var hw = MakeHwMats();
        Vector3 mq5Pos = o + new Vector3(-3.5f, 0f, -1.3f);
        Vector3 mq135Pos = o + new Vector3(-1.5f, 0f, -1.3f);
        Vector3 espPos = o + new Vector3(0.8f, 0f, -1.3f);
        Vector3 dhtPos = o + new Vector3(3f, 0f, -1.3f);
        BuildMqModule("MQ5", root, mq5Pos, "MQ-5", hw.PcbBlue, hw);
        BuildMqModule("MQ135", root, mq135Pos, "MQ-135", hw.PcbBlue2, hw);
        BuildEsp32(root, espPos, hw);
        BuildDht11(root, dhtPos, hw);
        BuildWires(root, mq5Pos, mq135Pos, espPos, dhtPos, hw);

        // Floating name labels (readable in the Game/Scene view)
        AddLabel("MQ-5\n(methane / combustible)", root, o + new Vector3(-3.5f, 1.1f, -1.3f));
        AddLabel("MQ-135\n(smoke index)", root, o + new Vector3(-1.5f, 1.1f, -1.3f));
        AddLabel("ESP32\n(controller)", root, o + new Vector3(0.8f, 0.8f, -1.3f));
        AddLabel("DHT11\n(temp / humidity)", root, o + new Vector3(3f, 1.4f, -1.3f));
        AddLabel("Status LEDs\ngreen OK | yellow gas/smoke/fault | red fire", root, o + new Vector3(0, 3.45f, 0));
        AddLabel("Buzzer", root, o + new Vector3(3f, 1.3f, 0));
        AddLabel("Valve_Methane", root, o + new Vector3(-4f, 1.5f, 0));
        AddLabel("Valve_CO2", root, o + new Vector3(-1.5f, 1.5f, 0));

        // Back wall that holds the event screen and the gauges
        MakePrimitive(PrimitiveType.Cube, "BackWall", root, o + new Vector3(-0.5f, 1.8f, 1.95f),
                      new Vector3(10f, 3.6f, 0.1f), new Color(0.16f, 0.17f, 0.2f));

        // Event display: shows the current event decided by the ESP32
        MakePrimitive(PrimitiveType.Cube, "EventScreen_Bezel", root, o + new Vector3(-2.9f, 2.4f, 1.86f),
                      new Vector3(3.7f, 1.5f, 0.06f), new Color(0.05f, 0.05f, 0.06f));
        var screenGo = MakePrimitive(PrimitiveType.Cube, "EventScreen", root, o + new Vector3(-2.9f, 2.4f, 1.82f),
                                     new Vector3(3.5f, 1.3f, 0.04f), new Color(0.02f, 0.06f, 0.02f));
        _eventScreen = screenGo.GetComponent<Renderer>();
        _eventTitle = MakeText("EventScreen_Title", root, o + new Vector3(-2.9f, 2.65f, 1.77f), "ALL OK", 0.06f, Color.green, TextAnchor.MiddleCenter);
        _eventSub = MakeText("EventScreen_Detail", root, o + new Vector3(-2.9f, 2.25f, 1.77f), "all systems normal", 0.028f, Color.white, TextAnchor.MiddleCenter);
        _eventReadings = MakeText("EventScreen_Readings", root, o + new Vector3(-2.9f, 1.9f, 1.77f), "", 0.022f, new Color(0.7f, 0.7f, 0.7f), TextAnchor.MiddleCenter);
        AddLabel("Event display (ESP32)", root, o + new Vector3(-2.9f, 3.1f, 1.8f));

        // Temperature and humidity gauges (show what the DHT11 reports)
        _tempNeedle = BuildGauge("TempGauge", root, o + new Vector3(1.7f, 2.4f, 1.8f), "DHT11 TEMP", "0", "70", 0.64f, out _tempText);
        _humNeedle = BuildGauge("HumidityGauge", root, o + new Vector3(3.5f, 2.4f, 1.8f), "DHT11 HUMIDITY", "0", "100", 2f, out _humText);

        // Leak simulator: a valve near the ESP32 that ejects gas or fire
        Vector3 lp = o + new Vector3(1.9f, 0f, -0.4f);
        MakePrimitive(PrimitiveType.Cylinder, "LeakValve_Pipe", root, lp + Vector3.up * 0.45f,
                      new Vector3(0.16f, 0.45f, 0.16f), new Color(0.45f, 0.47f, 0.5f));
        MakePrimitive(PrimitiveType.Sphere, "LeakValve_Body", root, lp + Vector3.up * 0.45f,
                      Vector3.one * 0.38f, new Color(0.75f, 0.55f, 0.15f));
        MakePrimitive(PrimitiveType.Cylinder, "LeakValve_Nozzle", root, lp + Vector3.up * 0.93f,
                      new Vector3(0.26f, 0.04f, 0.26f), new Color(0.12f, 0.12f, 0.12f));
        var stem = MakePrimitive(PrimitiveType.Cylinder, "LeakValve_Stem", root, lp + new Vector3(0f, 0.45f, -0.2f),
                                 new Vector3(0.05f, 0.08f, 0.05f), new Color(0.3f, 0.3f, 0.3f));
        stem.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var wheelPivot = new GameObject("LeakValve_WheelPivot").transform;
        wheelPivot.SetParent(root, false);
        wheelPivot.position = lp + new Vector3(0f, 0.45f, -0.3f);
        var wheel = MakePrimitive(PrimitiveType.Cylinder, "Wheel", wheelPivot, wheelPivot.position,
                                  new Vector3(0.42f, 0.015f, 0.42f), new Color(0.8f, 0.1f, 0.1f));
        wheel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        MakePrimitive(PrimitiveType.Cube, "WheelSpokeV", wheelPivot, wheelPivot.position - Vector3.forward * 0.02f,
                      new Vector3(0.04f, 0.42f, 0.02f), Color.white);
        MakePrimitive(PrimitiveType.Cube, "WheelSpokeH", wheelPivot, wheelPivot.position - Vector3.forward * 0.02f,
                      new Vector3(0.42f, 0.04f, 0.02f), Color.white);
        _leakHandle = wheelPivot;
        AddLabel("Leak simulator\n(gas / fire)", root, o + new Vector3(1.9f, 0.02f, -0.85f));

        // Particle effects (generated, no external assets needed)
        var fxTex = SoftTexture();
        Vector3 tip = lp + Vector3.up * 0.97f;
        _gasFx = BuildParticles("LeakGasFx", root, tip, MakeFxMaterial("LeakGasFx_Mat", false, fxTex));
        ConfigureGasFx(_gasFx);
        _fireFx = BuildParticles("LeakFireFx", root, tip, MakeFxMaterial("LeakFireFx_Mat", true, fxTex));
        ConfigureFireFx(_fireFx);

        var fireLightGo = new GameObject("LeakFireLight");
        fireLightGo.transform.SetParent(root, false);
        fireLightGo.transform.position = tip + Vector3.up * 0.7f - Vector3.forward * 0.3f;
        _fireLight = fireLightGo.AddComponent<Light>();
        _fireLight.type = LightType.Point;
        _fireLight.color = new Color(1f, 0.5f, 0.15f);
        _fireLight.range = 5f;
        _fireLight.intensity = 0f;
        _fireLight.enabled = false;

        // Camera
        if (setupCamera && Camera.main != null)
        {
            var cam = Camera.main.transform;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.RecordObject(cam, "Position camera");
#endif
            Vector3 camPos = o + new Vector3(-0.5f, 3.0f, -9f);          // higher than before
            cam.position = camPos;
            cam.rotation = Quaternion.LookRotation((o + new Vector3(-0.5f, 1.0f, 0f)) - camPos);   // still aims at the board area
        }

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.RegisterCreatedObjectUndo(rootGo, "Build gas safety scene");
            UnityEditor.EditorUtility.SetDirty(this);
            if (gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif
    }

    /// <summary>Removes the objects created by BuildScene.</summary>
    [ContextMenu("Clear Scene Objects")]
    public void ClearScene()
    {
        if (_sceneRoot != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.DestroyObjectImmediate(_sceneRoot);
            else Destroy(_sceneRoot);
#else
            Destroy(_sceneRoot);
#endif
        }
        _sceneRoot = null;
        _ledRenderers = new Renderer[3]; _ledLights = new Light[3];
        _buzzerRenderer = null; _audio = null;
        _combVisual = new ValveVisual();
        _toxVisual = new ValveVisual();
        _gasFx = null; _fireFx = null; _fireLight = null; _leakHandle = null;
        _tempNeedle = null; _humNeedle = null; _tempText = null; _humText = null;
        _eventScreen = null; _eventTitle = null; _eventSub = null; _eventReadings = null;
        _leakOpen = 0f; _tempAngle = 135f; _humAngle = 135f;
    }

    GameObject MakePrimitive(PrimitiveType type, string objName, Transform parent, Vector3 worldPos, Vector3 scale, Color color)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = objName;
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        go.transform.localScale = scale;

        var col = go.GetComponent<Collider>();
        if (col != null) DestroyObj(col);

        // Own material per object (works with Built-in, URP and HDRP), saved with the scene
        var r = go.GetComponent<Renderer>();
        var mat = new Material(r.sharedMaterial) { name = objName + "_Mat", color = color };
        r.sharedMaterial = mat;
        return go;
    }

    /// <summary>Creates a floating text label (child of the scene root, named "Label_...").</summary>
    void AddLabel(string text, Transform parent, Vector3 worldPos)
    {
        var go = new GameObject("Label_" + text.Split('\n')[0].Replace(" ", ""));
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;

        const float labelSize = 0.032f;     // smaller floating names
        const float outlineWidth = 0.01f;   // outline thickness in world units
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // Outline: dark copies of the text offset in 8 directions, a little behind the main text
        for (int k = 0; k < 8; k++)
        {
            float ang = k * Mathf.PI / 4f;
            var oc = new GameObject("Outline" + k);
            oc.transform.SetParent(go.transform, false);
            oc.transform.localPosition = new Vector3(Mathf.Cos(ang) * outlineWidth, Mathf.Sin(ang) * outlineWidth, 0.02f);
            var ot = oc.AddComponent<TextMesh>();
            ot.text = text;
            ot.font = font;
            ot.fontSize = 64;
            ot.characterSize = labelSize;
            ot.anchor = TextAnchor.LowerCenter;
            ot.alignment = TextAlignment.Center;
            ot.color = new Color(0.02f, 0.02f, 0.03f);
            oc.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }

        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 64;
        tm.characterSize = labelSize;
        tm.anchor = TextAnchor.LowerCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.white;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
    }

    /// <summary>Creates a TextMesh (readable from the front, the camera looks along +Z).</summary>
    TextMesh MakeText(string objName, Transform parent, Vector3 worldPos, string text, float charSize, Color color, TextAnchor anchor)
    {
        var go = new GameObject(objName);
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;

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

    /// <summary>Analog dial with a needle. Needle angle: +135 (min, lower-left) to -135 (max, lower-right).</summary>
    Transform BuildGauge(string gName, Transform root, Vector3 c, string title, string minLabel, string maxLabel,
                         float redFrom, out TextMesh valueText)
    {
        var bezel = MakePrimitive(PrimitiveType.Cylinder, gName + "_Bezel", root, c,
                                  new Vector3(1.5f, 0.05f, 1.5f), new Color(0.05f, 0.05f, 0.06f));
        bezel.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        var face = MakePrimitive(PrimitiveType.Cylinder, gName + "_Face", root, c + new Vector3(0f, 0f, -0.03f),
                                 new Vector3(1.3f, 0.05f, 1.3f), new Color(0.93f, 0.93f, 0.9f));
        face.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        const int ticks = 7;
        for (int i = 0; i < ticks; i++)
        {
            float t = i / (float)(ticks - 1);
            float a = Mathf.Lerp(135f, -135f, t);
            Vector3 dir = Quaternion.Euler(0f, 0f, a) * Vector3.up;
            var tk = MakePrimitive(PrimitiveType.Cube, gName + "_Tick" + i, root, c + dir * 0.55f + new Vector3(0f, 0f, -0.09f),
                                   new Vector3(0.035f, 0.1f, 0.01f), t >= redFrom ? new Color(0.8f, 0.05f, 0.05f) : Color.black);
            tk.transform.rotation = Quaternion.Euler(0f, 0f, a);
        }

        var pivot = new GameObject(gName + "_NeedlePivot").transform;
        pivot.SetParent(root, false);
        pivot.position = c + new Vector3(0f, 0f, -0.11f);
        MakePrimitive(PrimitiveType.Cube, gName + "_Needle", pivot, pivot.position + Vector3.up * 0.2f,
                      new Vector3(0.045f, 0.46f, 0.015f), new Color(0.85f, 0.1f, 0.1f));
        MakePrimitive(PrimitiveType.Sphere, gName + "_Hub", root, pivot.position, Vector3.one * 0.1f, new Color(0.1f, 0.1f, 0.1f));

        MakeText(gName + "_Title", root, c + new Vector3(0f, 0.32f, -0.1f), title, 0.02f, Color.black, TextAnchor.MiddleCenter);
        MakeText(gName + "_Min", root, c + new Vector3(-0.42f, -0.5f, -0.1f), minLabel, 0.018f, Color.black, TextAnchor.MiddleCenter);
        MakeText(gName + "_Max", root, c + new Vector3(0.42f, -0.5f, -0.1f), maxLabel, 0.018f, Color.black, TextAnchor.MiddleCenter);
        valueText = MakeText(gName + "_Value", root, c + new Vector3(0f, -0.3f, -0.1f), "--", 0.035f, Color.black, TextAnchor.MiddleCenter);
        return pivot;
    }

    // ───── Particle helpers ─────

    static Texture2D SoftTexture()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SoftParticle", wrapMode = TextureWrapMode.Clamp };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                a = a * a * (3f - 2f * a);
                px[y * n + x] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Shader FindShader(params string[] names)
    {
        foreach (var n in names)
        {
            var sh = Shader.Find(n);
            if (sh != null) return sh;
        }
        return null;
    }

    static Material MakeFxMaterial(string matName, bool additive, Texture2D tex)
    {
        Shader sh = additive
            ? FindShader("Legacy Shaders/Particles/Additive", "Particles/Standard Unlit", "Sprites/Default")
            : FindShader("Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Particles/Standard Unlit");
        if (sh == null) sh = Shader.Find("Hidden/InternalErrorShader");
        var m = new Material(sh) { name = matName };
        m.mainTexture = tex;
        if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
        return m;
    }

    ParticleSystem BuildParticles(string pName, Transform parent, Vector3 pos, Material mat)
    {
        var go = new GameObject(pName);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);   // cone emits upwards

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = mat;
        pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        pr.receiveShadows = false;

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 600;
        var em = ps.emission;
        em.rateOverTime = 0f;
        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        return ps;
    }

    static Gradient MakeGradient(Color[] colors, float[] colorTimes, float[] alphas, float[] alphaTimes)
    {
        var ck = new GradientColorKey[colors.Length];
        for (int i = 0; i < colors.Length; i++) ck[i] = new GradientColorKey(colors[i], colorTimes[i]);
        var ak = new GradientAlphaKey[alphas.Length];
        for (int i = 0; i < alphas.Length; i++) ak[i] = new GradientAlphaKey(alphas[i], alphaTimes[i]);
        var g = new Gradient();
        g.SetKeys(ck, ak);
        return g;
    }

    static void ConfigureGasFx(ParticleSystem ps)
    {
        var main = ps.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 3.5f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.7f, 1.1f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
        main.gravityModifier = -0.02f;

        var shape = ps.shape;
        shape.angle = 20f;
        shape.radius = 0.05f;

        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(MakeGradient(
            new[] { Color.white, Color.white }, new[] { 0f, 1f },
            new[] { 0f, 1f, 0f }, new[] { 0f, 0.15f, 1f }));

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.4f, 1f, 1.8f));

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.35f;
        noise.frequency = 0.6f;
    }

    static void ConfigureFireFx(ParticleSystem ps)
    {
        var main = ps.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.6f, 2.4f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
        main.gravityModifier = -0.15f;
        main.startColor = Color.white;

        var shape = ps.shape;
        shape.angle = 10f;
        shape.radius = 0.06f;

        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(MakeGradient(
            new[] { new Color(1f, 0.95f, 0.6f), new Color(1f, 0.5f, 0.1f), new Color(0.45f, 0.08f, 0.04f) },
            new[] { 0f, 0.4f, 1f },
            new[] { 0.9f, 0.8f, 0f }, new[] { 0f, 0.5f, 1f }));

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.15f));

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.5f;
        noise.frequency = 1.5f;
    }

    // ───── Hardware models (assembled from simple parts) ─────

    private Material _matSource;

    sealed class HwMats
    {
        public Material PcbBlue, PcbBlue2, PcbBlack, Silver, Gold, Black, Gray, Mesh, DhtBlue, DhtSlot, White,
                        PotBlue, LedRed, LedGreen, LedBlue, Tan, Board, Stand,
                        WireRed, WireBlack, WireYellow, WireGreen, WireWhite;
    }

    Material NewMat(string matName, Color color, float metallic, float smoothness, bool glow = false)
    {
        if (_matSource == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _matSource = tmp.GetComponent<Renderer>().sharedMaterial;
            DestroyObj(tmp);
        }
        var m = new Material(_matSource) { name = matName, color = color };
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (glow)
        {
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", color * 2f);
        }
        return m;
    }

    HwMats MakeHwMats()
    {
        return new HwMats
        {
            PcbBlue = NewMat("PCB_Blue", new Color(0.05f, 0.2f, 0.55f), 0f, 0.7f),
            PcbBlue2 = NewMat("PCB_Blue2", new Color(0.07f, 0.3f, 0.6f), 0f, 0.7f),
            PcbBlack = NewMat("PCB_Black", new Color(0.03f, 0.03f, 0.04f), 0f, 0.65f),
            Silver = NewMat("Metal_Silver", new Color(0.78f, 0.78f, 0.8f), 1f, 0.75f),
            Gold = NewMat("Metal_Gold", new Color(0.9f, 0.72f, 0.2f), 1f, 0.7f),
            Black = NewMat("Plastic_Black", new Color(0.02f, 0.02f, 0.025f), 0f, 0.45f),
            Gray = NewMat("Plastic_Gray", new Color(0.35f, 0.36f, 0.38f), 0f, 0.4f),
            Mesh = NewMat("Sensor_Mesh", new Color(0.25f, 0.25f, 0.27f), 0.8f, 0.3f),
            DhtBlue = NewMat("DHT11_Blue", new Color(0.1f, 0.42f, 0.85f), 0f, 0.55f),
            DhtSlot = NewMat("DHT11_Slot", new Color(0.02f, 0.08f, 0.2f), 0f, 0.2f),
            White = NewMat("Silkscreen_White", new Color(0.92f, 0.92f, 0.9f), 0f, 0.3f),
            PotBlue = NewMat("Pot_Blue", new Color(0.2f, 0.5f, 0.95f), 0f, 0.5f),
            LedRed = NewMat("LED_Red", new Color(1f, 0.1f, 0.1f), 0f, 0.8f, true),
            LedGreen = NewMat("LED_Green", new Color(0.1f, 1f, 0.2f), 0f, 0.8f, true),
            LedBlue = NewMat("LED_Blue", new Color(0.2f, 0.4f, 1f), 0f, 0.8f, true),
            Tan = NewMat("Cap_Tan", new Color(0.72f, 0.56f, 0.28f), 0f, 0.4f),
            Board = NewMat("Breadboard", new Color(0.9f, 0.9f, 0.86f), 0f, 0.35f),
            Stand = NewMat("Stand_Dark", new Color(0.1f, 0.1f, 0.12f), 0f, 0.4f),
            WireRed = NewMat("Wire_Red", new Color(0.85f, 0.1f, 0.1f), 0f, 0.5f),
            WireBlack = NewMat("Wire_Black", new Color(0.05f, 0.05f, 0.05f), 0f, 0.5f),
            WireYellow = NewMat("Wire_Yellow", new Color(0.95f, 0.8f, 0.1f), 0f, 0.5f),
            WireGreen = NewMat("Wire_Green", new Color(0.1f, 0.7f, 0.2f), 0f, 0.5f),
            WireWhite = NewMat("Wire_White", new Color(0.9f, 0.9f, 0.9f), 0f, 0.5f),
        };
    }

    /// <summary>A primitive that shares a material (no collider).</summary>
    GameObject Part(PrimitiveType type, string pName, Transform parent, Vector3 worldPos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = pName;
        go.transform.SetParent(parent, false);
        go.transform.position = worldPos;
        go.transform.localScale = scale;
        var col = go.GetComponent<Collider>();
        if (col != null) DestroyObj(col);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    /// <summary>A cylinder lying along the Z axis (round face toward the camera).</summary>
    GameObject PartZ(string pName, Transform parent, Vector3 worldPos, float diameter, float length, Material mat)
    {
        var go = Part(PrimitiveType.Cylinder, pName, parent, worldPos, new Vector3(diameter, length * 0.5f, diameter), mat);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        return go;
    }

    void Wire(string wName, Transform parent, Vector3 a, Vector3 b, Material mat)
    {
        const float thick = 0.02f;
        Vector3 d = b - a;
        float len = d.magnitude;
        if (len < 0.001f) return;
        var go = Part(PrimitiveType.Cube, wName, parent, (a + b) * 0.5f, new Vector3(thick, thick, len + thick), mat);
        go.transform.rotation = Quaternion.LookRotation(d);
    }

    /// <summary>MQ gas sensor breakout: blue PCB, metal-capped sensor, trimmer, comparator chip, LEDs and a 4-pin header.</summary>
    void BuildMqModule(string modName, Transform root, Vector3 basePos, string silk, Material pcb, HwMats m)
    {
        var t = new GameObject(modName).transform;
        t.SetParent(root, false);
        t.position = basePos;
        Vector3 c = basePos + new Vector3(0f, 0.42f, 0f);   // PCB centre (standing upright, components face the camera)

        Part(PrimitiveType.Cube, modName + "_FootL", t, basePos + new Vector3(-0.32f, 0.06f, 0f), new Vector3(0.1f, 0.12f, 0.22f), m.Stand);
        Part(PrimitiveType.Cube, modName + "_FootR", t, basePos + new Vector3(0.32f, 0.06f, 0f), new Vector3(0.1f, 0.12f, 0.22f), m.Stand);
        Part(PrimitiveType.Cube, modName + "_PCB", t, c, new Vector3(0.8f, 0.6f, 0.03f), pcb);

        Vector3 sc = c + new Vector3(-0.12f, 0f, 0f);
        PartZ(modName + "_SensorBase", t, sc + new Vector3(0f, 0f, -0.055f), 0.42f, 0.08f, m.Black);
        PartZ(modName + "_SensorCap", t, sc + new Vector3(0f, 0f, -0.165f), 0.36f, 0.14f, m.Silver);
        PartZ(modName + "_SensorMesh", t, sc + new Vector3(0f, 0f, -0.238f), 0.3f, 0.008f, m.Mesh);

        Part(PrimitiveType.Cube, modName + "_Trimmer", t, c + new Vector3(0.29f, 0.12f, -0.05f), new Vector3(0.14f, 0.14f, 0.07f), m.PotBlue);
        PartZ(modName + "_TrimmerScrew", t, c + new Vector3(0.29f, 0.12f, -0.09f), 0.07f, 0.01f, m.White);
        Part(PrimitiveType.Cube, modName + "_Comparator", t, c + new Vector3(0.29f, -0.07f, -0.03f), new Vector3(0.14f, 0.08f, 0.03f), m.Black);
        Part(PrimitiveType.Cube, modName + "_LedPower", t, c + new Vector3(0.19f, -0.19f, -0.02f), new Vector3(0.05f, 0.04f, 0.02f), m.LedRed);
        Part(PrimitiveType.Cube, modName + "_LedOutput", t, c + new Vector3(0.29f, -0.19f, -0.02f), new Vector3(0.05f, 0.04f, 0.02f), m.LedGreen);

        Part(PrimitiveType.Cube, modName + "_Header", t, c + new Vector3(-0.1f, -0.26f, -0.04f), new Vector3(0.34f, 0.06f, 0.07f), m.Black);
        for (int i = 0; i < 4; i++)
            Part(PrimitiveType.Cube, modName + "_Pin" + i, t, c + new Vector3(-0.22f + 0.08f * i, -0.33f, -0.04f), new Vector3(0.025f, 0.14f, 0.025f), m.Gold);

        MakeText(modName + "_Silkscreen", t, c + new Vector3(-0.12f, 0.25f, -0.025f), silk, 0.016f, Color.white, TextAnchor.MiddleCenter);
    }

    /// <summary>ESP32 DevKit on a breadboard: shielded module with antenna, micro-USB, buttons, USB chip, LEDs and pin headers.</summary>
    void BuildEsp32(Transform root, Vector3 b, HwMats m)
    {
        var t = new GameObject("ESP32").transform;
        t.SetParent(root, false);
        t.position = b;
        Vector3 P(float x, float h, float z) => b + new Vector3(x, 0.12f + h * 0.5f, z);   // sits on the board top (y = 0.12)

        Part(PrimitiveType.Cube, "ESP32_Breadboard", t, b + new Vector3(0f, 0.04f, 0f), new Vector3(1.7f, 0.08f, 0.9f), m.Board);
        Part(PrimitiveType.Cube, "ESP32_BreadboardGroove", t, b + new Vector3(0f, 0.081f, 0f), new Vector3(1.6f, 0.004f, 0.05f), m.Stand);
        Part(PrimitiveType.Cube, "ESP32_PCB", t, b + new Vector3(0f, 0.10f, 0f), new Vector3(1.45f, 0.04f, 0.72f), m.PcbBlack);

        // ESP-WROOM-32 module: metal shield + antenna area with gold traces
        Part(PrimitiveType.Cube, "ESP32_Shield", t, P(0.2f, 0.05f, 0f), new Vector3(0.5f, 0.05f, 0.42f), m.Silver);
        Part(PrimitiveType.Cube, "ESP32_AntennaBoard", t, P(0.575f, 0.012f, 0f), new Vector3(0.25f, 0.012f, 0.42f), m.PcbBlack);
        for (int i = 0; i < 5; i++)
            Part(PrimitiveType.Cube, "ESP32_AntennaTrace" + i, t, P(0.575f + ((i % 2 == 0) ? 0.02f : -0.02f), 0.02f, -0.16f + 0.08f * i),
                 new Vector3(0.16f, 0.004f, 0.018f), m.Gold);
        var shieldText = MakeText("ESP32_ShieldText", t, b + new Vector3(0.2f, 0.173f, 0f), "ESP-WROOM-32", 0.011f, new Color(0.1f, 0.1f, 0.12f), TextAnchor.MiddleCenter);
        shieldText.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // lies flat, readable from above

        // Micro-USB port, USB-UART chip, buttons, regulator, capacitors, LEDs
        Part(PrimitiveType.Cube, "ESP32_MicroUSB", t, P(-0.68f, 0.07f, 0f), new Vector3(0.2f, 0.07f, 0.28f), m.Silver);
        Part(PrimitiveType.Cube, "ESP32_MicroUSB_Opening", t, P(-0.78f, 0.04f, 0f), new Vector3(0.01f, 0.035f, 0.18f), m.Black);
        Part(PrimitiveType.Cube, "ESP32_UsbChip", t, P(-0.42f, 0.03f, 0.1f), new Vector3(0.14f, 0.03f, 0.14f), m.Black);
        for (int i = 0; i < 2; i++)
        {
            float z = (i == 0) ? -0.25f : 0.25f;
            Part(PrimitiveType.Cube, "ESP32_Button" + i, t, P(-0.55f, 0.04f, z), new Vector3(0.1f, 0.04f, 0.1f), m.Black);
            Part(PrimitiveType.Cylinder, "ESP32_ButtonCap" + i, t, b + new Vector3(-0.55f, 0.172f, z), new Vector3(0.06f, 0.012f, 0.06f), m.Gray);
        }
        Part(PrimitiveType.Cube, "ESP32_Regulator", t, P(-0.3f, 0.04f, -0.2f), new Vector3(0.1f, 0.04f, 0.08f), m.Black);
        for (int i = 0; i < 3; i++)
            Part(PrimitiveType.Cube, "ESP32_Capacitor" + i, t, P(-0.3f + 0.09f * i, 0.04f, 0.2f), new Vector3(0.05f, 0.04f, 0.05f), m.Tan);
        Part(PrimitiveType.Cube, "ESP32_LedPower", t, P(-0.2f, 0.02f, 0.28f), new Vector3(0.05f, 0.02f, 0.05f), m.LedRed);
        Part(PrimitiveType.Cube, "ESP32_LedStatus", t, P(-0.12f, 0.02f, 0.28f), new Vector3(0.05f, 0.02f, 0.05f), m.LedBlue);

        // Pin headers (15 pins on each long edge)
        for (int i = 0; i < 15; i++)
        {
            float x = -0.6f + 0.0857f * i;
            Part(PrimitiveType.Cube, "ESP32_PinFront" + i, t, P(x, 0.07f, -0.31f), new Vector3(0.03f, 0.07f, 0.03f), m.Gold);
            Part(PrimitiveType.Cube, "ESP32_PinBack" + i, t, P(x, 0.07f, 0.31f), new Vector3(0.03f, 0.07f, 0.03f), m.Gold);
        }
    }

    /// <summary>DHT11: blue plastic body with a ventilation grill, mounting hole, printed name and four legs.</summary>
    void BuildDht11(Transform root, Vector3 b, HwMats m)
    {
        var t = new GameObject("DHT11").transform;
        t.SetParent(root, false);
        t.position = b;
        Vector3 c = b + new Vector3(0f, 0.6f, 0f);

        Part(PrimitiveType.Cube, "DHT11_Body", t, c, new Vector3(0.5f, 0.7f, 0.2f), m.DhtBlue);
        for (int i = 0; i < 6; i++)
            Part(PrimitiveType.Cube, "DHT11_Slot" + i, t, c + new Vector3(-0.15f + 0.06f * i, -0.08f, -0.101f), new Vector3(0.025f, 0.4f, 0.01f), m.DhtSlot);
        PartZ("DHT11_Hole", t, c + new Vector3(0f, 0.3f, -0.1f), 0.07f, 0.01f, m.DhtSlot);
        MakeText("DHT11_Print", t, c + new Vector3(0f, 0.19f, -0.105f), "DHT11", 0.017f, Color.white, TextAnchor.MiddleCenter);
        for (int i = 0; i < 4; i++)
            Part(PrimitiveType.Cube, "DHT11_Leg" + i, t, b + new Vector3(-0.15f + 0.1f * i, 0.125f, 0f), new Vector3(0.025f, 0.25f, 0.025f), m.Silver);
    }

    /// <summary>Jumper wires lying on the floor from each sensor to the ESP32 header (routed so they never cross).</summary>
    void BuildWires(Transform root, Vector3 mq5, Vector3 mq135, Vector3 esp, Vector3 dht, HwMats m)
    {
        var wires = new GameObject("Wires").transform;
        wires.SetParent(root, false);
        float fy = esp.y;                   // floor level
        float espFront = esp.z - 0.31f;     // front header row of the ESP32
        float EspX(int i) => esp.x - 0.6f + 0.0857f * i;
        float MqPinX(Vector3 mod, int i) => mod.x - 0.22f + 0.08f * i;
        float DhtPinX(int i) => dht.x - 0.15f + 0.1f * i;

        void Route(string wName, Material mat, float pinX, float pinZ, float espX, float lane)
        {
            var p0 = new Vector3(pinX, fy + 0.03f, pinZ);
            var p1 = new Vector3(pinX, fy + 0.012f, lane);
            var p2 = new Vector3(espX, fy + 0.012f, lane);
            var p3 = new Vector3(espX, fy + 0.19f, lane);
            var p4 = new Vector3(espX, fy + 0.19f, espFront);
            Wire(wName + "_1", wires, p0, p1, mat);
            Wire(wName + "_2", wires, p1, p2, mat);
            Wire(wName + "_3", wires, p2, p3, mat);
            Wire(wName + "_4", wires, p3, p4, mat);
        }

        // Left group (MQ-5 then MQ-135): outermost wire = leftmost sensor pin -> rightmost ESP32 pin
        var left = new (string name, Material mat, float pinX, float pinZ)[]
        {
            ("MQ5_VCC", m.WireRed, MqPinX(mq5, 0), mq5.z),
            ("MQ5_GND", m.WireBlack, MqPinX(mq5, 1), mq5.z),
            ("MQ5_AO", m.WireYellow, MqPinX(mq5, 3), mq5.z),
            ("MQ135_VCC", m.WireRed, MqPinX(mq135, 0), mq135.z),
            ("MQ135_GND", m.WireBlack, MqPinX(mq135, 1), mq135.z),
            ("MQ135_AO", m.WireGreen, MqPinX(mq135, 3), mq135.z),
        };
        for (int n = 0; n < left.Length; n++)
            Route("Wire_" + left[n].name, left[n].mat, left[n].pinX, left[n].pinZ, EspX(8 - n), esp.z - 0.58f + 0.022f * n);

        // Right group (DHT11): outermost wire = rightmost leg -> leftmost ESP32 pin of the group
        var right = new (string name, Material mat, float pinX, float pinZ)[]
        {
            ("DHT11_GND", m.WireBlack, DhtPinX(3), dht.z),
            ("DHT11_DATA", m.WireWhite, DhtPinX(1), dht.z),
            ("DHT11_VCC", m.WireRed, DhtPinX(0), dht.z),
        };
        for (int n = 0; n < right.Length; n++)
            Route("Wire_" + right[n].name, right[n].mat, right[n].pinX, right[n].pinZ, EspX(12 + n), esp.z - 0.52f + 0.022f * n);
    }

    static void DestroyObj(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    ValveVisual BuildValve(string objName, Transform parent, Vector3 pos)
    {
        Color metal = new Color(0.35f, 0.38f, 0.42f);
        Color brass = new Color(0.72f, 0.48f, 0.12f);
        var body = MakePrimitive(PrimitiveType.Sphere, objName + "_Body", parent, pos,
                                 new Vector3(0.72f, 0.72f, 0.72f), brass);
        for (int side = -1; side <= 1; side += 2)
        {
            var pipe = MakePrimitive(PrimitiveType.Cylinder, objName + "_Pipe_" + side, parent,
                                     pos + Vector3.right * (side * 0.78f),
                                     new Vector3(0.28f, 0.7f, 0.28f), metal);
            pipe.transform.rotation = Quaternion.Euler(0f, 0f, 90f);

            var flange = MakePrimitive(PrimitiveType.Cylinder, objName + "_Flange_" + side, parent,
                                       pos + Vector3.right * (side * 0.48f),
                                       new Vector3(0.62f, 0.1f, 0.62f), metal);
            flange.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
        }

        MakePrimitive(PrimitiveType.Cylinder, objName + "_Bonnet", parent,
                      pos + Vector3.up * 0.4f, new Vector3(0.28f, 0.12f, 0.28f), metal);

        var pivot = new GameObject(objName + "_HandlePivot").transform;
        pivot.SetParent(parent, false);
        pivot.position = pos + Vector3.up * 0.56f;

        var handle = MakePrimitive(PrimitiveType.Cube, objName + "_Lever", pivot, pivot.position,
                                   new Vector3(0.9f, 0.12f, 0.18f), new Color(0.8f, 0.08f, 0.06f));
        handle.transform.localPosition = Vector3.zero;
        MakePrimitive(PrimitiveType.Cylinder, objName + "_LeverGrip", pivot,
                      pivot.position + Vector3.right * 0.4f,
                      new Vector3(0.22f, 0.12f, 0.22f), Color.white);

        return new ValveVisual { Pipe = body.GetComponent<Renderer>(), HandlePivot = pivot };
    }

    /// <summary>The buzzer tone is generated at runtime (an AudioClip cannot be saved in the scene).</summary>
    void EnsureBuzzerSound()
    {
        if (_audio == null) return;
        if (_audio.clip == null)
        {
            const int rate = 44100;
            var samples = new float[rate / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = Mathf.Sin(2f * Mathf.PI * 880f * i / rate) * 0.5f;
            var clip = AudioClip.Create("buzzer", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            _audio.clip = clip;
        }
        _audio.loop = true;
        _audio.volume = 0f;
        _audio.spatialBlend = 0f;
        _audio.Play();
    }

    // ───────────── Visual updates ─────────────

    void UpdateVisuals()
    {
        if (LedsMissing()) return;

        // Status LEDs: each one glows only for its own event
        bool flick = Mathf.Repeat(Time.time, 0.5f) < 0.25f;   // flickering yellow = gas leak
        _ledTarget[0] = _ledTarget[1] = _ledTarget[2] = 0f;
        switch (_esp32.State)
        {
            case Esp32GasSafety.SystemState.Ok: _ledTarget[0] = 1f; break;               // green
            case Esp32GasSafety.SystemState.Malfunction: _ledTarget[1] = 1f; break;               // yellow, steady
            case Esp32GasSafety.SystemState.GasLeak: _ledTarget[1] = flick ? 1f : 0f; break;  // yellow, flickering
            case Esp32GasSafety.SystemState.SmokeDetected: _ledTarget[1] = 1f; break;            // yellow, steady
            default: _ledTarget[2] = 1f; break;               // red
        }
        for (int i = 0; i < 3; i++) UpdateLed(i, _ledTarget[i]);

        // Buzzer
        _buzzerRenderer.material.color = _esp32.BuzzerOn ? Color.white : new Color(0.1f, 0.1f, 0.1f);
        _audio.volume = _esp32.BuzzerOn ? 0.25f : 0f;

        // Valves
        UpdateValve(_combVisual, _esp32.Motors.Methane);
        UpdateValve(_toxVisual, _esp32.Motors.Co2);

        UpdateLeak();
        UpdateGauges();
        UpdateEventScreen();
    }

    // ───── Hazard buttons ─────

    void TriggerHazard(string kind)
    {
        if (_demoMode)
        {
            _demoMode = false; _env.Clean(); _esp32.ResetValves();
            AddLog("Manual mode (hazard button pressed).");
        }
        switch (kind)
        {
            case "fire":
                _tgtTemp = 48; _tgtMq135 = 2500;
                AddLog("HAZARD: fire (temperature and smoke rising)");
                break;
            case "methane":
            case "gas":
                _tgtMq5 = 1800;
                AddLog("HAZARD: methane / combustible gas leak");
                break;
            case "co2":
                _tgtCo2 = 3000;
                AddLog("HAZARD: CO2 concentration rising");
                break;
            default:
                _tgtMq5 = 0; _tgtMq135 = 400; _tgtCo2 = 420; _tgtTemp = 24;
                AddLog("Hazards cleared (air returning to normal)");
                break;
        }
    }

    static double Toward(double current, ref double? target, double step)
    {
        if (!target.HasValue) return current;
        double t = target.Value;
        double next = current < t ? SMath.Min(t, current + step) : SMath.Max(t, current - step);
        if (SMath.Abs(next - t) < 1e-6) target = null;
        return next;
    }

    void StepHazards()
    {
        if (_demoMode) return;
        float dt = Time.deltaTime;
        _env.Mq5Ppm = Toward(_env.Mq5Ppm, ref _tgtMq5, 900.0 * dt);
        _env.Mq135Ppm = Toward(_env.Mq135Ppm, ref _tgtMq135, 1000.0 * dt);
        _env.Co2Ppm = Toward(_env.Co2Ppm, ref _tgtCo2, 1000.0 * dt);
        _env.TemperatureC = Toward(_env.TemperatureC, ref _tgtTemp, 10.0 * dt);
    }

    // ───── Leak simulator: shows the real situation (sliders in manual mode), type chosen by the ESP32 state ─────

    static void SetRate(ParticleSystem ps, float rate)
    {
        var e = ps.emission;
        e.rateOverTime = rate;
        if (!ps.isPlaying) ps.Play();
    }

    void UpdateLeak()
    {
        if (_gasFx == null || _fireFx == null) return;
        bool fire = _esp32.State == Esp32GasSafety.SystemState.Fire;

        // Physical situation (manual sliders or demo scenario), 0..1
        float combN = Mathf.Clamp01((float)(_env.Mq5Ppm / 3000.0));
        float co2N = Mathf.Clamp01((float)((_env.Co2Ppm - 420.0) / 5000.0));
        float tempN = Mathf.Clamp01((float)((_env.TemperatureC - 24.0) / 40.0));
        float smokeN = Mathf.Clamp01((float)((_env.Mq135Ppm - 800.0) / 3200.0));
        float gasN = Mathf.Max(combN, co2N);
        float fireN = fire ? Mathf.Clamp(Mathf.Max(tempN, smokeN), 0.35f, 1f) : 0f;

        // Fire (only while the ESP32 reports FIRE)
        SetRate(_fireFx, fire ? 20f + 130f * fireN : 0f);
        if (fire)
        {
            var fm = _fireFx.main;
            fm.startSpeed = new ParticleSystem.MinMaxCurve(1.2f + 1.6f * fireN, 1.8f + 2.2f * fireN);
            fm.startSize = new ParticleSystem.MinMaxCurve(0.2f + 0.25f * fireN, 0.35f + 0.45f * fireN);
        }
        if (_fireLight != null)
        {
            _fireLight.enabled = fire;
            _fireLight.intensity = fire ? 3f * fireN * (0.8f + 0.4f * Mathf.PerlinNoise(Time.time * 8f, 0f)) : 0f;
        }

        // Methane is blue-white; CO2 is shown in gray.
        var gm = _gasFx.main;
        if (fire)
        {
            SetRate(_gasFx, 0f);
        }
        else
        {
            Color methaneColor = new Color(0.72f, 0.88f, 1f, 0.55f);
            Color co2Color = new Color(0.45f, 0.45f, 0.47f, 0.6f);
            gm.startColor = co2N > combN ? co2Color : methaneColor;
            gm.startSize = new ParticleSystem.MinMaxCurve(0.25f + 0.2f * gasN, 0.45f + 0.35f * gasN);
            SetRate(_gasFx, gasN > 0.01f ? 5f + 75f * gasN : 0f);
        }

        // The valve wheel turns open with the intensity
        _leakOpen = Mathf.MoveTowards(_leakOpen, fire ? fireN : gasN, Time.deltaTime * 1.5f);
        if (_leakHandle != null) _leakHandle.localRotation = Quaternion.Euler(0f, 0f, _leakOpen * 270f);
    }

    // ───── Gauges (show what the DHT11 reports; N/A when it is disconnected) ─────

    float AnimateNeedle(Transform needle, float current, double? value, double min, double max)
    {
        float t = value.HasValue ? Mathf.Clamp01((float)((value.Value - min) / (max - min))) : 0f;
        float target = Mathf.Lerp(135f, -135f, t);
        float next = Mathf.Lerp(current, target, 1f - Mathf.Exp(-8f * Time.deltaTime));
        needle.localRotation = Quaternion.Euler(0f, 0f, next);
        return next;
    }

    void UpdateGauges()
    {
        if (_tempNeedle == null || _humNeedle == null) return;
        double? t = _esp32.Temp, h = _esp32.Hum;
        _tempAngle = AnimateNeedle(_tempNeedle, _tempAngle, t, 0, 70);
        _humAngle = AnimateNeedle(_humNeedle, _humAngle, h, 0, 100);

        Color normal = Color.black, warn = new Color(0.75f, 0.45f, 0f), alert = new Color(0.8f, 0.05f, 0.05f), off = new Color(0.45f, 0.45f, 0.45f);
        if (_tempText != null)
        {
            _tempText.text = t.HasValue ? $"{t.Value:F0} °C" : "N/A";
            _tempText.color = !t.HasValue ? off : t.Value >= 45 ? alert : normal;
        }
        if (_humText != null)
        {
            _humText.text = h.HasValue ? $"{h.Value:F0} %" : "N/A";
            _humText.color = !h.HasValue ? off : (h.Value <= 30 || h.Value > 90) ? warn : normal;
        }
    }

    // ───── Event screen ─────

    void UpdateEventScreen()
    {
        if (_eventTitle == null || _eventSub == null || _eventReadings == null) return;
        string title, sub;
        Color col;
        switch (_esp32.State)
        {
            case Esp32GasSafety.SystemState.Ok:
                title = "ALL OK"; sub = "all systems normal"; col = new Color(0.2f, 1f, 0.2f);
                break;
            case Esp32GasSafety.SystemState.Malfunction:
                title = "MALFUNCTION";
                sub = _esp32.Faults.Count > 0
                    ? "Faulty hardware:\n- " + string.Join("\n- ", _esp32.Faults)
                    : "check hardware";
                col = Color.yellow;
                break;
            case Esp32GasSafety.SystemState.GasLeak:
                bool methane = _esp32.Diagnosis.Contains("methane"), co2 = _esp32.Diagnosis.Contains("CO2");
                bool smoke = _esp32.Diagnosis.Contains("smoke (");
                title = "GAS HAZARD";
                sub = methane && co2 ? "methane + CO2" : methane ? "methane (MQ-5)" : "carbon dioxide (CO2)";
                if (smoke) sub += " + smoke";
                col = Mathf.Repeat(Time.time, 0.5f) < 0.25f ? Color.yellow : new Color(0.35f, 0.3f, 0f);   // flickers like the light
                break;
            case Esp32GasSafety.SystemState.SmokeDetected:
                title = "SMOKE DETECTED"; sub = "no fire conditions confirmed"; col = new Color(1f, 0.55f, 0.15f);
                break;
            default:
                title = "FIRE"; sub = "EVACUATE"; col = Color.red;
                break;
        }
        _eventTitle.text = title; _eventTitle.color = col;
        _eventSub.text = sub;
        _eventSub.characterSize = _esp32.State == Esp32GasSafety.SystemState.Malfunction ? 0.018f : 0.028f;
        _eventReadings.text = $"Methane {F(_esp32.Mq5.Value, "ppm")}   CO2 {F(_esp32.Co2.Value, "ppm")}   Smoke {F(_esp32.Mq135.Value, "ppm")}";
        if (_eventScreen != null) _eventScreen.material.color = Color.Lerp(new Color(0.02f, 0.02f, 0.02f), col, 0.12f);
    }

    static readonly Color[] LedColors =
    {
        new Color(0.1f, 1f, 0.2f),      // green
        new Color(1f, 0.85f, 0.05f),    // yellow
        new Color(1f, 0.1f, 0.08f)      // red
    };

    static Color DimLed(Color c) => new Color(c.r * 0.12f, c.g * 0.12f, c.b * 0.12f, 1f);

    bool LedsMissing()
    {
        if (_ledRenderers == null || _ledLights == null) return true;
        if (_ledRenderers.Length < 3 || _ledLights.Length < 3) return true;
        for (int i = 0; i < 3; i++)
            if (_ledRenderers[i] == null || _ledLights[i] == null) return true;
        return false;
    }

    /// <summary>Fades one LED toward on/off: lens colour, emission and a real light.</summary>
    void UpdateLed(int i, float target)
    {
        _ledLevel[i] = Mathf.MoveTowards(_ledLevel[i], target, Time.deltaTime * 12f);   // ~0.08 s fade
        float lv = _ledLevel[i];
        Color on = LedColors[i];

        var mat = _ledRenderers[i].material;
        mat.EnableKeyword("_EMISSION");
        mat.color = Color.Lerp(DimLed(on), on, lv);
        Color emit = new Color(on.r * lv * 3f, on.g * lv * 3f, on.b * lv * 3f, 1f);
        if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emit);
        if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", emit);

        _ledLights[i].enabled = lv > 0.01f;
        _ledLights[i].intensity = lv * 3f;
    }

    void UpdateValve(ValveVisual v, Esp32GasSafety.ValveMotor motor)
    {
        double speed = 50.0 / SMathMax(0.1, cycleSeconds);   // motor travels 50 % per cycle
        double step = speed * Time.deltaTime;
        if (v.Shown < motor.Position) v.Shown = SMathMin(motor.Position, v.Shown + step);
        else if (v.Shown > motor.Position) v.Shown = SMathMax(motor.Position, v.Shown - step);

        // Open = handle along the pipe, closed = handle perpendicular
        float closedFraction = 1f - (float)(v.Shown / 100.0);
        v.HandlePivot.localRotation = Quaternion.Euler(0f, closedFraction * 90f, 0f);

        Color col = motor.IsStalled ? Color.magenta
                  : motor.IsMoving ? Color.yellow
                  : motor.Position <= 0.01 ? Color.red : Color.green;
        v.Pipe.material.color = col;
    }

    static double SMathMin(double a, double b) => a < b ? a : b;

    // ───────────── On-screen dashboard ─────────────

    static string Hex(Esp32GasSafety.SystemState s) => s switch
    {
        Esp32GasSafety.SystemState.Ok => "#44ff44",
        Esp32GasSafety.SystemState.SmokeDetected => "#ff9f33",
        Esp32GasSafety.SystemState.Fire => "#ff4444",
        _ => "#ffe033"
    };

    static string StateText(Esp32GasSafety.SystemState s) => s switch
    {
        Esp32GasSafety.SystemState.Ok => "GREEN - all good",
        Esp32GasSafety.SystemState.Malfunction => "YELLOW (steady) - hardware malfunction",
        Esp32GasSafety.SystemState.GasLeak => "YELLOW (flickering) - GAS HAZARD",
        Esp32GasSafety.SystemState.SmokeDetected => "AMBER - SMOKE DETECTED",
        _ => "RED - FIRE"
    };

    static string F(double? v, string unit) => v.HasValue ? $"{v.Value:F0} {unit}" : "N/A";

    double Slider(string label, double value, float min, float max)
    {
        GUILayout.Label($"{label}: {value:F0}");
        return GUILayout.HorizontalSlider((float)value, min, max);
    }

    static bool HazardButton(string text, Color tint)
    {
        var prev = GUI.backgroundColor;
        GUI.backgroundColor = tint;
        bool pressed = GUILayout.Button(text, GUILayout.Height(32));
        GUI.backgroundColor = prev;
        return pressed;
    }

    /// <summary>Slider that also cancels a running hazard ramp when the user drags it.</summary>
    double SliderRamp(string label, double value, float min, float max, ref double? ramp)
    {
        GUILayout.Label($"{label}: {value:F0}");
        bool prev = GUI.changed;
        GUI.changed = false;
        float v = GUILayout.HorizontalSlider((float)value, min, max);
        if (GUI.changed) { ramp = null; value = v; }
        GUI.changed = prev || GUI.changed;
        return value;
    }

    void OnGUI()
    {
        float s = Mathf.Max(0.5f, Screen.height / 800f) * menuScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));

        GUI.skin.button.fontSize = 13;
        GUI.skin.toggle.fontSize = 13;

        var rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 13 };
        var big = new GUIStyle(rich) { fontSize = 15, fontStyle = FontStyle.Bold };
        var hint = new GUIStyle(rich) { fontSize = 11 };
        hint.normal.textColor = new Color(0.78f, 0.78f, 0.78f);

        if (!_menuOpen)
        {
            if (GUI.Button(new Rect(10, 10, 140, 30), "Show controls")) _menuOpen = true;
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 420, Screen.height / s - 20), GUI.skin.box);
        _menuScroll = GUILayout.BeginScrollView(_menuScroll);
        if (GUILayout.Button("Hide controls")) _menuOpen = false;

        GUILayout.Label("<b>ESP32 Gas & Fire Safety Prototype</b>", big);
        GUILayout.Label("Simulated channels; real MQ sensors are cross-sensitive.", hint);
        GUILayout.Label($"<b>Status:</b> <color={Hex(_esp32.State)}>{StateText(_esp32.State)}</color>", big);
        GUILayout.Label(_esp32.Diagnosis, rich);
        if (_esp32.Faults.Count > 0)
            GUILayout.Label("<b>Hardware faults:</b> " + string.Join(", ", _esp32.Faults), rich);

        GUILayout.Space(4);
        GUILayout.Label($"Methane / MQ-5: {F(_esp32.Mq5.Value, "ppm")}     CO2: {F(_esp32.Co2.Value, "ppm")}", rich);
        GUILayout.Label($"Smoke index / MQ-135: {F(_esp32.Mq135.Value, "ppm")}", rich);
        GUILayout.Label($"DHT11: {F(_esp32.Temp, "°C")}     {F(_esp32.Hum, "%")}", rich);
        GUILayout.Label($"Buzzer: {_esp32.BuzzerPattern}", rich);
        GUILayout.Label($"Valves: Methane = {_esp32.Motors.Methane.StateText}  |  CO2 = {_esp32.Motors.Co2.StateText}", rich);

        // Hazards: trigger events that the system has to detect
        GUILayout.Space(8);
        GUILayout.Label("<b>Trigger a hazard</b>", rich);
        GUILayout.BeginHorizontal();
        if (HazardButton("Fire", new Color(1f, 0.45f, 0.3f))) TriggerHazard("fire");
        if (HazardButton("Methane", new Color(0.5f, 0.8f, 1f))) TriggerHazard("methane");
        if (HazardButton("CO2", new Color(0.75f, 0.65f, 1f))) TriggerHazard("co2");
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Clear hazards", GUILayout.Height(26))) TriggerHazard("clear");
        GUILayout.Label("Fire: heat + smoke   |   Methane: MQ-5 1800   |   CO2: 3000 ppm", hint);

        GUILayout.Space(8);
        if (_demoMode)
        {
            int idx = Mathf.Min(_scIdx, _scenarios.Count - 1);
            GUILayout.Label($"<b>DEMO</b>: {_scenarios[idx].Name}", rich);
            if (GUILayout.Button("Switch to manual control"))
            {
                _demoMode = false; _env.Clean(); _esp32.ResetValves();
                AddLog("Manual mode.");
            }
        }
        else
        {
            GUILayout.Label("<b>MANUAL CONTROL</b>", rich);
            _env.Mq5Ppm = SliderRamp("Methane / MQ-5 ppm (hazard >= 1000)", _env.Mq5Ppm, 0, 3000, ref _tgtMq5);
            _env.Co2Ppm = SliderRamp("CO2 ppm (hazard >= 1500)", _env.Co2Ppm, 0, 6000, ref _tgtCo2);
            _env.TemperatureC = SliderRamp("Temp °C (hot >= 45, DHT11 max 50)", _env.TemperatureC, 0, 70, ref _tgtTemp);
            _env.HumidityPct = Slider("Humidity % (dry <= 30, DHT11 20-90)", _env.HumidityPct, 0, 100);

            GUILayout.Space(4);
            GUILayout.Label("Fault injection:", rich);
            GUILayout.BeginHorizontal();
            _env.Mq5Disconnected = GUILayout.Toggle(_env.Mq5Disconnected, "MQ-5 disconnected");
            _env.Mq135Disconnected = GUILayout.Toggle(_env.Mq135Disconnected, "MQ-135 disconnected");
            GUILayout.EndHorizontal();
            _env.Co2Disconnected = GUILayout.Toggle(_env.Co2Disconnected, "CO2 sensor disconnected");
            GUILayout.BeginHorizontal();
            _env.DhtDisconnected = GUILayout.Toggle(_env.DhtDisconnected, "DHT11 disconnected");
            bool stuck = GUILayout.Toggle(_env.Mq5StuckAt.HasValue, "MQ-5 frozen");
            _env.Mq5StuckAt = stuck ? (double?)(_env.Mq5StuckAt ?? 120) : null;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _env.MethaneMotorJam = GUILayout.Toggle(_env.MethaneMotorJam, "Methane valve jammed");
            _env.Co2MotorJam = GUILayout.Toggle(_env.Co2MotorJam, "CO2 valve jammed");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset sensors"))
            {
                _env.Clean();
                _tgtMq5 = _tgtMq135 = _tgtCo2 = _tgtTemp = null;
            }
            if (GUILayout.Button("Restart demo"))
            {
                _env.Clean(); _esp32.ResetValves();
                _tgtMq5 = _tgtMq135 = _tgtCo2 = _tgtTemp = null;
                _scIdx = 0; _cycleInSc = 0; _demoMode = true;
            }
            GUILayout.EndHorizontal();
        }

        if (GUILayout.Button("Technician: reset (reopen) valves")) _esp32.ResetValves();
        GUILayout.Label("The message history is printed in the Console.", hint);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}
