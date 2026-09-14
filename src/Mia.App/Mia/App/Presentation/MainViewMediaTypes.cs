// SPDX-License-Identifier: MIT

namespace Mia.App.Presentation;

public static class MainViewMediaTypes
{
    public static string Guess(string name) =>
        Path.GetExtension(name).ToUpperInvariant() switch
        {
            ".VCF" => "TEXT/X-VCARD",
            ".VCS" => "TEXT/X-VCALENDAR",
            ".TXT" => "text/plain",
            ".GIF" => "image/gif",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".PNG" => "image/png",
            ".EMY" => "audio/x-emelody",
            ".IMY" => "audio/i-melody",
            ".MID" or ".MIDI" => "audio/midi",
            _ => "application/octet-stream",
        };
}
