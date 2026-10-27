using UnityEngine;

// Any monster that can hold a player: the caught player hangs from GripPosition and is made to look at HeadPosition.
public interface IMonsterGrabber
{
    Vector3 HandPosition { get; }
    Vector3 GripPosition { get; }   // where the held body sits (inside the closed hand)
    Vector3 HeadPosition { get; }
}
