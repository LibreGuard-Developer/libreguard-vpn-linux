using System;
using System.Threading.Tasks;

#if AVALONIA
namespace Avalonia.Controls;
#elif WPF
namespace Avalonia.Xpf.Controls;
#endif

internal class EmptyDisposable : IDisposable, IAsyncDisposable
{
    public static EmptyDisposable Instance { get; } = new();

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        return default;
    }
}
