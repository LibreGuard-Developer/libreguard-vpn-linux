# Avalonia Controls WebView source snapshot

This directory contains a repository-owned source snapshot of
`AvaloniaUI/Avalonia.Controls.WebView` version `12.0.1`.

- Upstream repository: https://github.com/AvaloniaUI/Avalonia.Controls.WebView
- Upstream tag: `12.0.1`
- Upstream commit: `8ae0a848102ad68a7cff54c48712871f78df7b9b`
- License: MIT; see `LICENSE` in this directory.
- Local target: `net10.0` only.

LibreGuard carries two Linux GTK offscreen corrections in
`GtkOffscreenWebViewAdapter.cs`: event-derived keyboard modifiers and RGBA to
premultiplied BGRA frame conversion. Keep those patches when refreshing this
snapshot.
