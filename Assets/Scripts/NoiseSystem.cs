using System;
using UnityEngine;

// Anything can make a noise: NoiseSystem.Emit(position, radius).
// Monsters listen and investigate noises whose radius reaches them.
public static class NoiseSystem
{
    public static event Action<Vector3, float> OnNoise;

    public static void Emit(Vector3 position, float radius)
    {
        OnNoise?.Invoke(position, radius);
    }
}
