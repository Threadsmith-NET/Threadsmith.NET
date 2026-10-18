namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;

/// <summary>A single frozen clipboard capture; binary bytes stay inside the frontend until admission.</summary>
internal sealed record ClipboardCapture(string? Text, byte[]? Image);

/// <summary>Image-aware explicit capture using the existing bounded clipboard process adapter.</summary>
internal static partial class ClipboardReader
{
    /// <summary>Prefers an available image representation and otherwise retains ordinary text paste.</summary>
    internal static async Task<ClipboardCapture> CaptureAsync(TuiResourceLimits limits, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(limits.ClipboardTimeoutMilliseconds));
        if (OperatingSystem.IsWindows())
        {
            const string script = """
                Add-Type -AssemblyName System.Windows.Forms
                Add-Type -AssemblyName System.Drawing
                $out = [Console]::OpenStandardOutput()
                $data = [System.Windows.Forms.Clipboard]::GetDataObject()
                if ($null -ne $data -and $data.GetDataPresent([System.Windows.Forms.DataFormats]::Bitmap)) {
                    $image = $data.GetData([System.Windows.Forms.DataFormats]::Bitmap)
                    try {
                        if ($image.Width -gt 8192 -or $image.Height -gt 8192 -or ([long]$image.Width * $image.Height) -gt 16777216) { exit 3 }
                        $out.WriteByte(73)
                        $buffer = New-Object System.IO.MemoryStream
                        try {
                            $image.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
                            if ($buffer.Length -gt 5242880) { exit 3 }
                            $buffer.WriteTo($out)
                        } finally { $buffer.Dispose() }
                    } finally { $image.Dispose() }
                } elseif ($null -ne $data) {
                    $out.WriteByte(84)
                    $text = [string]$data.GetData([System.Windows.Forms.DataFormats]::UnicodeText)
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
                    $out.Write($bytes, 0, $bytes.Length)
                }
                """;
            var bytes = await ReadProcessBytesAsync(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-Command", script],
                ImageArtifactLimits.MaximumBytes + 1L,
                timeout.Token);
            return DecodeCapture(bytes, limits);
        }

        if (OperatingSystem.IsMacOS())
        {
            const string script = """
                ObjC.import('AppKit');
                const p = $.NSPasteboard.generalPasteboard;
                const o = $.NSFileHandle.fileHandleWithStandardOutput;
                let d = p.dataForType($.NSPasteboardTypePNG);
                if (!d || d.isNil()) d = p.dataForType($('public.jpeg'));
                if (d && !d.isNil()) {
                    o.writeData($('I').dataUsingEncoding($.NSUTF8StringEncoding)); o.writeData(d);
                } else {
                    let t = p.stringForType($.NSPasteboardTypeString);
                    o.writeData($('T').dataUsingEncoding($.NSUTF8StringEncoding));
                    if (t && !t.isNil()) o.writeData(t.dataUsingEncoding($.NSUTF8StringEncoding));
                }
                """;
            var bytes = await ReadProcessBytesAsync("/usr/bin/osascript", ["-l", "JavaScript", "-e", script], ImageArtifactLimits.MaximumBytes + 1L, timeout.Token);
            return DecodeCapture(bytes, limits);
        }

        foreach (var (executable, _) in ResolveLinuxCommands())
        {
            foreach (var mediaType in new[] { "image/png", "image/jpeg" })
            {
                var arguments = Path.GetFileName(executable) switch
                {
                    "wl-paste" => new[] { "--no-newline", "--type", mediaType },
                    "xclip" => ["-selection", "clipboard", "-o", "-t", mediaType],
                    _ => null,
                };
                if (arguments is not null && await ReadProcessBytesAsync(executable, arguments, ImageArtifactLimits.MaximumBytes, timeout.Token) is { Length: > 0 } image)
                {
                    return new ClipboardCapture(null, image);
                }
            }
        }

        return new ClipboardCapture(await ReadAsync(limits, timeout.Token), null);
    }

    private static ClipboardCapture DecodeCapture(byte[]? bytes, TuiResourceLimits limits)
    {
        if (bytes is not { Length: > 0 })
        {
            return new ClipboardCapture(null, null);
        }

        if (bytes[0] == (byte)'I')
        {
            return new ClipboardCapture(null, bytes[1..]);
        }

        if (bytes[0] != (byte)'T' || bytes.Length - 1 > limits.MaximumDraftBytes)
        {
            throw new InvalidDataException("Clipboard text exceeds the configured input limit.");
        }

        return new ClipboardCapture(_strictUtf8.GetString(bytes, 1, bytes.Length - 1), null);
    }
}
