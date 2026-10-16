namespace Cleanse;

/// <summary>
/// Configuration problems that are survivable but worth shouting about. Collected during service
/// registration and emitted through the logger once it exists, so they reach the platform's log
/// index as proper ECS JSON rather than raw console output.
/// </summary>
public sealed class StartupWarnings
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public void Add(string message) => _messages.Add(message);
}
