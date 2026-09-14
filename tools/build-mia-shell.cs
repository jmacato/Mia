// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run tools/build-mia-shell.cs -- <outline.svg> <output.svg>");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
XDocument source = XDocument.Load(inputPath);
XElement sourceRoot = source.Root
    ?? throw new InvalidDataException("The outline SVG has no root element.");
XNamespace svg = sourceRoot.Name.Namespace;
XElement sourceGroup = sourceRoot.Elements(svg + "g").Single();
List<XElement> sourcePaths = sourceGroup.Descendants(svg + "path").ToList();
if (sourcePaths.Count != 490)
{
    throw new InvalidDataException(
        $"Expected 490 handset paths, but found {sourcePaths.Count}.");
}

XElement body = FirstSubpathDefinition("phone-body", sourcePaths[0]);
XElement sideRocker = FirstSubpathDefinition("side-rocker", sourcePaths[1]);
XElement faceplate = GeometryDefinition("faceplate", sourcePaths[2]);
XElement screen = FirstSubpathDefinition("screen-window", sourcePaths[3]);
XElement shellDetails = GeometryDefinition("shell-details", sourcePaths[4]);
string[] shellDetailParts = SplitSubpaths(sourcePaths[4]);
if (shellDetailParts.Length != 9)
{
    throw new InvalidDataException(
        $"Expected 9 shell-detail subpaths, but found {shellDetailParts.Length}.");
}

string? shellDetailTransform = sourcePaths[4].Attribute("transform")?.Value;
XElement topCornerLeft = GeometryDefinitionFromData(
    "top-corner-left", shellDetailParts[1], shellDetailTransform);
XElement topCornerRight = GeometryDefinitionFromData(
    "top-corner-right", shellDetailParts[2], shellDetailTransform);
XElement joystickOuter = FirstSubpathDefinition("joystick-outer", sourcePaths[5]);
XElement joystickInner = FirstSubpathDefinition("joystick-inner", sourcePaths[6]);

string[] keypadParts = SplitSubpaths(sourcePaths[7]);
if (keypadParts.Length != 28)
{
    throw new InvalidDataException(
        $"Expected 28 keypad subpaths, but found {keypadParts.Length}.");
}

string? keypadTransform = sourcePaths[7].Attribute("transform")?.Value;
const string AuthorizedSeamConnectorGeometry =
    "M 395.719939 -317.45926 " +
    "C 395.719939 -317.45926 395.832814 -318.208049 396.03521 -319.540973 " +
    "C 396.400000 -321.200000 397.080000 -324.250000 397.448093 -325.915483 " +
    "M 398.20708 -331.011956 " +
    "C 398.450000 -332.650000 398.820000 -335.200000 399.05948 -336.786651 " +
    "M 399.666669 -340.950077 " +
    "C 399.920000 -342.750000 400.400000 -346.050000 400.659189 -347.865597 " +
    "M 401.172965 -351.495854 " +
    "C 401.430000 -353.350000 401.910000 -356.800000 402.169378 -358.705402 " +
    "M 402.531356 -361.406532 " +
    "C 402.850000 -363.900000 403.350000 -367.700000 403.671782 -370.415527 " +
    "M 455.819986 -319.540973 " +
    "C 455.580000 -321.130000 455.150000 -324.000000 454.909202 -325.601854 " +
    "M 454.111293 -330.968832 " +
    "C 453.870000 -332.620000 453.470000 -335.350000 453.223863 -337.017952 " +
    "M 452.694518 -340.640368 " +
    "C 452.430000 -342.470000 451.920000 -346.030000 451.651399 -347.885199 " +
    "M 451.188222 -351.178304 " +
    "C 450.920000 -353.080000 450.450000 -356.570000 450.176241 -358.478021 " +
    "M 449.77534 -361.465338 " +
    "C 449.460000 -364.080000 448.960000 -367.740000 448.650483 -370.431209 ";
XElement bodyPanelSeamConnectors = GeometryDefinitionFromData(
    "body-panel-seam-connectors", AuthorizedSeamConnectorGeometry, keypadTransform);
List<XElement> keyDefinitions = keypadParts
    .Take(16)
    .Select((geometry, index) => GeometryDefinitionFromData(
        $"key-{index}", geometry, keypadTransform))
    .ToList();
List<XElement> keypadSeamDefinitions = keypadParts
    .Skip(16)
    .Take(11)
    .Select((geometry, index) => GeometryDefinitionFromData(
        $"keypad-seam-{index}", geometry, keypadTransform))
    .ToList();

XElement speakerOuter = FirstSubpathDefinition("speaker-outer", sourcePaths[8]);
string[] speakerParts = SplitSubpaths(sourcePaths[9]);
if (speakerParts.Length != 9)
{
    throw new InvalidDataException(
        $"Expected 9 speaker subpaths, but found {speakerParts.Length}.");
}

string? speakerTransform = sourcePaths[9].Attribute("transform")?.Value;
XElement speakerInner = GeometryDefinitionFromData(
    "speaker-inner", speakerParts[0], speakerTransform);
List<XElement> speakerSlotDefinitions = speakerParts
    .Skip(1)
    .Select((geometry, index) => GeometryDefinitionFromData(
        $"speaker-slot-{index}", geometry, speakerTransform))
    .ToList();

List<XElement> wordmarkDefinitions = sourcePaths
    .Skip(10)
    .Take(13)
    .Select((path, index) => GeometryDefinition($"wordmark-{index}", path))
    .ToList();
List<XElement> legendDefinitions = sourcePaths
    .Skip(429)
    .Select((path, index) => GeometryDefinition($"legend-{index}", path))
    .ToList();

XElement definitions = XElement.Parse(
    """
    <defs xmlns="http://www.w3.org/2000/svg">
      <!-- Colors sampled from a front-facing Arctic Blue Sony Ericsson T68i photograph. -->
      <radialGradient id="body-edge" cx="39%" cy="15%" r="96%" fx="31%" fy="5%">
        <stop offset="0%" stop-color="#5d6268"/>
        <stop offset="11%" stop-color="#f5f5f1"/>
        <stop offset="35%" stop-color="#d6d6d2"/>
        <stop offset="72%" stop-color="#a3a4a3"/>
        <stop offset="100%" stop-color="#555a60"/>
      </radialGradient>
      <radialGradient id="body-field" cx="40%" cy="18%" r="96%" fx="30%" fy="6%">
        <stop offset="0%" stop-color="#e4e5e2"/>
        <stop offset="20%" stop-color="#cacbc8"/>
        <stop offset="53%" stop-color="#a7a9aa"/>
        <stop offset="81%" stop-color="#7b7f83"/>
        <stop offset="100%" stop-color="#50565c"/>
      </radialGradient>
      <radialGradient id="body-sheen" cx="27%" cy="8%" r="84%" fx="21%" fy="2%">
        <stop offset="0%" stop-color="#fff" stop-opacity=".58"/>
        <stop offset="22%" stop-color="#fff" stop-opacity=".24"/>
        <stop offset="58%" stop-color="#fff" stop-opacity="0"/>
        <stop offset="100%" stop-color="#24313b" stop-opacity=".2"/>
      </radialGradient>
      <radialGradient id="body-right-specular" cx="96%" cy="42%" r="72%" fx="98%" fy="38%">
        <stop offset="0%" stop-color="#e5f7ff" stop-opacity=".62"/>
        <stop offset="16%" stop-color="#fff" stop-opacity=".34"/>
        <stop offset="44%" stop-color="#f7fbfd" stop-opacity=".08"/>
        <stop offset="100%" stop-color="#fff" stop-opacity="0"/>
      </radialGradient>
      <radialGradient id="lcd-surround-blue" gradientUnits="userSpaceOnUse"
                      cx="418" cy="-251" r="116" fx="412" fy="-235">
        <stop offset="0%" stop-color="#8ea4cb"/>
        <stop offset="9%" stop-color="#8394b8"/>
        <stop offset="30%" stop-color="#4b5f89"/>
        <stop offset="58%" stop-color="#3d5482"/>
        <stop offset="82%" stop-color="#3a4a6e"/>
        <stop offset="100%" stop-color="#2d3854"/>
      </radialGradient>
      <radialGradient id="blue-sheen" gradientUnits="userSpaceOnUse"
                      cx="409" cy="-231" r="78" fx="406" fy="-225">
        <stop offset="0%" stop-color="#fff" stop-opacity=".58"/>
        <stop offset="22%" stop-color="#edf8ff" stop-opacity=".27"/>
        <stop offset="58%" stop-color="#fff" stop-opacity="0"/>
        <stop offset="100%" stop-color="#112d49" stop-opacity=".2"/>
      </radialGradient>
      <radialGradient id="blue-right-specular" gradientUnits="userSpaceOnUse"
                      cx="457" cy="-282" r="68" fx="460" fy="-274">
        <stop offset="0%" stop-color="#d1f5fd" stop-opacity=".76"/>
        <stop offset="17%" stop-color="#b5dcf7" stop-opacity=".46"/>
        <stop offset="48%" stop-color="#98afcf" stop-opacity=".1"/>
        <stop offset="100%" stop-color="#fff" stop-opacity="0"/>
      </radialGradient>
      <radialGradient id="navigation-well-shadow" gradientUnits="userSpaceOnUse"
                      cx="425.95" cy="-316.6" r="31" fx="425.95" fy="-316.6">
        <stop offset="0%" stop-color="#061a31" stop-opacity=".4"/>
        <stop offset="34%" stop-color="#102e4b" stop-opacity=".27"/>
        <stop offset="72%" stop-color="#274b6c" stop-opacity=".08"/>
        <stop offset="100%" stop-color="#fff" stop-opacity="0"/>
      </radialGradient>
      <radialGradient id="key-silver" cx="31%" cy="14%" r="94%" fx="23%" fy="5%">
        <stop offset="0%" stop-color="#fff"/>
        <stop offset="18%" stop-color="#f4f3ee"/>
        <stop offset="48%" stop-color="#cfceca"/>
        <stop offset="78%" stop-color="#949596"/>
        <stop offset="100%" stop-color="#5c6064"/>
      </radialGradient>
      <radialGradient id="speaker-silver" cx="34%" cy="16%" r="92%" fx="24%" fy="5%">
        <stop offset="0%" stop-color="#fff"/>
        <stop offset="30%" stop-color="#e7e6e1"/>
        <stop offset="68%" stop-color="#a6a7a6"/>
        <stop offset="100%" stop-color="#555b60"/>
      </radialGradient>
      <radialGradient id="top-cap-aperture" gradientUnits="userSpaceOnUse"
                      cx="425.6" cy="-218.8" r="29.4"
                      gradientTransform="matrix(1 0 0 .24 0 -166.29)">
        <stop offset="0%" stop-color="#fff" stop-opacity="1"/>
        <stop offset="88%" stop-color="#fff" stop-opacity="1"/>
        <stop offset="100%" stop-color="#fff" stop-opacity="0"/>
      </radialGradient>
      <radialGradient id="joystick-blue" cx="50%" cy="48%" r="54%" fx="50%" fy="48%">
        <stop offset="0%" stop-color="#03162f"/>
        <stop offset="42%" stop-color="#0d3157"/>
        <stop offset="72%" stop-color="#416c94"/>
        <stop offset="91%" stop-color="#b8ccd8"/>
        <stop offset="100%" stop-color="#596c7b"/>
      </radialGradient>
      <filter id="shell-soft" x="-25%" y="-25%" width="150%" height="150%">
        <feGaussianBlur stdDeviation=".16"/>
      </filter>
      <filter id="key-shadow" x="-25%" y="-35%" width="150%" height="170%">
        <feOffset dy=".22" in="SourceAlpha" result="shadow-offset"/>
        <feGaussianBlur in="shadow-offset" stdDeviation=".18" result="shadow-blur"/>
        <feColorMatrix in="shadow-blur" result="shadow" values="0 0 0 0 0.10 0 0 0 0 0.11 0 0 0 0 0.12 0 0 0 .34 0"/>
        <feMerge>
          <feMergeNode in="shadow"/>
          <feMergeNode in="SourceGraphic"/>
        </feMerge>
      </filter>
      <filter id="wordmark-float" x="-35%" y="-80%" width="170%" height="260%"
              color-interpolation-filters="sRGB">
        <!-- Keep the requested 2 px depth, but use a restrained half-pixel spread. -->
        <feMorphology in="SourceAlpha" operator="dilate" radius=".09" result="spread"/>
        <feOffset in="spread" dx=".255" dy=".255" result="depth"/>
        <feGaussianBlur in="depth" stdDeviation=".1" result="soft-depth"/>
        <feColorMatrix in="soft-depth" result="shadow"
                       values="0 0 0 0 .035 0 0 0 0 .075 0 0 0 0 .12 0 0 0 .23 0"/>
        <feMerge><feMergeNode in="shadow"/><feMergeNode in="SourceGraphic"/></feMerge>
      </filter>
      <filter id="screen-inset" x="-25%" y="-25%" width="150%" height="150%">
        <feOffset dy="-.5" in="SourceAlpha" result="upper"/>
        <feGaussianBlur in="upper" stdDeviation=".65" result="upper-blur"/>
        <feColorMatrix in="upper-blur" result="upper-shadow" values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 .54 0"/>
        <feMerge><feMergeNode in="upper-shadow"/><feMergeNode in="SourceGraphic"/></feMerge>
      </filter>
      <clipPath id="body-clip"><use href="#phone-body"/></clipPath>
      <mask id="top-cap-mask" maskUnits="userSpaceOnUse" maskContentUnits="userSpaceOnUse"
            x="181" y="65" width="70" height="13" style="mask-type:alpha">
        <use href="#phone-body" fill="url(#top-cap-aperture)"/>
      </mask>
    </defs>
    """);
definitions.Add(body);
definitions.Add(sideRocker);
definitions.Add(faceplate);
definitions.Add(screen);
definitions.Add(shellDetails);
definitions.Add(topCornerLeft);
definitions.Add(topCornerRight);
definitions.Add(joystickOuter);
definitions.Add(joystickInner);
definitions.Add(keyDefinitions);
definitions.Add(keypadSeamDefinitions);
definitions.Add(bodyPanelSeamConnectors);
definitions.Add(speakerOuter);
definitions.Add(speakerInner);
definitions.Add(speakerSlotDefinitions);
definitions.Add(wordmarkDefinitions);
definitions.Add(legendDefinitions);

XElement shell = XElement.Parse(
    """
    <g xmlns="http://www.w3.org/2000/svg" id="mia-shell" clip-path="url(#body-clip)">
      <use href="#phone-body" fill="#655e5a"/>
      <use href="#phone-body" fill="url(#body-edge)" filter="url(#shell-soft)"
           transform="translate(215.77 153.2) scale(.986) translate(-215.77 -153.2)"/>
      <use href="#phone-body" fill="url(#body-field)"
           transform="translate(215.77 153.2) scale(.957 .974) translate(-215.77 -153.2)"/>
      <use href="#phone-body" fill="url(#body-sheen)"
           transform="translate(215.77 153.2) scale(.957 .974) translate(-215.77 -153.2)"/>
      <use href="#phone-body" fill="url(#body-right-specular)"
           transform="translate(215.77 153.2) scale(.957 .974) translate(-215.77 -153.2)"/>

      <use href="#phone-body" fill="url(#lcd-surround-blue)" mask="url(#top-cap-mask)"/>
      <use href="#phone-body" fill="url(#blue-sheen)" mask="url(#top-cap-mask)"/>
      <use href="#phone-body" fill="url(#blue-right-specular)" mask="url(#top-cap-mask)"/>
      <use href="#faceplate" fill="url(#lcd-surround-blue)" stroke="#3a3b45" stroke-width=".13"/>
      <use href="#faceplate" fill="url(#blue-sheen)"/>
      <use href="#faceplate" fill="url(#blue-right-specular)"/>
      <use href="#faceplate" fill="url(#navigation-well-shadow)"/>
      <use href="#top-corner-left" fill="url(#key-silver)" stroke="#6e7174" stroke-width=".1"/>
      <use href="#top-corner-right" fill="url(#key-silver)" stroke="#6e7174" stroke-width=".1"/>
      <use href="#shell-details" fill="none" stroke="#fff" stroke-width=".15"
           opacity=".32" transform="translate(.07 .14)"/>
      <use href="#shell-details" fill="none" stroke="#323b45" stroke-width=".12" opacity=".66"/>

      <g id="body-panel-seam-engraving" fill="none">
        <g id="body-panel-seam-highlight" stroke="#fff" stroke-width=".17"
           opacity=".64" transform="translate(.07 .14)">
          <use href="#body-panel-seam-connectors"/>
        </g>
        <g id="body-panel-seam-groove" stroke="#686d71" stroke-width=".16" opacity=".86">
          <use href="#body-panel-seam-connectors"/>
        </g>
      </g>

      <use href="#screen-window" fill="none" stroke="url(#lcd-surround-blue)" stroke-width=".9" opacity=".9"/>
      <use href="#screen-window" fill="none" stroke="url(#blue-right-specular)" stroke-width=".9"/>
      <use href="#screen-window" fill="#000" stroke="#34404a" stroke-width=".36" filter="url(#screen-inset)"/>

      <g id="earpiece">
        <use href="#speaker-outer" fill="#3e3e3c" stroke="#5a5e62" stroke-width=".12" filter="url(#key-shadow)"/>
        <use href="#speaker-inner" fill="url(#lcd-surround-blue)"/>
        <use href="#speaker-inner" fill="url(#blue-sheen)"/>
        <g id="speaker-slots" fill="none" stroke="#d1d1d1" stroke-width=".11" opacity=".7">
        </g>
      </g>

      <g id="sony-ericsson-wordmark" fill="#d8d7d3" filter="url(#wordmark-float)">
      </g>

      <g id="navigation-keys" fill="url(#key-silver)" stroke="#716d69" stroke-width=".11" filter="url(#key-shadow)">
      </g>
      <g id="numeric-keys" fill="url(#key-silver)" stroke="#716d69" stroke-width=".11" filter="url(#key-shadow)">
      </g>
      <g id="joystick">
        <use href="#joystick-outer" fill="url(#speaker-silver)" stroke="#3f4b55" stroke-width=".16"/>
        <use href="#joystick-inner" fill="url(#joystick-blue)" stroke="#233b52" stroke-width=".08"/>
      </g>

      <g id="key-legends" fill="#3d3c3f">
      </g>
      <g id="call-legend" fill="#566e91">
        <use href="#legend-4"/>
      </g>
      <g id="end-legend" fill="#c26a70">
        <use href="#legend-5"/><use href="#legend-6"/>
      </g>

      <use href="#side-rocker" fill="url(#key-silver)" stroke="#716d69" stroke-width=".11" filter="url(#key-shadow)"/>
      <use href="#phone-body" fill="none" stroke="#3e3f48" stroke-width=".16" opacity=".72"/>
    </g>
    """);

AddUses(shell, svg, "speaker-slots", "speaker-slot", speakerSlotDefinitions.Count);
AddUses(shell, svg, "sony-ericsson-wordmark", "wordmark", wordmarkDefinitions.Count);
AddUses(shell, svg, "body-panel-seam-highlight", "keypad-seam", keypadSeamDefinitions.Count);
AddUses(shell, svg, "body-panel-seam-groove", "keypad-seam", keypadSeamDefinitions.Count);

XElement navigationKeys = FindGroup(shell, svg, "navigation-keys");
foreach (int index in new[] { 0, 1, 2, 7 })
{
    navigationKeys.Add(Use(svg, $"key-{index}"));
}

XElement numericKeys = FindGroup(shell, svg, "numeric-keys");
foreach (int index in new[] { 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15 })
{
    numericKeys.Add(Use(svg, $"key-{index}"));
}

XElement keyLegends = FindGroup(shell, svg, "key-legends");
for (int index = 0; index < legendDefinitions.Count; index++)
{
    if (index is 4 or 5 or 6)
    {
        continue;
    }

    keyLegends.Add(Use(svg, $"legend-{index}"));
}

XDocument output = new(
    new XDeclaration("1.0", "UTF-8", null),
    new XElement(
        svg + "svg",
        new XAttribute("width", "470"),
        new XAttribute("height", "958"),
        new XAttribute("viewBox", "173.472656 66.800781 84.597656 172.800781"),
        new XAttribute("role", "img"),
        new XAttribute("aria-label", "Sony Ericsson T68i handset shell"),
        new XElement(svg + "title", "Sony Ericsson T68i"),
        new XElement(
            svg + "metadata",
            "All handset geometry except one user-authorized seam-completion path is extracted from page 6 " +
            "of the Sony Ericsson T68i user guide. That path mirrors the missing upper-left seam segment " +
            "and joins the gaps between the vertical PDF body-seam fragments. " +
            "No raster image, photo tracing, or PDF illustration overlay is present. Colors and specular " +
            "highlights use supplied Arctic Blue Mia photographs as references; no photograph is embedded."),
        definitions,
        shell));

List<string> sourceGeometry = sourcePaths
    .Select(path => path.Attribute("d")?.Value)
    .OfType<string>()
    .ToList();
foreach (XElement path in output.Descendants(svg + "path"))
{
    string geometry = path.Attribute("d")?.Value
        ?? throw new InvalidDataException("An output path has no geometry.");
    if (path.Attribute("id")?.Value == "body-panel-seam-connectors")
    {
        if (geometry != AuthorizedSeamConnectorGeometry)
        {
            throw new InvalidDataException(
                "The authorized body-seam connector geometry changed unexpectedly.");
        }

        continue;
    }

    if (!sourceGeometry.Any(candidate => candidate.Contains(geometry, StringComparison.Ordinal)))
    {
        throw new InvalidDataException(
            $"Output path '{path.Attribute("id")?.Value}' is not PDF-derived.");
    }
}

int authorizedPathCount = output
    .Descendants(svg + "path")
    .Count(path => path.Attribute("id")?.Value == "body-panel-seam-connectors");
if (authorizedPathCount != 1)
{
    throw new InvalidDataException(
        $"Expected one user-authorized seam-connector path, but found {authorizedPathCount}.");
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
output.Save(outputPath, SaveOptions.DisableFormatting);
File.WriteAllText(
    outputPath,
    File.ReadAllText(outputPath).ReplaceLineEndings("\n"),
    new UTF8Encoding(false));
Console.WriteLine(
    $"Built {outputPath}: {output.Descendants(svg + "path").Count() - authorizedPathCount} " +
    "PDF-derived paths, one user-authorized seam-completion path, zero raster images.");
return 0;

static string[] SplitSubpaths(XElement source) => Regex
    .Split(source.Attribute("d")?.Value
        ?? throw new InvalidDataException("A source path has no geometry."), @"(?=M )")
    .Where(part => !string.IsNullOrWhiteSpace(part))
    .ToArray();

static XElement FirstSubpathDefinition(string id, XElement source)
{
    string geometry = SplitSubpaths(source)
        .First(part => Regex.IsMatch(part, @"Z\s*$"));
    return GeometryDefinitionFromData(id, geometry, source.Attribute("transform")?.Value);
}

static XElement GeometryDefinition(string id, XElement source) => GeometryDefinitionFromData(
    id,
    source.Attribute("d")?.Value
        ?? throw new InvalidDataException($"The {id} path has no geometry."),
    source.Attribute("transform")?.Value);

static XElement GeometryDefinitionFromData(string id, string geometry, string? transform)
{
    XElement path = new(
        XName.Get("path", "http://www.w3.org/2000/svg"),
        new XAttribute("id", id),
        new XAttribute("d", geometry));
    if (!string.IsNullOrWhiteSpace(transform))
    {
        path.SetAttributeValue("transform", transform);
    }

    return path;
}

static XElement FindGroup(XElement shell, XNamespace svg, string id) => shell
    .Descendants(svg + "g")
    .Single(element => element.Attribute("id")?.Value == id);

static XElement Use(XNamespace svg, string id) => new(
    svg + "use",
    new XAttribute("href", $"#{id}"));

static void AddUses(
    XElement shell,
    XNamespace svg,
    string groupId,
    string definitionPrefix,
    int count)
{
    XElement group = FindGroup(shell, svg, groupId);
    for (int index = 0; index < count; index++)
    {
        group.Add(Use(svg, $"{definitionPrefix}-{index}"));
    }
}
