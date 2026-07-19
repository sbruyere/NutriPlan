namespace NutriPlan.Services;

public enum ToastKind { Saved, Success, Error }

public sealed record ToastMessage(Guid Id, string Text, ToastKind Kind);

/// <summary>
/// File de notifications éphémères (scopée par circuit). Les toasts « Enregistré »
/// se remplacent entre eux pour ne pas s'empiler pendant la saisie rapide.
/// Les mutations sont verrouillées et <see cref="Toasts"/> expose un instantané :
/// l'auto-fermeture reprend sur un thread du pool pendant que le circuit peut
/// être en train de rendre la liste.
/// </summary>
public class ToastService
{
    private readonly object _gate = new();
    private readonly List<ToastMessage> _toasts = [];

    public IReadOnlyList<ToastMessage> Toasts
    {
        get { lock (_gate) return _toasts.ToArray(); }
    }

    public event Action? OnChange;

    public void ShowSaved(string text = "Enregistré") => Show(text, ToastKind.Saved);
    public void ShowSuccess(string text) => Show(text, ToastKind.Success);
    public void ShowError(string text) => Show(text, ToastKind.Error);

    private void Show(string text, ToastKind kind)
    {
        var toast = new ToastMessage(Guid.NewGuid(), text, kind);
        lock (_gate)
        {
            if (kind == ToastKind.Saved) _toasts.RemoveAll(t => t.Kind == ToastKind.Saved);
            _toasts.Add(toast);
        }
        OnChange?.Invoke();
        _ = AutoDismissAsync(toast);
    }

    private async Task AutoDismissAsync(ToastMessage toast)
    {
        await Task.Delay(toast.Kind == ToastKind.Error ? 6000 : 1800);
        Dismiss(toast.Id);
    }

    public void Dismiss(Guid id)
    {
        bool removed;
        lock (_gate) { removed = _toasts.RemoveAll(t => t.Id == id) > 0; }
        if (removed) OnChange?.Invoke();
    }
}
