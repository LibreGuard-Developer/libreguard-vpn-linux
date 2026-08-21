using System;
using System.Collections.Generic;
using System.Net;
using Avalonia.Logging;

namespace Avalonia.Controls.Macios.Interop;

internal class NSHTTPCookie : NSObject
{
    private static readonly IntPtr s_class = Libobjc.objc_getClass("NSHTTPCookie");
    private static readonly IntPtr s_cookieWithProperties = Libobjc.sel_getUid("cookieWithProperties:");
    private static readonly IntPtr s_properties = Libobjc.sel_getUid("properties");

    private static readonly NSString s_valueKey = NSString.Create("Value");
    private static readonly NSString s_domainKey = NSString.Create("Domain");
    private static readonly NSString s_nameKey = NSString.Create("Name");
    private static readonly NSString s_pathKey = NSString.Create("Path");
    private static readonly NSString s_secureKey = NSString.Create("Secure");
    private static readonly NSString s_httpOnlyKey = NSString.Create("HttpOnly");
    private static readonly NSString s_expiresKey = NSString.Create("Expires");

    public NSHTTPCookie(IntPtr handle, bool owns) : base(handle, owns)
    {
    }

    public static NSHTTPCookie Create(Cookie cookie)
    {
        var names = new List<NSObject>();
        var values = new List<NSObject>();

        void Add(NSObject value, NSObject key)
        {
            names.Add(value);
            values.Add(key);
        }

        if (string.IsNullOrEmpty(cookie.Value)
            || string.IsNullOrEmpty(cookie.Domain)
            || string.IsNullOrEmpty(cookie.Name))
        {
            throw new InvalidOperationException(
                "To successfully create a cookie, you must provide values for (at least) the Cookie.Name and Cookie.Value keys, and Cookie.Domain key.");
        }

        Add(s_valueKey, NSString.Create(cookie.Value));
        Add(s_domainKey, NSString.Create(cookie.Domain));
        Add(s_nameKey, NSString.Create(cookie.Name));
        Add(s_pathKey, NSString.Create(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path));
        if (cookie.Secure)
            Add(s_secureKey, NSString.Create("TRUE"));
        if (cookie.HttpOnly)
            Add(s_httpOnlyKey, NSString.Create("TRUE"));
        if (cookie.Expires != DateTime.MinValue)
            Add(s_expiresKey, NSDate.FromDateTimeOffset(cookie.Expires));

        try
        {
            using var dict = NSDictionary.WithObjects(values, names, (uint)names.Count);
            var handle = Libobjc.intptr_objc_msgSend(s_class, s_cookieWithProperties, dict.Handle);
            if (handle == default)
                throw new InvalidOperationException("NSHTTPCookie creation failed.");

            return new NSHTTPCookie(handle, true);
        }
        finally
        {
            foreach (var value in values)
            {
                value.Dispose();
            }
        }
    }

    public Cookie? ToSystemCookie()
    {
        var props = NSDictionary.AsStringDictionary(Libobjc.intptr_objc_msgSend(Handle, s_properties));
        object? GetValueOrDefault(string key)
        {
            return props.TryGetValue(key, out var prop) ? prop : null;
        }

        try
        {
            return new Cookie(
                (string)props["Name"]!,
                (string?)GetValueOrDefault("Value"),
                (string?)GetValueOrDefault("Path"),
                (string?)GetValueOrDefault("Domain"))
            {
                Expires =
                    (GetValueOrDefault("Expires") as DateTimeOffset? ?? DateTimeOffset.MinValue).UtcDateTime,
                Secure = (string?)GetValueOrDefault("Secure") == "TRUE",
                HttpOnly = (string?)GetValueOrDefault("HttpOnly") == "TRUE",
                Version = int.TryParse((string?)GetValueOrDefault("Version"), out var v) ? v : 0
            };
        }
        catch (Exception ex)
        {
            Logger.TryGet(LogEventLevel.Warning, "WebView")
                ?.Log(this, "Unable to read NSHTTPCookie:\r\n{Exception}", ex);
            return null;
        }
    }
}
