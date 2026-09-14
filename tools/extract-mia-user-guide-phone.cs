// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Xml.Linq;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/extract-mia-user-guide-phone.cs -- <page-6.svg> <output.svg>");
    return 2;
}

const string phoneClipId = "clip-0";
const string firstNonPhoneClipId = "clip-1";
const string phoneBoundsPath =
    "M 258.070312 66.800781 L 258.070312 239.601562 L 173.472656 239.601562 " +
    "L 173.472656 66.800781 Z M 258.070312 66.800781 ";

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
XDocument source = XDocument.Load(inputPath);
XElement sourceRoot = source.Root
    ?? throw new InvalidDataException("The page SVG has no root element.");
XNamespace svg = sourceRoot.Name.Namespace;

XElement sourceDefs = sourceRoot.Element(svg + "defs")
    ?? throw new InvalidDataException("The page SVG has no definitions.");
XElement phoneClip = sourceDefs
    .Elements(svg + "clipPath")
    .Single(element => element.Attribute("id")?.Value == phoneClipId);

List<XElement> pageElements = sourceRoot.Elements().SkipWhile(
    element => element.Name == svg + "defs").ToList();
int boundsIndex = pageElements.FindIndex(element =>
    element.Name == svg + "path" &&
    element.Attribute("d")?.Value == phoneBoundsPath);
if (boundsIndex < 0)
{
    throw new InvalidDataException("The Mia phone bounds were not found on the page.");
}

int endIndex = pageElements.FindIndex(boundsIndex + 1, element =>
    element.Name == svg + "g" &&
    element.Attribute("clip-path")?.Value == $"url(#{firstNonPhoneClipId})");
if (endIndex < 0)
{
    throw new InvalidDataException("The first non-phone vector group was not found.");
}

List<XElement> phoneElements = pageElements
    .Skip(boundsIndex + 1)
    .Take(endIndex - boundsIndex - 1)
    .Select(element => new XElement(element))
    .ToList();
if (phoneElements.Count < 100)
{
    throw new InvalidDataException(
        $"The extracted phone has only {phoneElements.Count.ToString(CultureInfo.InvariantCulture)} vector elements.");
}

XDocument output = new(
    new XDeclaration("1.0", "UTF-8", null),
    new XElement(
        svg + "svg",
        new XAttribute("width", "470"),
        new XAttribute("height", "958"),
        new XAttribute("viewBox", "173.472656 66.800781 84.597656 172.800781"),
        new XElement(svg + "title", "Sony Ericsson T68i user-guide vector"),
        new XElement(
            svg + "metadata",
            "Extracted directly from page 6 of the Sony Ericsson T68i user guide; no raster tracing."),
        new XElement(svg + "defs", new XElement(phoneClip)),
        new XElement(svg + "g", new XAttribute("id", "mia-user-guide-vector"), phoneElements)));

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
output.Save(outputPath, SaveOptions.DisableFormatting);
Console.WriteLine(
    $"Extracted {phoneElements.Count.ToString(CultureInfo.InvariantCulture)} phone vector elements to {outputPath}.");
return 0;
