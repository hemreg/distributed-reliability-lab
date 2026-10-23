namespace Lab.Shared;

/// <summary>
/// A one-shot crash point, armed over HTTP (POST /faults/...) and held only in this process's
/// memory. It fires at most once: when it fires the process is killed, and the restarted
/// process starts with nothing armed, so it behaves normally after the restart.
/// If an operation_id was given, only a message with that operation_id fires it;
/// otherwise the next message does.
/// </summary>
public sealed class ArmedFault(string name)
{
    private readonly Lock _gate = new();
    private bool _armed;
    private string? _operationId;

    public string Name { get; } = name;

    public void Arm(string? operationId)
    {
        lock (_gate)
        {
            _armed = true;
            _operationId = string.IsNullOrWhiteSpace(operationId) ? null : operationId;
        }
    }

    /// <summary>True exactly once for the first matching message after Arm; disarms itself.</summary>
    public bool TryFire(string operationId)
    {
        lock (_gate)
        {
            if (!_armed || (_operationId is not null && _operationId != operationId)) return false;
            _armed = false;
            _operationId = null;
            return true;
        }
    }
}
