// Ported from DSHBox pilot/interlock-relay-core (GPL-3.0, v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4):
//   src/main/java/interlock/relay/core/exec/FrameEncoder.kt (convert)
// Registered in docs/UPSTREAMS.md. The pure pixel step of the background recorder: ARGB → NV12 with nearest-neighbour
// downscale, BT.601 fixed point, limited range (luma 16..235, chroma 16..240). MediaCodec / MediaMuxer stay in
// DshMobile.Android behind IVideoEncoder.

namespace DshMobile.Runtime.Automation.Relay.Exec.Shizuku;

public static class Nv12
{
    /// <summary>Bytes of one NV12 frame with the encoder's own padding.</summary>
    public static int FrameBytes(int stride, int sliceHeight) => stride * sliceHeight * 3 / 2;

    /// <summary>
    /// Fills <paramref name="plane"/> (at least <see cref="FrameBytes"/>) from <paramref name="argb"/>
    /// (<c>Bitmap.getPixels</c> layout, row length = <paramref name="sourceWidth"/>).
    /// </summary>
    public static void Convert(ReadOnlySpan<int> argb, int sourceWidth, int sourceHeight, int width, int height, int stride, int sliceHeight,
        Span<byte> plane)
    {
        var rowSize = sourceWidth;
        var stepX = Math.Max(1, sourceWidth / width);
        var stepY = Math.Max(1, sourceHeight / height);
        for (var row = 0; row < height; row++)
        {
            var srcRow = Math.Min(sourceHeight - 1, row * stepY);
            var at = row * stride;
            for (var col = 0; col < width; col++)
            {
                var color = argb[srcRow * rowSize + Math.Min(rowSize - 1, col * stepX)];
                var r = (color >> 16) & 0xFF;
                var g = (color >> 8) & 0xFF;
                var b = color & 0xFF;
                plane[at + col] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 16, 235);
            }
        }
        var chromaBase = stride * sliceHeight;
        for (var chromaRow = 0; chromaRow < height / 2; chromaRow++)
        {
            var srcRow = Math.Min(sourceHeight - 1, chromaRow * 2 * stepY);
            var at = chromaBase + chromaRow * stride;
            for (var col = 0; col < width / 2; col++)
            {
                var color = argb[srcRow * rowSize + Math.Min(rowSize - 1, col * 2 * stepX)];
                var r = (color >> 16) & 0xFF;
                var g = (color >> 8) & 0xFF;
                var b = color & 0xFF;
                plane[at + col * 2] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 16, 240);
                plane[at + col * 2 + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 16, 240);
            }
        }
    }
}
