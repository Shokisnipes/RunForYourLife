using UnityEngine;

// Any monster that can hold a player: the caught player follows HandPosition and is made to look at HeadPosition.
public interface IMonsterGrabber
{
    Vector3 HandPosition { get; }
    Vector3 HeadPosition { get; }
}
