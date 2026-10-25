namespace DeskDx;

/// <summary>
/// Registered answers for the session's dialog watcher. Registrations are matched in order against
/// the dialog text (case-insensitive <c>Contains</c>); the first match wins.
/// </summary>
/// <remarks>
/// The registry is thread-safe: registering during a running session is allowed, and the watcher
/// thread reads the list under the same lock.
/// </remarks>
public sealed class DialogRegistry
{
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = new();

    /// <summary>
    /// Registers an answer for dialogs whose text contains <paramref name="textContains"/>
    /// (case-insensitive).
    /// </summary>
    /// <param name="textContains">Substring of the dialog text to match.</param>
    /// <param name="answer">Button label to click, e.g. "Ja", "No", "OK" — matched by button name.</param>
    public void On(string textContains, string answer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textContains);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        lock (_gate)
        {
            _registrations.Add(new Registration(textContains, answer));
        }
    }

    /// <summary>Removes every registration (used between scenarios in a single session).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _registrations.Clear();
        }
    }

    /// <summary>The answer for a dialog text, or null when nothing is registered.</summary>
    internal string? FindAnswer(string dialogText)
    {
        lock (_gate)
        {
            foreach (var registration in _registrations)
            {
                if (dialogText.Contains(registration.TextContains, StringComparison.OrdinalIgnoreCase))
                {
                    return registration.Answer;
                }
            }

            return null;
        }
    }

    private sealed record Registration(string TextContains, string Answer);
}
