namespace Chaos.Client;

/// <summary>Thrown when a client can't be patched.</summary>
public sealed class PatchException(string message) : Exception(message);
