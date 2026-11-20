namespace TERMinator.Linux.Services;

/// <summary>
/// Minimal IObserver&lt;T&gt; that forwards OnNext to an Action.
/// Avalonia 11 doesn't ship the Subscribe(Action&lt;T&gt;) extension, and we don't
/// want to take a dependency on System.Reactive just for this.
/// </summary>
internal sealed class ActionObserver<T> : IObserver<T>
{
    private readonly Action<T> _onNext;

    public ActionObserver(Action<T> onNext) => _onNext = onNext;

    public void OnNext(T value) => _onNext(value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
