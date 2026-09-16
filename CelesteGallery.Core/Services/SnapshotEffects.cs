using System;

namespace CelesteGallery.Services;

/// <summary>
/// 截图之后的各种像素级处理：裁剪、马赛克、模糊。
///
/// 全部是纯数组操作、不碰 UI，所以能在命令行探针里直接压测
/// （见 tools/IndexHarness 那套做法），不用开界面肉眼 randomness。
/// </summary>
public static class SnapshotEffects
{
    /// <summary>
    /// 从整帧里裁一块出来。 Rect 越界会被夹进画面范围内，不会抛。
    /// </summary>
    public static CapturedFrame? Crop(CapturedFrame src, CaptureRect rect)
    {
        int x = Math.Clamp(rect.X, 0, src.Width);
        int y = Math.Clamp(rect.Y, 0, src.Height);
        int x2 = Math.Clamp(rect.X + rect.Width, 0, src.Width);
        int y2 = Math.Clamp(rect.Y + rect.Height, 0, src.Height);

        int w = x2 - x, h = y2 - y;
        if (w <= 0 || h <= 0) return null;

        int stride = w * 4;
        var dst = new byte[stride * h];
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(src.Pixels, (y + row) * src.Stride + x * 4, dst, row * stride, stride);
        }
        return new CapturedFrame(dst, w, h, stride, src.Scale);
    }

    /// <summary>
    /// 马赛克（方块化的那种打码）。把区域划成 <paramref name="block"/> 像素的格子，
    /// 每格统一涂成该格左上角那个点的颜色。
    /// </summary>
    public static void Mosaic(CapturedFrame frame, CaptureRect rect, int block = 10)
    {
        if (block < 2) block = 2;
        var clamped = Clamp(frame, rect);
        if (clamped.IsEmpty) return;

        for (int gy = clamped.Y; gy < clamped.Bottom; gy += block)
        {
            for (int gx = clamped.X; gx < clamped.Right; gx += block)
            {
                int bx = Math.Min(gx, clamped.Right - 1);
                int by = Math.Min(gy, clamped.Bottom - 1);
                int i = by * frame.Stride + bx * 4;
                byte b = frame.Pixels[i], g = frame.Pixels[i + 1], r = frame.Pixels[i + 2], a = frame.Pixels[i + 3];

                int yEnd = Math.Min(gy + block, clamped.Bottom);
                int xEnd = Math.Min(gx + block, clamped.Right);
                for (int yy = gy; yy < yEnd; yy++)
                {
                    int o = yy * frame.Stride;
                    for (int xx = gx; xx < xEnd; xx++)
                    {
                        int p = o + xx * 4;
                        frame.Pixels[p] = b;
                        frame.Pixels[p + 1] = g;
                        frame.Pixels[p + 2] = r;
                        frame.Pixels[p + 3] = a;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 方框模糊。够用就好——截图里的模糊基本都是用来盖住敏感信息，
    /// 不需要真正的高斯核，跑得快更重要。
    /// </summary>
    public static void BoxBlur(CapturedFrame frame, CaptureRect rect, int radius = 6, int passes = 2)
    {
        var clamped = Clamp(frame, rect);
        if (clamped.IsEmpty) return;
        if (radius < 1) radius = 1;
        passes = Math.Clamp(passes, 1, 4);

        // 先拷一份出来当"源"，不然边模糊边被自己影响，会拖出条状的脏边
        var backup = new byte[frame.Pixels.Length];
        Buffer.BlockCopy(frame.Pixels, 0, backup, 0, frame.Pixels.Length);

        for (int pass = 0; pass < passes; pass++)
        {
            if (pass > 0) Buffer.BlockCopy(frame.Pixels, 0, backup, 0, frame.Pixels.Length);

            for (int y = clamped.Y; y < clamped.Bottom; y++)
            {
                for (int x = clamped.X; x < clamped.Right; x++)
                {
                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    int count = 0;

                    int y0 = Math.Max(clamped.Y, y - radius);
                    int y1 = Math.Min(clamped.Bottom - 1, y + radius);
                    int x0 = Math.Max(clamped.X, x - radius);
                    int x1 = Math.Min(clamped.Right - 1, x + radius);

                    for (int yy = y0; yy <= y1; yy++)
                    {
                        int rowStart = yy * frame.Stride;
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            int p = rowStart + xx * 4;
                            sb += backup[p]; sg += backup[p + 1]; sr += backup[p + 2]; sa += backup[p + 3];
                            count++;
                        }
                    }

                    int dst = y * frame.Stride + x * 4;
                    frame.Pixels[dst] = (byte)(sb / count);
                    frame.Pixels[dst + 1] = (byte)(sg / count);
                    frame.Pixels[dst + 2] = (byte)(sr / count);
                    frame.Pixels[dst + 3] = (byte)(sa / count);
                }
            }
        }
    }

    private static CaptureRect Clamp(CapturedFrame frame, CaptureRect rect)
    {
        int x = Math.Clamp(rect.X, 0, frame.Width - 1);
        int y = Math.Clamp(rect.Y, 0, frame.Height - 1);
        int right = Math.Clamp(rect.X + rect.Width, 0, frame.Width);
        int bottom = Math.Clamp(rect.Y + rect.Height, 0, frame.Height);
        return new CaptureRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
