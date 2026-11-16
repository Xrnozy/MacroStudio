using OpenCvSharp;

namespace MacroStudio.Vision;

/// <summary>
/// Part voting: cut the target into patches. A few well-spread "seed" patches are searched over the frame and vote
/// for where the whole target must be; every candidate position is then checked patch by patch. A cover over part of
/// the target only removes those patches, the rest still agree on one position. Training-free, exact on position,
/// and works for flat UI that has too few keypoints for feature matching.
/// </summary>
internal static class PatchVoting
{
    const double PatchFloor = 0.85;   // a patch counts as found only if it matches this well
    const int MinPatches = 4;         // fewer agreeing patches is not evidence
    const int Seeds = 8;
    const double FlatSd = 8;          // a cell flatter than this is "flat": it proves nothing by matching, only by colour

    sealed record Patch(Rect R, Scalar Mean, double Sd, int I, int J);
    readonly record struct Hit(int X, int Y, double Score, Rect R, double Detail, int I, int J, double Part = 1)
    {
        /// <summary>A textured cell: it was searched for and matched. A flat cell was only checked by colour.</summary>
        public bool Informed => Detail >= FlatSd;
    }

    sealed record Vote(int Seed, int X, int Y, double Score);

    /// <summary>Patch layout of one template at one scale: built once, reused every frame.</summary>
    sealed class Info
    {
        public int Ps; public double D;
        public List<Patch> Patches = [];   // every cell of the grid, most detailed first (hopeless candidates die early)
        public int Informed;               // how many cells are textured enough to be matched
        public double TotalDetail;         // sum of the textured cells' detail (std. deviation)
        public int Nx, Ny;                 // the patch grid
        public List<Patch> Seeds = [];
        public List<Rect> SeedCell = [];   // each seed's own cell before it was grown for the half-size search
        public List<Mat> SeedImg = [];     // seed patches at search resolution
    }

    static Info InfoOf(Scaled sc)
    {
        lock (sc)
        {
            if (sc.PatchData is Info cached) return cached;
            // small patches, evenly spread (overlapping when needed): a cover then spoils only the patches it actually touches
            var info = new Info { Ps = Math.Clamp(Math.Min(sc.W, sc.H) / 4, 8, 20) };
            // seeds search at half size (a 4x faster full-frame pass) when the patches are big enough to survive it —
            // and grown to 16 px below. A small patch halved is a smear that votes weakly, so a small template's seeds
            // search at full size instead: their vote is sharper and impostors score lower there.
            info.D = info.Ps >= 12 ? 0.5 : 1;
            int nx = Math.Max(1, (int)Math.Round(sc.W / (double)info.Ps)), ny = Math.Max(1, (int)Math.Round(sc.H / (double)info.Ps));
            info.Nx = nx; info.Ny = ny;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int x = nx == 1 ? 0 : (int)Math.Round(i * (sc.W - info.Ps) / (double)(nx - 1));
                    int y = ny == 1 ? 0 : (int)Math.Round(j * (sc.H - info.Ps) / (double)(ny - 1));
                    var r = new Rect(x, y, info.Ps, info.Ps);
                    using var pg = new Mat(sc.Gray, r);
                    Cv2.MeanStdDev(pg, out _, out var sd);
                    using var pc = new Mat(sc.Bgr, r);
                    info.Patches.Add(new Patch(r, Cv2.Mean(pc), sd.Val0, i, j));
                    if (sd.Val0 >= FlatSd) { info.Informed++; info.TotalDetail += sd.Val0; }   // flat cells stay: their colour still proves presence
                }
            info.Patches.Sort((a, b) => b.Sd.CompareTo(a.Sd));
            PickSeeds(sc, info);
            sc.PatchData = info;
            return info;
        }
    }

    /// <summary>
    /// The most detailed patch of every region of the grid first, then more by detail. Voting used to start from the
    /// strongest patches anywhere, which all sat in the same busy region: one cover over it silenced every voter.
    /// Bins tile the whole target, so a contiguous cover always leaves some voters standing. Flat patches never vote:
    /// they match everywhere and say nothing.
    /// </summary>
    static void PickSeeds(Scaled sc, Info info)
    {
        var informed = info.Patches.Where(p => p.Sd >= FlatSd);              // already most-detailed first
        // ~Seeds bins in the grid's aspect ratio: the best-textured patch of each region
        int by = Math.Clamp((int)Math.Round(Math.Sqrt(Seeds * info.Ny / (double)Math.Max(1, info.Nx))), 1, Math.Min(info.Ny, Seeds));
        if (by == 1 && info.Ny >= 2) by = 2;                                 // never a single band when there are rows to cover
        int bx = Math.Clamp(Seeds / by, 1, info.Nx);
        foreach (var p in informed.GroupBy(p => Math.Min(bx - 1, p.I * bx / info.Nx) + Math.Min(by - 1, p.J * by / info.Ny) * bx))
            info.Seeds.Add(p.First());                                       // first of its bin = the most detailed there
        // top up with the next most detailed patches that are not right on top of a seed already chosen
        foreach (var p in informed)
            if (info.Seeds.Count < Seeds && info.Seeds.All(s => Math.Abs(s.R.X - p.R.X) >= info.Ps * 1.5 || Math.Abs(s.R.Y - p.R.Y) >= info.Ps * 1.5)) info.Seeds.Add(p);
        foreach (var p in informed) if (info.Seeds.Count < Seeds && !info.Seeds.Contains(p)) info.Seeds.Add(p);
        // grow small seeds so they survive the half-size search (never past the template edge), and drop grown twins:
        // two nearly identical seed images would vote twice for the same part of the target
        const int SeedPx = 16;
        var grown = new List<Patch>();
        var cells = new List<Rect>();
        foreach (var s in info.Seeds)
        {
            var g = info.D == 1 || info.Ps >= SeedPx || sc.W < SeedPx || sc.H < SeedPx ? s
                : s with { R = new Rect(Math.Clamp(s.R.X - (SeedPx - info.Ps) / 2, 0, sc.W - SeedPx), Math.Clamp(s.R.Y - (SeedPx - info.Ps) / 2, 0, sc.H - SeedPx), SeedPx, SeedPx) };
            if (!grown.Any(o => Math.Abs(o.R.X - g.R.X) < g.R.Width / 2 && Math.Abs(o.R.Y - g.R.Y) < g.R.Height / 2)) { grown.Add(g); cells.Add(s.R); }
        }
        info.Seeds = grown; info.SeedCell = cells;
        foreach (var s in info.Seeds)
        {
            using var pg = new Mat(sc.Gray, s.R);
            var small = new Mat();
            Cv2.Resize(pg, small, new Size(Math.Max(1, (int)(pg.Width * info.D)), Math.Max(1, (int)(pg.Height * info.D))), 0, 0, InterpolationFlags.Area);
            info.SeedImg.Add(small);
        }
    }

    public static List<Detection> Find(FrameContext ctx, Mat tpl, double scale, double minVisible, Rect? hint)
    {
        List<Detection> last = [];
        foreach (var s in FindSteps(ctx, tpl, scale, minVisible, hint)) if (s != null) last = s;
        return last;
    }

    /// <summary>Same as <see cref="Find"/> in slices: one seed's search per step (null = more to do), the verified candidates at the end.</summary>
    public static IEnumerable<List<Detection>?> FindSteps(FrameContext ctx, Mat tpl, double scale, double minVisible, Rect? hint)
    {
        var sc = Prepared.Of(tpl).At(scale);
        int tw = sc.W, th = sc.H;
        var bounds = new Rect(0, 0, ctx.Width, ctx.Height);
        var area = hint is { } h ? h.Intersect(bounds) : bounds;
        if (tw < 16 || th < 16 || area.Width < tw || area.Height < th) { yield return []; yield break; }
        var info = InfoOf(sc);
        if (info.Informed < MinPatches) { yield return []; yield break; }

        // seeds vote on the shared gray (half resolution when the patch is big enough to survive it)
        double d = info.D;
        Mat gd; int ox, oy;
        if (d == 1) { gd = new Mat(ctx.Gray, area); ox = area.X; oy = area.Y; }
        else
        {
            int ax = area.X / 2, ay = area.Y / 2;
            gd = new Mat(ctx.Half, new Rect(ax, ay, Math.Min(area.Width / 2, ctx.Half.Width - ax), Math.Min(area.Height / 2, ctx.Half.Height - ay)));
            ox = ax * 2; oy = ay * 2;
        }
        int slack = d == 1 ? 3 : 5;
        var votes = new List<Vote>();
        var guess = new (int X, int Y, double Score)[info.Seeds.Count];   // each seed's own best guess at full size
        using (gd)
            for (int i = 0; i < info.Seeds.Count; i++)
            {
                var pd = info.SeedImg[i];
                if (gd.Width < pd.Width || gd.Height < pd.Height) continue;
                using var map = Detector.ScoreMap(gd, pd);
                var sr = info.Seeds[i].R;
                var cell = info.SeedCell[i];
                for (int k = 0; k < 4; k++)
                {
                    Cv2.MinMaxLoc(map, out _, out double mx, out _, out Point loc);
                    if (k == 0) guess[i] = (ox + (int)Math.Round(loc.X / d) - sr.X, oy + (int)Math.Round(loc.Y / d) - sr.Y, mx);
                    if (mx < 0.4) break;
                    var kill = new Rect(Math.Max(0, loc.X - pd.Width / 2), Math.Max(0, loc.Y - pd.Height / 2), pd.Width, pd.Height).Intersect(new Rect(0, 0, map.Width, map.Height));
                    using (var m = new Mat(map, kill)) m.SetTo(Scalar.All(-2));
                    // re-check the guess at full size in a small window: a half-size impostor fades, the real patch stays.
                    // The seed's own cell is used (not its grown search image), and if the cell straddles a cover its
                    // untouched part still pins the position — covered parts are ignored, they do not silence the vote.
                    int tx = ox + (int)Math.Round(loc.X / d) - sr.X, ty = oy + (int)Math.Round(loc.Y / d) - sr.Y;
                    MatchPart(ctx.Gray, sc.Gray, cell, tx + cell.X, ty + cell.Y, 4, bounds, out int px, out int py, out _, out double fmx);
                    int vx = px - cell.X, vy = py - cell.Y;
                    if (k == 0) guess[i] = (vx, vy, fmx);
                    if (fmx >= PatchFloor - 0.05) votes.Add(new Vote(i, vx, vy, fmx));
                }
                yield return null;
            }

        // candidate positions: where at least two different seeds agree, each verified against ALL patches
        var cands = new List<(int X, int Y, List<Hit> Hits)>();
        var tried = new List<(int X, int Y)>();
        bool Check(int cx, int cy)
        {
            if (tried.Any(t => Math.Abs(t.X - cx) <= slack && Math.Abs(t.Y - cy) <= slack)) return false;
            tried.Add((cx, cy));
            var hits = Verify(ctx, info, sc, cx, cy, 4, Prepared.Of(tpl).ColorTolerance * 1.0 + 5, minVisible, out double same);
            var proven = Rigid(hits.Where(h => h.Informed).ToList());
            double visible = Share(hits, info);
            if (proven.Sum(h => h.Part) >= MinPatches && Textured(proven, info, minVisible) && visible >= minVisible && Spread(proven, sc) && !LookAlike(proven.Sum(h => h.Part) / info.Informed, same)) { cands.Add((cx, cy, hits)); return true; }
            return false;
        }
        for (int inst = 0; inst < 3; inst++)
        {
            List<Vote>? best = null;
            foreach (var v in votes)
            {
                var members = votes.Where(w => Math.Abs(w.X - v.X) <= slack && Math.Abs(w.Y - v.Y) <= slack).GroupBy(w => w.Seed).Select(gr => gr.First()).ToList();
                if (best == null || members.Count > best.Count) best = members;
            }
            if (best == null || best.Count < Math.Min(2, info.Seeds.Count)) break;
            int cx = Median(best.Select(m => m.X)), cy = Median(best.Select(m => m.Y));
            Check(cx, cy);
            votes.RemoveAll(w => Math.Abs(w.X - cx) < tw / 2 && Math.Abs(w.Y - cy) < th / 2); // look for another instance elsewhere
        }
        if (cands.Count == 0)
        {
            // a heavy cover may have silenced all but one voter: a lone seed that matched well is still worth one
            // verification — the patch check itself decides whether the position is real. Strongest guesses first.
            var lone = new List<(int X, int Y, double Score)>();
            foreach (var t in votes.Select(v => (v.X, v.Y, v.Score)).Concat(guess.Where(g => g.Score >= 0.7).Select(g => (g.X, g.Y, g.Score))).OrderByDescending(t => t.Score))
                if (!lone.Any(l => Math.Abs(l.X - t.X) <= slack && Math.Abs(l.Y - t.Y) <= slack)) lone.Add(t);
            int extra = 0;
            foreach (var (lx, ly, _) in lone)
            {
                if (extra >= 20) break;
                int n = tried.Count; Check(lx, ly); if (tried.Count > n) extra++;   // every position really verified counts, accepted or not
            }
        }

        // A patch that matches at two different candidates (the frame and gradient that buttons of a kind share) tells them apart from nothing:
        // the strongest candidate is accepted first and claims its cells; a weaker one then has to stand on the evidence
        // the winners did not already use. A candidate that is mostly the same place as a winner is the same instance.
        var found = new List<Detection>();
        var claimed = new HashSet<(int I, int J)>();
        foreach (var c in cands.OrderByDescending(c => c.Hits.Where(h => h.Informed).Sum(h => h.Part)).ThenByDescending(c => Share(c.Hits, info)))
        {
            if (found.Any(f => Math.Abs(f.Rect.X - c.X) < tw * 0.75 && Math.Abs(f.Rect.Y - c.Y) < th * 0.75)) continue;
            var own = c.Hits.Where(h => !claimed.Contains((h.I, h.J))).ToList();
            var ownProven = Rigid(own.Where(h => h.Informed).ToList());
            double uv = Share(own, info);
            if (ownProven.Sum(h => h.Part) >= MinPatches && Textured(ownProven, info, minVisible) && uv >= minVisible && Spread(ownProven, sc))
            {
                found.Add(new Detection(new Rect(Median(ownProven.Select(m => m.X)), Median(ownProven.Select(m => m.Y)), tw, th), uv * own.Average(m => m.Score), scale, "patches", uv));
                foreach (var ow in own) claimed.Add((ow.I, ow.J));
            }
        }
        yield return found;
    }

    /// <summary>Cheap re-check of a covered target at its last position (no search): are enough patches still there?</summary>

    public static Detection? Recheck(FrameContext ctx, Mat tpl, double scale, Point topLeft, double minVisible)
    {
        var sc = Prepared.Of(tpl).At(scale);
        var info = InfoOf(sc);
        if (info.Informed < MinPatches) return null;
        var hits = Verify(ctx, info, sc, topLeft.X, topLeft.Y, 6, Prepared.Of(tpl).ColorTolerance * 1.0 + 5, minVisible, out double same);
        var proven = Rigid(hits.Where(h => h.Informed).ToList());
        double visible = Share(hits, info);
        if (proven.Sum(h => h.Part) < MinPatches || !Textured(proven, info, minVisible) || visible < minVisible || !Spread(proven, sc) || LookAlike(proven.Sum(h => h.Part) / info.Informed, same)) return null;
        return new Detection(new Rect(Median(proven.Select(m => m.X)), Median(proven.Select(m => m.Y)), sc.W, sc.H), visible * hits.Average(m => m.Score), scale, "patches", visible);
    }

    /// <summary>Flat cells match any patch of the same colour, so they cannot carry the visible share alone: the detailed cells must cover it too.</summary>
    static bool Textured(List<Hit> proven, Info info, double minVisible) => proven.Sum(h => h.Part) >= Math.Max(minVisible, 0.4) * info.Informed;

    /// <summary>A button moves as one piece: patches that each slid to a different offset (repeated letter shapes in another label) are not evidence of it.</summary>
    static List<Hit> Rigid(List<Hit> hits)
    {
        if (hits.Count == 0) return hits;
        int mx = Median(hits.Select(h => h.X)), my = Median(hits.Select(h => h.Y));
        return hits.Where(h => Math.Abs(h.X - mx) <= 2 && Math.Abs(h.Y - my) <= 2).ToList();
    }

        /// <summary>
    /// Cells that are really there at the candidate position, each with the offset it implies. Textured cells are
    /// searched in a small window; flat cells (which match everywhere and prove nothing) are only asked whether their
    /// own colour is still there — that is what makes the visible fraction honest: a cover over a flat part of the
    /// target now counts, instead of hiding behind the cells that were never tracked.
    /// </summary>
    static List<Hit> Verify(FrameContext ctx, Info info, Scaled sc, int cx, int cy, int look, double colorTol, double minVisible, out double sameMaterialShare)
    {
        double missed = 0, missedSame = 0;
        var hits = new List<Hit>();
        int ps = info.Ps, total = info.Patches.Count;
        var bounds = new Rect(0, 0, ctx.Width, ctx.Height);
        int need = (int)Math.Ceiling(minVisible * total - 1e-9);
        int informedLeft = info.Informed, informedHits = 0;
        for (int idx = 0; idx < total; idx++)
        {
            // the most detailed cells go first: once a candidate can no longer reach the share it needs, stop paying for it
            if (hits.Count + (total - idx) < need || informedHits + informedLeft < MinPatches) break;
            var p = info.Patches[idx];
            if (p.Sd < FlatSd)
            {
                var at = new Rect(cx + p.R.X, cy + p.R.Y, ps, ps);
                if (at.X >= 0 && at.Y >= 0 && at.Right <= bounds.Width && at.Bottom <= bounds.Height)
                {
                    using var there = new Mat(ctx.Bgr, at);
                    var gap = Cv2.Mean(there);
                    double dist = colorTol >= 250 ? 0 : ColorMath.Distance(gap, p.Mean);
                    if (dist <= colorTol)
                        hits.Add(new Hit(cx, cy, colorTol >= 250 ? 0.6 : Math.Clamp(1 - dist / colorTol, 0.5, 1), p.R, p.Sd, p.I, p.J));
                    else if (Chroma(p.Mean) >= MinChroma) missed += p.Sd;   // a flat miss is never "same material": same colour would have matched
                }
                continue;
            }
            informedLeft--;
            var spot = new Rect(cx + p.R.X, cy + p.R.Y, ps, ps);
            double part = MatchPart(ctx.Gray, sc.Gray, p.R, spot.X, spot.Y, look, bounds, out int px, out int py, out Rect used, out double mx);
            if (part <= 0)                                       // covered or changed: this patch does not vote
            {
                if (spot.X >= 0 && spot.Y >= 0 && spot.Right <= bounds.Width && spot.Bottom <= bounds.Height)
                {
                    // Only coloured patches can say what fills a gap: a grey or dark patch looks like any cover. A gap that still shows the target's
                    // own saturated colour (gold between the letters of another label) is not covered by anything: it is the same kind of button.
                    if (Chroma(p.Mean) >= MinChroma)
                    {
                        using var there = new Mat(ctx.Bgr, spot);
                        var gap = Cv2.Mean(there);
                        missed += p.Sd;
                        if (Chroma(gap) >= 0.5 * Chroma(p.Mean) && ColorMath.Similar(gap, p.Mean, colorTol)) missedSame += p.Sd;   // a dark or grey cover has no colour of its own: it is not "the same"
                    }
                }
                continue;
            }
            var seenAt = new Rect(px + used.X, py + used.Y, used.Width, used.Height);
            if (seenAt.X < 0 || seenAt.Y < 0 || seenAt.Right > bounds.Width || seenAt.Bottom > bounds.Height) continue;   // a part found at its expected spot may hang off the frame
            using var seen = new Mat(ctx.Bgr, seenAt);
            var mean = Cv2.Mean(seen);
            // correlation ignores contrast, so also require roughly the same colour: a patch of another hue is a different thing, a lighter or slightly tinted one is not
            if (!ColorMath.Similar(mean, p.Mean, colorTol)) { if (Chroma(p.Mean) >= MinChroma) missed += p.Sd; continue; }
            informedHits++;
            // only part of the cell proved itself: the rest counts toward the missing share, same-material checked on the whole cell
            if (part < 1 && Chroma(p.Mean) >= MinChroma && spot.X >= 0 && spot.Y >= 0 && spot.Right <= bounds.Width && spot.Bottom <= bounds.Height)
            {
                using var there = new Mat(ctx.Bgr, spot);
                var gap = Cv2.Mean(there);
                missed += p.Sd * (1 - part);
                if (Chroma(gap) >= 0.5 * Chroma(p.Mean) && ColorMath.Similar(gap, p.Mean, colorTol)) missedSame += p.Sd * (1 - part);
            }
            hits.Add(new Hit(px - p.R.X, py - p.R.Y, mx, new Rect(p.R.X + used.X, p.R.Y + used.Y, used.Width, used.Height), p.Sd, p.I, p.J, part));
        }
        sameMaterialShare = missed > 0 ? missedSame / missed : 0;
        return hits;
    }

    /// <summary>
    /// How much of one template cell is really at its expected frame position. The whole cell is tried first; when it
    /// only partly survived a cover (a moderate score), its four halves — then, if none showed, its four quarters —
    /// each get their own small search. The answer is the share of the cell's area that proved itself, so a cell the
    /// cover cuts through still counts for exactly the part that is there. <paramref name="px"/>,<paramref name="py"/>
    /// receive the best implied cell position and <paramref name="used"/> the part of the cell that matched.
    /// </summary>
    static double MatchPart(Mat gray, Mat tpl, Rect cell, int ex, int ey, int look, Rect bounds,
        out int px, out int py, out Rect used, out double score)
    {
        px = ex; py = ey; used = new Rect(0, 0, cell.Width, cell.Height);
        score = Scan(gray, tpl, cell, ex, ey, look, bounds, out int bx, out int by);
        if (score >= PatchFloor - 0.05) { px = bx; py = by; return 1; }
        if (score < 0.45) return 0;                          // no resemblance at all: paying for smaller parts finds only noise
        int hw = cell.Width / 2, hh = cell.Height / 2;
        var halves = new (Rect R, int Q)[]
        {
            (new Rect(0, 0, hw, cell.Height), 5),                       // left:  TL|BL
            (new Rect(cell.Width - hw, 0, hw, cell.Height), 10),        // right: TR|BR
            (new Rect(0, 0, cell.Width, hh), 3),                        // top:   TL|TR
            (new Rect(0, cell.Height - hh, cell.Width, hh), 12),        // bottom: BL|BR
        };
        int found = Halves(gray, tpl, cell, ex, ey, look, bounds, halves, ref px, ref py, ref used, ref score);
        if (found == 0 && cell.Width >= 8 && cell.Height >= 8)
        {
            var quarters = new (Rect R, int Q)[]
            {
                (new Rect(0, 0, hw, hh), 1), (new Rect(cell.Width - hw, 0, hw, hh), 2),
                (new Rect(0, cell.Height - hh, hw, hh), 4), (new Rect(cell.Width - hw, cell.Height - hh, hw, hh), 8),
            };
            found = Halves(gray, tpl, cell, ex, ey, look, bounds, quarters, ref px, ref py, ref used, ref score);
        }
        int bits = 0; for (int f = found; f != 0; f &= f - 1) bits++;
        return bits / 4.0;
    }

    /// <summary>Tries each sub-rect of a cell; returns the bitmask of covered quadrants, keeping the strongest match's implied cell position.</summary>
    static int Halves(Mat gray, Mat tpl, Rect cell, int ex, int ey, int look, Rect bounds,
        (Rect R, int Q)[] subs, ref int px, ref int py, ref Rect used, ref double score)
    {
        int found = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        foreach (var (hr, q) in subs)
        {
            var cr = new Rect(cell.X + hr.X, cell.Y + hr.Y, hr.Width, hr.Height);
            double m = Scan(gray, tpl, cr, ex + hr.X, ey + hr.Y, look, bounds, out int hx, out int hy);
            if (m >= PatchFloor - 0.05)
            {
                if (m > score) { score = m; px = hx - hr.X; py = hy - hr.Y; }
                x0 = Math.Min(x0, hr.X); y0 = Math.Min(y0, hr.Y); x1 = Math.Max(x1, hr.Right); y1 = Math.Max(y1, hr.Bottom);
                found |= q;
            }
        }
        if (found != 0) used = new Rect(x0, y0, x1 - x0, y1 - y0);
        return found;
    }

    /// <summary>Best correlation of one template rect in a small window around its expected position; returns the position that achieved it.</summary>
    static double Scan(Mat gray, Mat tpl, Rect cell, int ex, int ey, int look, Rect bounds, out int px, out int py)
    {
        px = ex; py = ey;
        var win = new Rect(ex - look, ey - look, cell.Width + 2 * look, cell.Height + 2 * look).Intersect(bounds);
        if (win.Width < cell.Width || win.Height < cell.Height) return -1;   // that part of the target is off-screen
        using var sub = new Mat(gray, win);
        using var pg = new Mat(tpl, cell);
        using var map = new Mat();
        Cv2.MatchTemplate(sub, pg, map, TemplateMatchModes.CCoeffNormed);   // an informed cell always has enough deviation for correlation
        Cv2.PatchNaNs(map, 0);
        Cv2.MinMaxLoc(map, out _, out double mx, out _, out _);
        var loc = PeakNear(map, mx, new Point(ex - win.X, ey - win.Y));
        px = win.X + loc.X; py = win.Y + loc.Y;
        return mx;
    }

    /// <summary>The share of the target's cells that are really there, flat cells counted by colour, cells a cover cuts through counted for the part that proved itself.</summary>
    static double Share(List<Hit> hits, Info info) => hits.Sum(h => h.Part) / info.Patches.Count;

    /// <summary>
    /// Buttons that share a shape, frame and colours (RETRY / MODIFIERS) agree on the frame and the flat gradient and differ in the label. What is
    /// "missing" then is not hidden by anything: it is plainly there, in the same colours, just not the same drawing. A real cover shows something
    /// else (a dark popup, white, another picture). So when most of what is missing still has the target's colours, and a good part is missing, it
    /// is another button. <paramref name="visible"/> is the share of the *textured* cells — the share the label difference actually moves.
    /// </summary>
    const double SameMaterial = 0.7;
    const double MinChroma = 40;   // spread between the strongest and weakest colour channel for a patch to count as coloured

    static double Chroma(Scalar m) => Math.Max(m.Val0, Math.Max(m.Val1, m.Val2)) - Math.Min(m.Val0, Math.Min(m.Val1, m.Val2));

    static bool LookAlike(double visible, double sameMaterialShare) => visible < 0.8 && sameMaterialShare >= SameMaterial;

    /// <summary>The surviving patches must cover a real stretch of the target in both directions: a strip of one edge slides freely along that edge and proves no position.</summary>
    static bool Spread(List<Hit> hits, Scaled sc)
    {
        int x0 = hits.Min(h => h.R.X), x1 = hits.Max(h => h.R.Right), y0 = hits.Min(h => h.R.Y), y1 = hits.Max(h => h.R.Bottom);
        return (x1 - x0) >= 0.3 * sc.W && (y1 - y0) >= 0.3 * sc.H;
    }

    /// <summary>
    /// The position among the (near-)best ones closest to <paramref name="expected"/>, for a map only a few pixels
    /// across: scanned directly instead of paying for a threshold pass plus allocations on every cell of every check.
    /// </summary>
    static Point PeakNear(Mat map, double max, Point expected, double tol = 0.01)
    {
        map.GetArray<float>(out var v);
        int w = map.Width;
        Point best = expected; double bd = double.MaxValue;
        for (int i = 0; i < v.Length; i++)
            if (v[i] >= max - tol)
            {
                double d = (double)(i % w - expected.X) * (i % w - expected.X) + (double)(i / w - expected.Y) * (i / w - expected.Y);
                if (d < bd) { bd = d; best = new Point(i % w, i / w); }
            }
        return bd == double.MaxValue ? expected : best;
    }

    static int Median(IEnumerable<int> v) { var a = v.OrderBy(i => i).ToArray(); return a[a.Length / 2]; }
}
