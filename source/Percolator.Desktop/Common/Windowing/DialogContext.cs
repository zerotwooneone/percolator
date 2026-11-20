using System;

namespace Percolator.Desktop.Common.Windowing;

internal class DialogContext : IDialogContext
{
    private readonly Action _closeAction;

    public bool IsClosed { get; private set; }

    public DialogContext(Action closeAction)
    {
        _closeAction = closeAction ?? throw new ArgumentNullException(nameof(closeAction));
    }

    public void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        _closeAction();
    }

    public void Cancel()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        _closeAction();
    }
}

internal sealed class DialogContext<TData> : DialogContext, IDialogContext<TData>
{
    public TData Data { get; }

    public DialogContext(TData data, Action closeAction) : base(closeAction)
    {
        Data = data;
    }
}

internal class DialogResultContext<TResult> : IDialogResultContext<TResult>
{
    private readonly Action _closeAction;

    public TResult? Result { get; private set; }
    public bool IsClosed { get; private set; }

    public DialogResultContext(Action closeAction)
    {
        _closeAction = closeAction ?? throw new ArgumentNullException(nameof(closeAction));
    }

    public void Close(TResult result)
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        Result = result;
        _closeAction();
    }

    public void Cancel()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        Result = default;
        _closeAction();
    }
}

internal sealed class DialogContext<TData, TResult> : DialogResultContext<TResult>, IDialogContext<TData, TResult>
{
    public TData Data { get; }

    public DialogContext(TData data, Action closeAction) : base(closeAction)
    {
        Data = data;
    }
}
