using System.Diagnostics;
using Ansible.Core;
using Microsoft.UI.Dispatching;

namespace Ansible;

internal sealed class UiSessionObserver : IDisposable
{
    private readonly DictationSession _session;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<SessionSnapshot> _render;
    private readonly Action<double>? _level;
    private long _lastVersion = -1;
    private volatile bool _disposed;

    public UiSessionObserver(
        DictationSession session, DispatcherQueue dispatcher,
        Action<SessionSnapshot> render, Action<double>? level = null)
    {
        _session = session;
        _dispatcher = dispatcher;
        _render = render;
        _level = level;
        session.StateChanged += OnState;
        session.AudioLevelChanged += OnLevel;
        OnState(session.State);
    }

    private void OnState(SessionSnapshot state) => Dispatch(() =>
    {
        if (_disposed || state.Version <= _lastVersion) { return; }
        _lastVersion = state.Version;
        _render(state);
    });

    private void OnLevel(double value) => Dispatch(() =>
    {
        if (!_disposed) { _level?.Invoke(value); }
    });

    private void Dispatch(Action action)
    {
        if (_disposed) { return; }
        if (_dispatcher.HasThreadAccess) { action(); }
        else if (!_dispatcher.TryEnqueue(() => { if (!_disposed) { action(); } }))
        {
            Debug.WriteLine("Ansible: the UI dispatcher is closed; a late view update was discarded.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _session.StateChanged -= OnState;
        _session.AudioLevelChanged -= OnLevel;
    }
}
