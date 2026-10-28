using System.Collections.Generic;
using UnityEngine;

// One shared way for 3D sounds to fade with distance, so everything in the game obeys the same "physics":
// full volume out to fullVolumeDistance (roughly the size of the thing making the noise), then the real-world
// inverse-distance law (-6 dB every time the distance doubles), easing to silence at maxDistance.
//
// Gain staging: every sound's volume is set so that, heard at its fullVolumeDistance, its loudest moment lands at
// a target level (loudest 0.4 s RMS of the file + the volume in dB). When adding a sound, measure the file and
// pick the volume that hits its target:
//   giant scream / climber call  -11 to -12 dB   (meant to carry across the map)
//   giant footstep / grab sounds  -14 dB
//   death stinger (2D)            -14 dB
//   hatch creak                   -14 dB         (a warning: the room below is 4-5 m away)
//   hatch landing thump           -18 dB
//   door torn open by the giant   -14 dB
//   window smashed by the Climber -14 dB
//   clue radio static             -30 dB         (louder while a broadcast comes through)
//   door closing / opening        -27 / -28 dB   (your own sneaky actions: just above your footsteps)
//   climber crawl steps           -24 dB         (climbing steps -27)
//   giant breathing               -24 dB
//   player footsteps              -29 dB         (your own steps should sit under everything)
//   ambience bed (2D)             -37 dB         (night drone; fades in with the dark)
//   dusk ambience bed (2D)        -35 dB         (sparse birds; the file is gain-staged to -29 at volume 1, played at 0.5)
public static class AudioMix
{
    static readonly Dictionary<(float, float), AnimationCurve> curves = new Dictionary<(float, float), AnimationCurve>();

    public static void Setup3D(AudioSource s, float fullVolumeDistance, float maxDistance)
    {
        fullVolumeDistance = Mathf.Max(0.1f, fullVolumeDistance);
        maxDistance = Mathf.Max(maxDistance, fullVolumeDistance + 1f);
        s.playOnAwake = false;
        s.spatialBlend = 1f;
        s.dopplerLevel = 0f;
        s.minDistance = fullVolumeDistance;
        s.maxDistance = maxDistance;
        s.rolloffMode = AudioRolloffMode.Custom;
        s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, Curve(fullVolumeDistance, maxDistance));
    }

    // X = distance / maxDistance (that's how Unity reads custom rolloff curves), Y = volume
    static AnimationCurve Curve(float full, float max)
    {
        var key = (full, max);
        if (curves.TryGetValue(key, out var cached)) return cached;

        var keys = new List<Keyframe>();
        const int samples = 32;
        for (int i = 0; i <= samples; i++)
        {
            float x = Mathf.Pow(i / (float)samples, 2f);   // more points close to the source, where it changes fastest
            float d = x * max;
            float v = d <= full ? 1f : full / d;            // inverse-distance law
            v *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, x));   // ease out to silence at max range
            keys.Add(new Keyframe(x, v));
        }
        var curve = new AnimationCurve(keys.ToArray());
        for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
        curves[key] = curve;
        return curve;
    }
}
