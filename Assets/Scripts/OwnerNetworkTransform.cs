using Unity.Netcode.Components;

// Like NetworkTransform, but the owning player moves it (not the host).
// This keeps your own movement instant instead of waiting on the network.
public class OwnerNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative() => false;
}
