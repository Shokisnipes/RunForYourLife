using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// The crater a near-miss slam leaves (on the Monster object, next to MonsterAI). Runs on every computer from
// MonsterAI's SlamRpc, so everyone sees the same hit (the random details differ a little, that's fine).
//  - a cloud of dust rolling out along the street and hanging in the fog
//  - bits of grit thrown up, and a few asphalt chunks that fly, bounce and settle as rubble
//  - a ring of broken slabs pushed up around the impact (no colliders: the NavMesh and players ignore it)
//  - the crack decal (Tools > RunForYourLife > Crack Decal Textures) on the ground, which lights up as a dent
// Craters stay for the rest of the night (a trail of near misses) and go when a new city is built.
// Materials are made by Tools > RunForYourLife > Ground Slam FX Setup.
public class GroundSlamFx : MonoBehaviour
{
    [Tooltip("Seconds from the slam message to the hand hitting the ground (effects wait this long).")]
    public float impactDelay = 0.12f;
    [Tooltip("Oldest craters are removed past this many.")]
    public int maxCraters = 12;

    [Header("Crack decal")]
    public Material crackDecalMaterial;
    [Tooltip("Width of the crack decal (m). The dented middle is about 40% of it.")]
    public float decalSize = 6f;

    [Header("Broken slab rim")]
    public Material rubbleMaterial;
    public int rimSlabs = 12;
    [Tooltip("Slabs sit around the dent at this distance (m) from the middle.")]
    public float rimRadius = 1.35f;
    [Tooltip("How steeply the slabs are pushed up (degrees, min-max).")]
    public Vector2 rimTilt = new Vector2(12f, 35f);

    [Header("Flying chunks (physics for a few seconds, then they stay as rubble)")]
    public int chunks = 9;
    public Vector2 chunkSize = new Vector2(0.12f, 0.4f);
    public Vector2 chunkSpeed = new Vector2(4f, 9f);
    public float chunkSettleTime = 3f;

    [Header("Dust")]
    public Material dustMaterial;
    public int dustPuffs = 45;
    public Color dustColor = new Color(0.55f, 0.53f, 0.5f, 0.45f);
    [Tooltip("How fast the dust ring rolls out (m/s, min-max).")]
    public Vector2 dustSpeed = new Vector2(3f, 8f);
    public Vector2 dustLifetime = new Vector2(3f, 5.5f);
    [Tooltip("Small grit thrown up into the air.")]
    public int grit = 40;

    readonly Queue<GameObject> craters = new Queue<GameObject>();
    Transform root;

    void OnEnable() => CityManager.Built += Clear;
    void OnDisable() => CityManager.Built -= Clear;

    // New city: the old craters go with the old streets
    void Clear()
    {
        foreach (var c in craters) if (c != null) Destroy(c);
        craters.Clear();
    }

    public void Spawn(Vector3 point, Vector3 monsterPosition)
    {
        if (root == null) root = new GameObject("Slam Craters").transform;
        point = RealFloor(point);
        var crater = new GameObject("Crater").transform;
        crater.SetParent(root, false);
        crater.SetPositionAndRotation(point, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));

        MakeDecal(crater);
        MakeRim(crater);
        MakeChunks(crater, point);
        MakeDust(point);

        craters.Enqueue(crater.gameObject);
        while (craters.Count > maxCraters)
        {
            var old = craters.Dequeue();
            if (old != null) Destroy(old);
        }
    }

    // The NavMesh floats above the street; find the real ground under the hand (not a player, not the monster)
    Vector3 RealFloor(Vector3 p)
    {
        var hits = Physics.RaycastAll(p + Vector3.up * 3f, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        Vector3 floor = p;
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<FirstPersonController>() != null) continue;
            if (h.collider.GetComponentInParent<MonsterAI>() != null) continue;
            if (h.rigidbody != null && !h.rigidbody.isKinematic) continue;
            if (h.distance < best) { best = h.distance; floor = h.point; }
        }
        return floor;
    }

    void MakeDecal(Transform crater)
    {
        if (crackDecalMaterial == null) return;
        var go = new GameObject("Crack");
        go.transform.SetParent(crater, false);
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // projects straight down
        var d = go.AddComponent<DecalProjector>();
        d.material = crackDecalMaterial;
        d.pivot = Vector3.zero;
        d.size = new Vector3(decalSize, decalSize, 0.8f);   // shallow, so it doesn't paint feet standing in it
    }

    void MakeRim(Transform crater)
    {
        for (int i = 0; i < rimSlabs; i++)
        {
            float a = (i + Random.Range(-0.3f, 0.3f)) / rimSlabs * 360f;
            Vector3 outward = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
            float r = rimRadius * Random.Range(0.85f, 1.15f);
            float width = Random.Range(0.7f, 1.3f), depth = Random.Range(0.5f, 0.9f), thick = Random.Range(0.1f, 0.2f);

            var slab = Piece(crater, new Vector3(width, thick, depth), false);
            // Inner edge lifted, outer edge sunk into the street: the ground looks pushed up around the hit
            slab.localRotation = Quaternion.LookRotation(outward) * Quaternion.Euler(-Random.Range(rimTilt.x, rimTilt.y), Random.Range(-15f, 15f), Random.Range(-10f, 10f));
            slab.localPosition = outward * r + Vector3.up * thick * 0.3f;
        }
    }

    void MakeChunks(Transform crater, Vector3 point)
    {
        var monsterColliders = GetComponentsInChildren<Collider>();
        for (int i = 0; i < chunks; i++)
        {
            float s = Random.Range(chunkSize.x, chunkSize.y);
            var chunk = Piece(crater, new Vector3(s * Random.Range(0.8f, 1.6f), s * Random.Range(0.4f, 0.8f), s), true);
            Vector3 dir = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
            chunk.position = point + dir * Random.Range(0.3f, 1f) + Vector3.up * 0.3f;
            chunk.rotation = Random.rotation;
            var col = chunk.GetComponent<Collider>();
            foreach (var m in monsterColliders) if (m != null) Physics.IgnoreCollision(col, m);
            var rb = chunk.gameObject.AddComponent<Rigidbody>();
            rb.mass = 5f;
            rb.linearVelocity = (dir * Random.Range(0.3f, 0.8f) + Vector3.up).normalized * Random.Range(chunkSpeed.x, chunkSpeed.y);
            rb.angularVelocity = Random.insideUnitSphere * 12f;
            StartCoroutine(Settle(rb, col));
        }
    }

    // Once they've landed they're just scenery: no physics, nothing to trip over
    IEnumerator Settle(Rigidbody rb, Collider col)
    {
        yield return new WaitForSeconds(chunkSettleTime);
        if (rb != null) Destroy(rb);
        if (col != null) Destroy(col);
    }

    Transform Piece(Transform parent, Vector3 size, bool keepCollider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        if (!keepCollider) Destroy(go.GetComponent<Collider>());
        go.name = "Rubble";
        go.transform.SetParent(parent, false);
        go.transform.localScale = size;
        if (rubbleMaterial != null) go.GetComponent<MeshRenderer>().sharedMaterial = rubbleMaterial;
        return go.transform;
    }

    void MakeDust(Vector3 point)
    {
        if (dustMaterial == null) return;
        var go = new GameObject("Slam Dust");
        go.transform.position = point + Vector3.up * 0.3f;
        Destroy(go, dustLifetime.y + 1f);

        // Dust ring rolling out low along the ground
        var dust = go.AddComponent<ParticleSystem>();
        dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = dust.main;
        main.duration = 1f;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(dustLifetime.x, dustLifetime.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(dustSpeed.x, dustSpeed.y);
        main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = dustColor;
        main.gravityModifier = -0.01f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = dustPuffs + 10;
        var emission = dust.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)dustPuffs) });
        var shape = dust.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.8f;
        shape.rotation = new Vector3(-90f, 0f, 0f);   // flat on the ground, puffing outward
        var vel = dust.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        vel.y = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
        var limit = dust.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 0.6f;
        limit.dampen = 0.08f;   // rolls out fast, then hangs
        var size = dust.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 2.4f));
        var rot = dust.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
        var color = dust.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                     new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
        color.color = fade;
        var rend = dust.GetComponent<ParticleSystemRenderer>();
        rend.sharedMaterial = dustMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        dust.Play();

        // Grit thrown up into the air, falling back and bouncing on the street
        var gritGo = new GameObject("Grit");
        gritGo.transform.SetParent(go.transform, false);
        var bits = gritGo.AddComponent<ParticleSystem>();
        bits.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var bm = bits.main;
        bm.duration = 0.5f;
        bm.loop = false;
        bm.playOnAwake = false;
        bm.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
        bm.startSpeed = new ParticleSystem.MinMaxCurve(5f, 12f);
        bm.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.14f);
        bm.startColor = new Color(0.12f, 0.11f, 0.1f, 1f);
        bm.gravityModifier = 1.5f;
        bm.simulationSpace = ParticleSystemSimulationSpace.World;
        bm.maxParticles = grit + 5;
        var be = bits.emission;
        be.rateOverTime = 0f;
        be.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)grit) });
        var bs = bits.shape;
        bs.shapeType = ParticleSystemShapeType.Cone;
        bs.angle = 40f;
        bs.radius = 0.6f;
        bs.rotation = new Vector3(-90f, 0f, 0f);   // up
        var bc = bits.collision;
        bc.enabled = true;
        bc.type = ParticleSystemCollisionType.Planes;
        var plane = new GameObject("Ground Plane").transform;
        plane.SetParent(go.transform, false);
        plane.position = point;
        bc.SetPlane(0, plane);
        bc.bounce = 0.3f;
        bc.dampen = 0.5f;
        bc.lifetimeLoss = 0.2f;
        var br = bits.GetComponent<ParticleSystemRenderer>();
        br.sharedMaterial = dustMaterial;
        br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        bits.Play();
    }
}
