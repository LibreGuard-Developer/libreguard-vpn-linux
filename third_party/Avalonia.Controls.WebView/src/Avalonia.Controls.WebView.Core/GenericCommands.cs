namespace Avalonia.Controls;

internal class GenericCommands : NativeWebViewCommandManager, IWebViewAdapterWithCommands
{
    internal GenericCommands(IWebViewAdapter adapter) : base(new Commands(adapter))
    {
    }

    private class Commands(IWebViewAdapter webView) : IWebViewAdapterWithCommands
    {
        public void Copy() => webView.InvokeScript("document.execCommand('copy')");
        public void Cut() => webView.InvokeScript("document.execCommand('cut')");
        public void Paste() => webView.InvokeScript("document.execCommand('paste')");
        public void SelectAll() => webView.InvokeScript("document.execCommand('selectAll')");
        public void Undo() => webView.InvokeScript("document.execCommand('undo')");
        public void Redo() => webView.InvokeScript("document.execCommand('redo')");
    }
}
