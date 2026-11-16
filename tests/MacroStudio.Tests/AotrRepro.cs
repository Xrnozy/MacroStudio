using MacroStudio.Vision;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
namespace MacroStudio.Tests;
public class AotrRepro(ITestOutputHelper o)
{
    static string P(string n) => Path.Combine(AppContext.BaseDirectory, "Assets", n);
    [Theory]
    [InlineData(593, 476, 114, 27, 2.59)]
    [InlineData(354, 484, 126, 28, 2.59)]
    [InlineData(815, 475, 114, 27, 2.59)]
    public void Select_button_is_not_found_on_mission_failed(int x, int y, int w, int h, double up)
    {
        using var sel = Cv2.ImRead(P("aotr_select.png"));
        using var tpl = new Mat(sel, new Rect(x, y, w, h)).Clone();
        using var small = Cv2.ImRead(P("aotr_failed.png"));
        using var frame = new Mat(); Cv2.Resize(small, frame, new Size(), up, up, InterpolationFlags.Cubic);
        var r = new Detector().Search(frame, tpl, 0.85, 1, null, out var best);
        foreach (var d in r) o.WriteLine($"{d.Method} {d.Score:F2} vis={d.Visible:F2} {d.Rect}");
        o.WriteLine($"best={best:F2} hits={r.Count}");
        Assert.Empty(r);
    }
}
