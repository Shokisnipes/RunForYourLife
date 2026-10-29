using Unity.Netcode;
using UnityEngine;

// Some parked cars still have a working alarm. Walk or run into one and it goes off: a loud wail and blinking orange
// lights for alarmSeconds, heard by the monsters from far away. Bad luck when you're sneaking past, a lure when you
// set one off on purpose and run. Crouch-walking past a car is quiet enough not to set it off. Wrecks never have one.
// The host decides (from the city seed) which cars have alarms and rings them; every machine plays the sound and lights.
// Place ONE of these on the same scene object as GameFlow (it needs the NetworkObject).
public class CarAlarms : NetworkBehaviour
{
    [Header("Which cars")]
    [Range(0f, 1f), Tooltip("Chance each parked car (not wrecks) has a working alarm.")]
    public float alarmChance = 0.35f;

    [Header("Setting it off")]
    [Tooltip("Touching a car while moving faster than this (m/s) sets its alarm off. Crouching is 1.75, walking 3.5.")]
    public float bumpSpeed = 2.5f;
    [Tooltip("How close (metres) your body must get to the car's side to count as touching it.")]
    public float touchDistance = 0.15f;

    [Header("While it rings")]
    [Tooltip("How long an alarm rings (seconds).")]
    public float alarmSeconds = 20f;
    [Tooltip("After it stops, the alarm can go off again after this long (seconds).")]
    public float rearmSeconds = 30f;
    [Tooltip("The monsters hear a ringing alarm this far away (metres).")]
    public float noiseRadius = 70f;
    [Tooltip("The alarm repeats its noise this often (seconds) while it rings, so the monster keeps coming.")]
    public float noiseInterval = 2.5f;

    [Header("Sound and lights")]
    public AudioClip alarmClip;
    [Tooltip("Volume, set to the level in AudioMix.cs (car alarm -13 dB).")]
    [Range(0f, 1f)] public float alarmVolume = 0.72f;
    [Tooltip("The alarm fades out at this distance (metres).")]
    public float soundRange = 120f;
    public Color lightColour = new Color(1f, 0.55f, 0.15f);
    [Tooltip("Brightness of the blinking lights.")]
    public float lightIntensity = 6f;
    [Tooltip("Blinks per second.")]
    public float blinkRate = 1.6f;

    Vehicle[] cars = new Vehicle[0];   // the cars with alarms, sorted by position so every machine agrees
    float[] ringUntil = new float[0];  // local time each alarm stops (every machine)
    float[] armedAt = new float[0];    // host: when each alarm can go off again
    AudioSource[] sources = new AudioSource[0];
    Light[] lights = new Light[0];
    float nextBumpCheck, nextNoise;

    public override void OnNetworkSpawn()
    {
        RefreshCars();
        CityManager.Built += RefreshCars;
    }

    public override void OnNetworkDespawn()
    {
        CityManager.Built -= RefreshCars;
        StopAll();
    }

    // Same cars on every machine: the city is built from the same seed, so sort and roll the same dice
    void RefreshCars()
    {
        StopAll();
        var all = Vehicle.All.ToArray();
        System.Array.Sort(all, (a, b) =>
        {
            Vector3 p = a.transform.position, q = b.transform.position;
            int c = p.x.CompareTo(q.x);
            return c != 0 ? c : p.z.CompareTo(q.z);
        });
        var rng = new System.Random((CityManager.Current != null ? CityManager.Current.seed : 0) ^ 0x4a1a);
        var list = new System.Collections.Generic.List<Vehicle>();
        foreach (var v in all)
            if (rng.NextDouble() < alarmChance && !v.wreck) list.Add(v);
        cars = list.ToArray();
        ringUntil = new float[cars.Length];
        armedAt = new float[cars.Length];
        sources = new AudioSource[cars.Length];
        lights = new Light[cars.Length];
    }

    void StopAll()
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] != null) Destroy(sources[i].gameObject);
            if (lights[i] != null) Destroy(lights[i].gameObject);
        }
    }

    void Update()
    {
        if (!IsSpawned) return;
        CheckBump();

        bool noise = IsServer && Time.time >= nextNoise;
        if (noise) nextNoise = Time.time + noiseInterval;
        for (int i = 0; i < cars.Length; i++)
        {
            if (cars[i] == null) continue;
            bool ringing = Time.time < ringUntil[i];
            if (ringing && noise) NoiseSystem.Emit(cars[i].transform.position, noiseRadius);
            Show(i, ringing);
        }
    }

    // Your own player walking into a car with an alarm
    void CheckBump()
    {
        if (Time.time < nextBumpCheck) return;
        nextBumpCheck = Time.time + 0.1f;
        FirstPersonController me = null;
        foreach (var p in FirstPersonController.All) if (p.IsOwner) { me = p; break; }
        if (me == null || me.IsDead || me.IsGrabbed || me.IsHidden || me.CurrentSpeed < bumpSpeed) return;

        var cc = me.GetComponent<CharacterController>();
        float radius = cc != null ? cc.radius : 0.4f;
        Vector3 waist = me.transform.position + Vector3.up * 0.8f;
        for (int i = 0; i < cars.Length; i++)
        {
            if (cars[i] == null || Time.time < ringUntil[i]) continue;
            Vector3 d = cars[i].transform.position - waist;
            if (d.sqrMagnitude > 64f) continue;   // only cars within 8 m are worth the closer look
            var box = cars[i].GetComponent<BoxCollider>();
            if (box == null) continue;
            Vector3 closest = box.ClosestPoint(waist);
            closest.y = waist.y;
            if (Vector3.Distance(closest, waist) <= radius + touchDistance)
            {
                BumpRpc(i);
                ringUntil[i] = Time.time + 0.5f;   // don't spam the host while the answer comes back
                return;
            }
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void BumpRpc(int car)
    {
        if (car < 0 || car >= cars.Length || Time.time < armedAt[car]) return;
        armedAt[car] = Time.time + alarmSeconds + rearmSeconds;
        nextNoise = 0f;   // heard straight away
        RingRpc(car);
    }

    [Rpc(SendTo.Everyone)]
    void RingRpc(int car)
    {
        if (car < 0 || car >= cars.Length || cars[car] == null) return;
        ringUntil[car] = Time.time + alarmSeconds;
        if (sources[car] == null)
        {
            var go = new GameObject("Car Alarm");
            go.transform.SetParent(cars[car].transform, false);
            go.transform.localPosition = Vector3.up * cars[car].size.y * 0.5f;
            var s = go.AddComponent<AudioSource>();
            AudioMix.Setup3D(s, 3f, soundRange);
            s.clip = alarmClip;
            s.loop = true;
            s.volume = alarmVolume;
            sources[car] = s;

            var lgo = new GameObject("Car Alarm Light");
            lgo.transform.SetParent(cars[car].transform, false);
            lgo.transform.localPosition = new Vector3(0f, cars[car].size.y + 0.4f, 0f);
            var l = lgo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = lightColour;
            l.range = Mathf.Max(cars[car].size.z, 6f);
            l.shadows = LightShadows.None;
            l.enabled = false;
            lights[car] = l;
        }
        if (alarmClip != null) { sources[car].time = 0f; sources[car].Play(); }
    }

    void Show(int i, bool ringing)
    {
        var s = sources[i];
        if (s == null) return;
        if (!ringing)
        {
            if (s.isPlaying) s.Stop();
            lights[i].enabled = false;
            return;
        }
        // Fade the last second out instead of cutting it
        float left = ringUntil[i] - Time.time;
        s.volume = alarmVolume * Mathf.Clamp01(left);
        bool on = Mathf.Repeat(Time.time * blinkRate, 1f) < 0.5f;
        lights[i].enabled = on;
        lights[i].intensity = lightIntensity;
    }

    // Debug/tests: how many cars have alarms, and set one off from the host
    public int AlarmCount => cars.Length;
    public Vector3 AlarmPosition(int i) => cars[i].transform.position;
    public bool Ringing(int i) => Time.time < ringUntil[i];
    public void DebugRing(int i) { if (IsServer) BumpRpc(i); }
}
