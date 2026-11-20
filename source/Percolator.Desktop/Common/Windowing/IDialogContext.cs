namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Context passed into a parameterless, void-returning dialog ViewModel providing lifecycle control.
/// </summary>
public interface IDialogContext
{
    /// <summary>Closes the dialog affirmatively.</summary>
    void Close();

    /// <summary>Cancels or dismisses the dialog without affirmative completion.</summary>
    void Cancel();
}

/// <summary>
/// Context passed into a void-returning dialog ViewModel providing strongly typed input parameters.
/// </summary>
/// <typeparam name="TData">The input parameter type.</typeparam>
public interface IDialogContext<out TData> : IDialogContext
{
    /// <summary>The input data supplied when launching the dialog.</summary>
    TData Data { get; }
}

/// <summary>
/// Context passed into a result-returning dialog ViewModel providing lifecycle control.
/// </summary>
/// <typeparam name="TResult">The result type returned upon affirmative closure.</typeparam>
public interface IDialogResultContext<in TResult>
{
    /// <summary>Closes the dialog affirmatively and sets the return result.</summary>
    void Close(TResult result);

    /// <summary>Cancels or dismisses the dialog without a result.</summary>
    void Cancel();
}

/// <summary>
/// Context passed into a result-returning dialog ViewModel providing strongly typed input parameters and lifecycle control.
/// </summary>
/// <typeparam name="TData">The input parameter type.</typeparam>
/// <typeparam name="TResult">The result type returned upon affirmative closure.</typeparam>
public interface IDialogContext<out TData, in TResult> : IDialogResultContext<TResult>
{
    /// <summary>The input data supplied when launching the dialog.</summary>
    TData Data { get; }
}
