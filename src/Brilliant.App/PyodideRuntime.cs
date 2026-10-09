using Brilliant.Core.Content;
using Brilliant.Core.Python;
using Microsoft.JSInterop;

namespace Brilliant.App;

/// <summary>
/// The Python Runtime over the bundled Pyodide, running in a Web Worker inside the BlazorWebView
/// (wwwroot/python-runtime.js). Works offline: nothing is fetched from the network.
/// </summary>
public sealed class PyodideRuntime(IJSRuntime js) : IPythonRuntime, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<TestRunResult> RunTestsAsync(string code, string entrypoint, IReadOnlyList<CodeTest> tests,
        CancellationToken cancellationToken = default)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./python-runtime.js");
        var json = await _module.InvokeAsync<string>("runTests", cancellationToken, code, entrypoint, TestRunResultJson.SerializeTests(tests));
        return TestRunResultJson.Parse(json);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { } // the webview is already gone
    }
}
