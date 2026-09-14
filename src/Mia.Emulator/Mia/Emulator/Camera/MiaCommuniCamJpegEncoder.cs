// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Camera;

/// <summary>
/// Portable baseline-JFIF encoder for native MCA-25 still images. It keeps
/// image encoding in the accessory model and does not depend on a host camera
/// or graphics backend.
/// </summary>
internal static class MiaCommuniCamJpegEncoder
{
    static readonly byte[] LuminanceQuantization =
    [
        8, 6, 5, 8, 12, 20, 26, 31,
        6, 6, 7, 10, 13, 29, 30, 28,
        7, 7, 8, 12, 20, 29, 35, 28,
        7, 9, 11, 15, 26, 44, 40, 31,
        9, 11, 19, 28, 34, 55, 52, 39,
        12, 18, 28, 32, 41, 52, 57, 46,
        25, 32, 39, 44, 52, 61, 60, 51,
        36, 46, 48, 49, 56, 50, 52, 50,
    ];

    static readonly byte[] ChrominanceQuantization =
    [
        9, 9, 12, 24, 50, 50, 50, 50,
        9, 11, 13, 33, 50, 50, 50, 50,
        12, 13, 28, 50, 50, 50, 50, 50,
        24, 33, 50, 50, 50, 50, 50, 50,
        50, 50, 50, 50, 50, 50, 50, 50,
        50, 50, 50, 50, 50, 50, 50, 50,
        50, 50, 50, 50, 50, 50, 50, 50,
        50, 50, 50, 50, 50, 50, 50, 50,
    ];

    static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    static readonly byte[] DcLuminanceCounts =
        [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    static readonly byte[] DcChrominanceCounts =
        [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    static readonly byte[] DcValues =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    static readonly byte[] AcLuminanceCounts =
    [
        0, 2, 1, 3, 3, 2, 4, 3,
        5, 5, 4, 4, 0, 0, 1, 125,
    ];
    static readonly byte[] AcLuminanceValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16,
        0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4,
        0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
        0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];
    static readonly byte[] AcChrominanceCounts =
    [
        0, 2, 1, 2, 4, 4, 3, 4,
        7, 5, 4, 4, 0, 1, 2, 119,
    ];
    static readonly byte[] AcChrominanceValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34,
        0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2,
        0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
        0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    static readonly double[] Cosines = BuildCosines();
    static readonly MiaCommuniCamJpegHuffmanTable DcLuminance =
        new(DcLuminanceCounts, DcValues);
    static readonly MiaCommuniCamJpegHuffmanTable AcLuminance =
        new(AcLuminanceCounts, AcLuminanceValues);
    static readonly MiaCommuniCamJpegHuffmanTable DcChrominance =
        new(DcChrominanceCounts, DcValues);
    static readonly MiaCommuniCamJpegHuffmanTable AcChrominance =
        new(AcChrominanceCounts, AcChrominanceValues);

    public static byte[] Encode(MiaCameraFrame frame, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!MiaCommuniCamImageProfile.IsSupportedNativeSize(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The MCA-25 does not support the requested native size.");
        }

        var output = new List<byte>(checked(width * height / 2));
        WriteHeaders(output, width, height);
        var bits = new MiaCommuniCamJpegBitWriter(output);
        EncodeRaster(frame, width, height, bits);
        bits.Flush();
        output.Add(0xff);
        output.Add(0xd9);
        return output.ToArray();
    }

    static void WriteHeaders(List<byte> output, int width, int height)
    {
        output.Add(0xff);
        output.Add(0xd8);
        WriteSegment(
            output,
            0xe0,
            [
                (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0,
                1, 1, 0, 0, 1, 0, 1, 0, 0,
            ]);
        WriteQuantizationTable(output, 0, LuminanceQuantization);
        WriteQuantizationTable(output, 1, ChrominanceQuantization);
        WriteStartOfFrame(output, width, height);
        WriteHuffmanTable(output, 0x00, DcLuminanceCounts, DcValues);
        WriteHuffmanTable(output, 0x10, AcLuminanceCounts, AcLuminanceValues);
        WriteHuffmanTable(output, 0x01, DcChrominanceCounts, DcValues);
        WriteHuffmanTable(
            output,
            0x11,
            AcChrominanceCounts,
            AcChrominanceValues);
        WriteSegment(
            output,
            0xda,
            [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
    }

    static void WriteQuantizationTable(
        List<byte> output,
        byte identifier,
        ReadOnlySpan<byte> table)
    {
        var payload = new byte[65];
        payload[0] = identifier;
        for (var index = 0; index < ZigZag.Length; index++)
        {
            payload[index + 1] = table[ZigZag[index]];
        }
        WriteSegment(output, 0xdb, payload);
    }

    static void WriteStartOfFrame(List<byte> output, int width, int height)
    {
        var payload = new byte[15];
        payload[0] = 8;
        payload[1] = checked((byte)(height >> 8));
        payload[2] = (byte)(height & byte.MaxValue);
        payload[3] = checked((byte)(width >> 8));
        payload[4] = (byte)(width & byte.MaxValue);
        payload[5] = 3;
        payload[6] = 1;
        payload[7] = 0x11;
        payload[8] = 0;
        payload[9] = 2;
        payload[10] = 0x11;
        payload[11] = 1;
        payload[12] = 3;
        payload[13] = 0x11;
        payload[14] = 1;
        WriteSegment(output, 0xc0, payload);
    }

    static void WriteHuffmanTable(
        List<byte> output,
        byte tableClassAndIdentifier,
        ReadOnlySpan<byte> counts,
        ReadOnlySpan<byte> values)
    {
        var payload = new byte[1 + counts.Length + values.Length];
        payload[0] = tableClassAndIdentifier;
        counts.CopyTo(payload.AsSpan(1));
        values.CopyTo(payload.AsSpan(1 + counts.Length));
        WriteSegment(output, 0xc4, payload);
    }

    static void WriteSegment(
        List<byte> output,
        byte marker,
        ReadOnlySpan<byte> payload)
    {
        int length = checked(payload.Length + 2);
        output.Add(0xff);
        output.Add(marker);
        output.Add(checked((byte)(length >> 8)));
        output.Add(checked((byte)length));
        foreach (byte value in payload)
        {
            output.Add(value);
        }
    }

    static void EncodeRaster(
        MiaCameraFrame frame,
        int width,
        int height,
        MiaCommuniCamJpegBitWriter bits)
    {
        var luminance = new double[64];
        var blueDifference = new double[64];
        var redDifference = new double[64];
        var coefficients = new int[64];
        int previousLuminance = 0;
        int previousBlueDifference = 0;
        int previousRedDifference = 0;
        int cropWidth = Math.Min(frame.Width, frame.Height * 4 / 3);
        int cropHeight = Math.Min(frame.Height, frame.Width * 3 / 4);
        int cropX = (frame.Width - cropWidth) / 2;
        int cropY = (frame.Height - cropHeight) / 2;

        for (var blockY = 0; blockY < height; blockY += 8)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                FillBlocks(new MiaCommuniCamJpegSamplingBlock(
                    frame,
                    width,
                    height,
                    blockX,
                    blockY,
                    cropX,
                    cropY,
                    cropWidth,
                    cropHeight,
                    luminance,
                    blueDifference,
                    redDifference));
                previousLuminance = EncodeBlock(
                    new MiaCommuniCamJpegEncodingBlock(
                    luminance,
                    LuminanceQuantization,
                    coefficients,
                    DcLuminance,
                    AcLuminance,
                    bits),
                    previousLuminance);
                previousBlueDifference = EncodeBlock(
                    new MiaCommuniCamJpegEncodingBlock(
                    blueDifference,
                    ChrominanceQuantization,
                    coefficients,
                    DcChrominance,
                    AcChrominance,
                    bits),
                    previousBlueDifference);
                previousRedDifference = EncodeBlock(
                    new MiaCommuniCamJpegEncodingBlock(
                    redDifference,
                    ChrominanceQuantization,
                    coefficients,
                    DcChrominance,
                    AcChrominance,
                    bits),
                    previousRedDifference);
            }
        }
    }

    static void FillBlocks(MiaCommuniCamJpegSamplingBlock block)
    {
        for (int y = 0; y < 8; y++)
        {
            FillRow(block, y);
        }
    }

    static void FillRow(MiaCommuniCamJpegSamplingBlock block, int y)
    {
        int targetY = Math.Min(block.BlockY + y, block.Height - 1);
        int sourceY = block.CropY +
            targetY * block.CropHeight / block.Height;
        for (int x = 0; x < 8; x++)
        {
            int targetX = Math.Min(block.BlockX + x, block.Width - 1);
            int sourceX = block.CropX +
                targetX * block.CropWidth / block.Width;
            (int red, int green, int blue) = ReadRgb(
                block.Frame,
                block.Frame.Pixels.Span,
                sourceX,
                sourceY);
            int index = y * 8 + x;
            block.Luminance[index] =
                0.299 * red + 0.587 * green + 0.114 * blue - 128;
            block.BlueDifference[index] =
                -0.168736 * red - 0.331264 * green + 0.5 * blue;
            block.RedDifference[index] =
                0.5 * red - 0.418688 * green - 0.081312 * blue;
        }
    }

    static (int Red, int Green, int Blue) ReadRgb(
        MiaCameraFrame frame,
        ReadOnlySpan<byte> source,
        int x,
        int y)
    {
        int offset = y * frame.Stride;
        if (frame.PixelFormat == MiaCameraPixelFormat.Rgb332)
        {
            byte value = source[offset + x];
            return (
                (value >> 5) * 255 / 7,
                (value >> 2 & 7) * 255 / 7,
                (value & 3) * 255 / 3);
        }

        offset += x * 4;
        return (source[offset + 2], source[offset + 1], source[offset]);
    }

    static int EncodeBlock(
        MiaCommuniCamJpegEncodingBlock block,
        int previousDc)
    {
        TransformAndQuantize(
            block.Samples,
            block.Quantization,
            block.Coefficients);
        int currentDc = block.Coefficients[0];
        WriteValue(currentDc - previousDc, block.DcTable, block.Bits);

        int zeroRun = 0;
        for (var index = 1; index < ZigZag.Length; index++)
        {
            zeroRun = EncodeAcCoefficient(
                block,
                block.Coefficients[ZigZag[index]],
                zeroRun);
        }
        if (zeroRun != 0)
        {
            block.AcTable.Write(0, block.Bits);
        }
        return currentDc;
    }

    static int EncodeAcCoefficient(
        MiaCommuniCamJpegEncodingBlock block,
        int value,
        int zeroRun)
    {
        if (value == 0)
        {
            return zeroRun + 1;
        }
        while (zeroRun >= 16)
        {
            block.AcTable.Write(0xf0, block.Bits);
            zeroRun -= 16;
        }
        int category = GetCategory(value);
        block.AcTable.Write((byte)(zeroRun << 4 | category), block.Bits);
        block.Bits.Write(GetValueBits(value, category), category);
        return 0;
    }

    static void WriteValue(
        int value,
        MiaCommuniCamJpegHuffmanTable table,
        MiaCommuniCamJpegBitWriter bits)
    {
        int category = GetCategory(value);
        table.Write((byte)category, bits);
        if (category != 0)
        {
            bits.Write(GetValueBits(value, category), category);
        }
    }

    static int GetCategory(int value)
    {
        int magnitude = Math.Abs(value);
        var category = 0;
        while (magnitude != 0)
        {
            category++;
            magnitude >>= 1;
        }
        return category;
    }

    static uint GetValueBits(int value, int category) =>
        checked((uint)(value >= 0 ? value : value + (1 << category) - 1));

    static void TransformAndQuantize(
        double[] samples,
        ReadOnlySpan<byte> quantization,
        int[] coefficients)
    {
        for (var verticalFrequency = 0; verticalFrequency < 8;
            verticalFrequency++)
        {
            double verticalScale = verticalFrequency == 0
                ? 1 / Math.Sqrt(2)
                : 1;
            for (var horizontalFrequency = 0; horizontalFrequency < 8;
                horizontalFrequency++)
            {
                double sum = 0;
                for (var y = 0; y < 8; y++)
                {
                    double vertical = Cosines[y * 8 + verticalFrequency];
                    for (var x = 0; x < 8; x++)
                    {
                        sum += samples[y * 8 + x] *
                            Cosines[x * 8 + horizontalFrequency] * vertical;
                    }
                }
                double horizontalScale = horizontalFrequency == 0
                    ? 1 / Math.Sqrt(2)
                    : 1;
                int index = verticalFrequency * 8 + horizontalFrequency;
                coefficients[index] = checked((int)Math.Round(
                    0.25 * horizontalScale * verticalScale * sum /
                    quantization[index],
                    MidpointRounding.AwayFromZero));
            }
        }
    }

    static double[] BuildCosines()
    {
        var values = new double[64];
        for (var sample = 0; sample < 8; sample++)
        {
            for (var frequency = 0; frequency < 8; frequency++)
            {
                values[sample * 8 + frequency] = Math.Cos(
                    (2 * sample + 1) * frequency * Math.PI / 16);
            }
        }
        return values;
    }

}
