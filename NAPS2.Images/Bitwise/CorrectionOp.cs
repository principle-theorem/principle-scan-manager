namespace NAPS2.Images.Bitwise;

// TODO: experimental
public class CorrectionOp : UnaryBitwiseImageOp
{
    private readonly CorrectionMode _mode;
    private readonly bool _valid;
    private readonly int _whitePoint;
    private readonly int _blackPoint;

    public CorrectionOp(CorrectionPreProcessingOp preProcessingOp)
    {
        _mode = preProcessingOp.Mode;
        var total = preProcessingOp.TotalCount;
        var counts = preProcessingOp.Counts;
        var segments = new int[64];
        for (int i = 0; i < 256; i++)
        {
            segments[i / 4] += counts[i];
        }
        Console.WriteLine(string.Join(" | ", counts));
        Console.WriteLine(string.Join(" | ", segments));
        int bs = 0;
        while (bs < 62 && (segments[bs] < segments[bs + 1] || segments[bs] < total / 2000))
            bs++;
        int ws = 63;
        while (ws > 1 && (segments[ws] < segments[ws - 1] || segments[ws] < total / 64))
            ws--;
        if (bs > 38 || ws < 24 || bs >= ws)
        {
            _valid = false;
            return;
        }
        _valid = true;
        // TODO: Find a better way to get the white and black points
        // var whiteMode = ws * 4 + 2;
        // var blackMode = bs * 4 + 2;
        _whitePoint = 236;
        _blackPoint = 72;
    }

    protected override void PerformCore(BitwiseImageData data, int partStart, int partEnd)
    {
        if (data.bytesPerPixel is 1 or 3 or 4)
        {
            PerformRgba(data, partStart, partEnd);
        }
        else
        {
            throw new InvalidOperationException("Unsupported pixel format");
        }
    }

    private unsafe void PerformRgba(BitwiseImageData data, int partStart, int partEnd)
    {
        if (!_valid)
            return;
        bool flatten = _mode == CorrectionMode.Document;
        bool retainColor = _mode == CorrectionMode.Photo;
        for (int i = partStart; i < partEnd; i++)
        {
            var row = data.ptr + data.stride * i;
            for (int j = 0; j < data.w; j++)
            {
                var pixel = row + j * data.bytesPerPixel;
                int r = *(pixel + data.rOff);
                int g = *(pixel + data.gOff);
                int b = *(pixel + data.bOff);

                if (flatten)
                {
                    var white = _whitePoint;
                    var black = _blackPoint;

                    if (r < black)
                        r = black;
                    if (g < black)
                        g = black;
                    if (b < black)
                        b = black;

                    if (r > white)
                        r = white;
                    if (g > white)
                        g = white;
                    if (b > white)
                        b = white;

                    r = (r - black) * 255 / (white - black);
                    g = (g - black) * 255 / (white - black);
                    b = (b - black) * 255 / (white - black);
                }

                if (retainColor)
                {
                    var min = Math.Min(r, Math.Min(g, b));
                    var max = Math.Max(r, Math.Max(g, b));
                    var black = Math.Min(min, _blackPoint);
                    var white = Math.Max(max, _whitePoint);

                    r = (r - black) * 255 / (white - black);
                    g = (g - black) * 255 / (white - black);
                    b = (b - black) * 255 / (white - black);
                }

                *(pixel + data.rOff) = (byte) r;
                *(pixel + data.gOff) = (byte) g;
                *(pixel + data.bOff) = (byte) b;
            }
        }
    }
}