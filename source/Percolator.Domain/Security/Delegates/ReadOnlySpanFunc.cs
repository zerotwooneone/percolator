namespace Percolator.Domain.Security.Delegates;

/// <summary>
/// A delegate that accepts a <see cref=\"ReadOnlySpan{T}\"/> and returns a result of type <typeparamref name=\"TResult\"/>.
/// Because <see cref=\"ReadOnlySpan{T}\"/> is a ref struct, it cannot be used with standard generic delegates such as <see cref=\"System.Func{T, TResult}\"/>.
/// </summary>
/// <typeparam name=\"T\">The element type of the read-only span.</typeparam>
/// <typeparam name=\"TResult\">The return type produced from the span.</typeparam>
/// <param name=\"span\">The read-only span.</param>
/// <returns>The result of the operation.</returns>
public delegate TResult ReadOnlySpanFunc<T, out TResult>(ReadOnlySpan<T> span);
