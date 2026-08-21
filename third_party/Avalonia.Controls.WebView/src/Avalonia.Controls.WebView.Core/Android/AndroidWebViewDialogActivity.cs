#if ANDROID
using System;
using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Avalonia.Controls.Android;

[Activity(
    NoHistory = true,
    ExcludeFromRecents = true,
    TaskAffinity = "",
    FinishOnTaskLaunch = true,
    LaunchMode = LaunchMode.SingleTop)]
internal class AndroidWebViewDialogActivity : Activity
{
    public const string WebViewIdKey = "webview_id";
    public const string TitleIdKey = "title";
    private string? _webViewId;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _webViewId = Intent?.GetStringExtra(WebViewIdKey);
        if (string.IsNullOrEmpty(_webViewId))
        {
            Finish();
            return;
        }

        ActivityRegistry.Register(_webViewId, this);

        if (Intent?.GetStringExtra(TitleIdKey) is { } title)
        {
            Title = title;
        }

        var adapter = WebViewRegistry.Get(_webViewId)
                      ?? throw new InvalidOperationException("WebView not found in registry.");

        SetContentView(adapter.WebView);
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();


        if (!string.IsNullOrEmpty(_webViewId))
        {
            ActivityRegistry.Unregister(_webViewId);
            WebViewRegistry.OnDestroyed(_webViewId);
        }
    }

    public static class ActivityRegistry
    {
        private static readonly Dictionary<string, WeakReference<Activity>> s_activities = new();

        public static void Register(string id, Activity activity)
        {
            s_activities[id] = new WeakReference<Activity>(activity);
        }

        public static void FinishActivity(string id)
        {
            if (s_activities.Remove(id, out var weakRef) &&
                weakRef.TryGetTarget(out var activity))
            {
                activity.Finish();
            }
        }

        public static void Unregister(string id)
        {
            s_activities.Remove(id);
        }
    }

    public static class WebViewRegistry
    {
        private static readonly Dictionary<string, (AndroidWebViewAdapter, WeakReference<Action>)> s_registry = [];

        public static void Register(string id, AndroidWebViewAdapter webView, Action onClose)
        {
            s_registry[id] = (webView, new WeakReference<Action>(onClose));
        }

        public static void OnDestroyed(string id)
        {
            if (s_registry.Remove(id, out var tuple))
            {
                if (tuple.Item2.TryGetTarget(out var callback))
                {
                    callback.Invoke();
                }
                tuple.Item1.Dispose();
            }
        }

        public static AndroidWebViewAdapter? Get(string id)
        {
            s_registry.TryGetValue(id, out var tuple);
            return tuple.Item1;
        }
    }
}
#endif
