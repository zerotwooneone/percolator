using System;
using R3;

namespace Percolator.Simulator.Features.Shell;

internal enum ToastSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

internal sealed record ToastMessage(
    string Title,
    string Message,
    ToastSeverity Severity = ToastSeverity.Informational);

internal interface IStatusToastService : IDisposable
{
    Observable<ToastMessage> Toasts { get; }
    void ShowToast(string title, string message, ToastSeverity severity = ToastSeverity.Informational);
}

internal sealed class StatusToastService : IStatusToastService
{
    private readonly Subject<ToastMessage> _subject = new();

    public Observable<ToastMessage> Toasts => _subject;

    public void ShowToast(string title, string message, ToastSeverity severity = ToastSeverity.Informational)
    {
        _subject.OnNext(new ToastMessage(title, message, severity));
    }

    public void Dispose()
    {
        _subject.OnCompleted();
        _subject.Dispose();
    }
}