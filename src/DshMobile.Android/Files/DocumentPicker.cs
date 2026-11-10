using Android.Content;
using Android.Database;
using Android.Provider;
using Android.Webkit;

namespace DshMobile.App.Files;

/// <summary>System document picker (SAF) helpers. No storage permission is needed: grants are per-pick.</summary>
public static class DocumentPicker
{
    /// <summary>ACTION_OPEN_DOCUMENT for the given HTML accept types (MIME types or ".ext").</summary>
    public static Intent OpenIntent(IEnumerable<string>? acceptTypes, bool multiple)
    {
        var mimes = ToMimeTypes(acceptTypes);
        var intent = new Intent(Intent.ActionOpenDocument)
            .AddCategory(Intent.CategoryOpenable)
            .SetType(mimes.Length == 1 ? mimes[0] : "*/*")
            .PutExtra(Intent.ExtraAllowMultiple, multiple);
        if (mimes.Length > 1)
        {
            intent.PutExtra(Intent.ExtraMimeTypes, mimes);
        }
        return intent;
    }

    /// <summary>ACTION_CREATE_DOCUMENT: the user chooses where an exported file goes.</summary>
    public static Intent CreateIntent(string fileName) =>
        new Intent(Intent.ActionCreateDocument)
            .AddCategory(Intent.CategoryOpenable)
            .SetType(MimeForName(fileName))
            .PutExtra(Intent.ExtraTitle, fileName);

    /// <summary>URIs from a picker result (single Data or multi-select ClipData).</summary>
    public static Android.Net.Uri[] ResultUris(Intent? data)
    {
        if (data is null)
        {
            return [];
        }
        if (data.ClipData is { } clip && clip.ItemCount > 0)
        {
            return Enumerable.Range(0, clip.ItemCount).Select(i => clip.GetItemAt(i)!.Uri).OfType<Android.Net.Uri>().ToArray();
        }
        return data.Data is { } uri ? [uri] : [];
    }

    public static string DisplayName(ContentResolver resolver, Android.Net.Uri uri)
    {
        using ICursor? c = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
        if (c is not null && c.MoveToFirst() && !c.IsNull(0))
        {
            return c.GetString(0) ?? "file";
        }
        return uri.LastPathSegment ?? "file";
    }

    public static string[] ToMimeTypes(IEnumerable<string>? acceptTypes)
    {
        var result = new List<string>();
        foreach (var raw in acceptTypes ?? [])
        {
            foreach (var t in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (t.Contains('/'))
                {
                    result.Add(t.ToLowerInvariant());
                }
                else if (t.StartsWith('.') && MimeTypeMap.Singleton?.GetMimeTypeFromExtension(t[1..].ToLowerInvariant()) is { } m)
                {
                    result.Add(m);
                }
                else
                {
                    return ["*/*"]; // unknown extension: don't silently hide files the page accepts
                }
            }
        }
        return result.Count == 0 ? ["*/*"] : result.Distinct().ToArray();
    }

    public static string MimeForName(string name)
    {
        var ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return (ext.Length > 0 ? MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) : null) ?? "application/octet-stream";
    }
}
