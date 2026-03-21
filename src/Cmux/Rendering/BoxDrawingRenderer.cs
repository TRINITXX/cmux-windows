using System;

namespace Cmux.Rendering;

/// <summary>
/// Generates pixel-perfect BGRA bitmaps for box-drawing (U+2500–U+257F)
/// and block element (U+2580–U+259F) characters. Lines extend exactly to
/// cell edges, eliminating inter-cell gaps that font-based rendering causes.
/// </summary>
internal static class BoxDrawingRenderer
{
    // ── Line weight constants ────────────────────────────────────────
    private const byte N = 0; // None
    private const byte L = 1; // Light
    private const byte H = 2; // Heavy
    private const byte D = 3; // Double
    private const byte X = 0xFF; // Sentinel: fall through to DirectWrite

    // ── Segment table (U+2500–U+257F) ───────────────────────────────
    // 4 bytes per entry: left, right, up, down
    // Indexed by (codepoint - 0x2500) * 4
    private static readonly byte[] SegmentTable =
    {
        // U+2500–U+250F
        L,L,N,N, // ─  light horizontal
        H,H,N,N, // ━  heavy horizontal
        N,N,L,L, // │  light vertical
        N,N,H,H, // ┃  heavy vertical
        L,L,N,N, // ┄  triple dash horizontal (→ solid)
        H,H,N,N, // ┅  heavy triple dash horizontal
        N,N,L,L, // ┆  triple dash vertical
        N,N,H,H, // ┇  heavy triple dash vertical
        L,L,N,N, // ┈  quadruple dash horizontal
        H,H,N,N, // ┉  heavy quadruple dash horizontal
        N,N,L,L, // ┊  quadruple dash vertical
        N,N,H,H, // ┋  heavy quadruple dash vertical
        N,L,N,L, // ┌  light down and right
        N,H,N,L, // ┍  down light and right heavy
        N,L,N,H, // ┎  down heavy and right light
        N,H,N,H, // ┏  heavy down and right

        // U+2510–U+251F
        L,N,N,L, // ┐  light down and left
        H,N,N,L, // ┑  down light and left heavy
        L,N,N,H, // ┒  down heavy and left light
        H,N,N,H, // ┓  heavy down and left
        N,L,L,N, // └  light up and right
        N,H,L,N, // ┕  up light and right heavy
        N,L,H,N, // ┖  up heavy and right light
        N,H,H,N, // ┗  heavy up and right
        L,N,L,N, // ┘  light up and left
        H,N,L,N, // ┙  up light and left heavy
        L,N,H,N, // ┚  up heavy and left light
        H,N,H,N, // ┛  heavy up and left
        N,L,L,L, // ├  light vertical and right
        N,H,L,L, // ┝  vertical light and right heavy
        N,L,H,L, // ┞  up heavy and right down light
        N,L,L,H, // ┟  down heavy and right up light

        // U+2520–U+252F
        N,L,H,H, // ┠  vertical heavy and right light
        N,H,H,L, // ┡  down light and right up heavy
        N,H,L,H, // ┢  up light and right down heavy
        N,H,H,H, // ┣  heavy vertical and right
        L,N,L,L, // ┤  light vertical and left
        H,N,L,L, // ┥  vertical light and left heavy
        L,N,H,L, // ┦  up heavy and left down light
        L,N,L,H, // ┧  down heavy and left up light
        L,N,H,H, // ┨  vertical heavy and left light
        H,N,H,L, // ┩  down light and left up heavy
        H,N,L,H, // ┪  up light and left down heavy
        H,N,H,H, // ┫  heavy vertical and left
        L,L,N,L, // ┬  light down and horizontal
        H,L,N,L, // ┭  left heavy and right down light
        L,H,N,L, // ┮  right heavy and left down light
        H,H,N,L, // ┯  down light and horizontal heavy

        // U+2530–U+253F
        L,L,N,H, // ┰  down heavy and horizontal light
        H,L,N,H, // ┱  right light and left down heavy
        L,H,N,H, // ┲  left light and right down heavy
        H,H,N,H, // ┳  heavy down and horizontal
        L,L,L,N, // ┴  light up and horizontal
        H,L,L,N, // ┵  left heavy and right up light
        L,H,L,N, // ┶  right heavy and left up light
        H,H,L,N, // ┷  up light and horizontal heavy
        L,L,H,N, // ┸  up heavy and horizontal light
        H,L,H,N, // ┹  right light and left up heavy
        L,H,H,N, // ┺  left light and right up heavy
        H,H,H,N, // ┻  heavy up and horizontal
        L,L,L,L, // ┼  light vertical and horizontal
        H,L,L,L, // ┽  left heavy and right vertical light
        L,H,L,L, // ┾  right heavy and left vertical light
        H,H,L,L, // ┿  vertical light and horizontal heavy

        // U+2540–U+254F
        L,L,H,L, // ╀  up heavy and down horizontal light
        L,L,L,H, // ╁  down heavy and up horizontal light
        L,L,H,H, // ╂  vertical heavy and horizontal light
        H,L,H,L, // ╃  left up heavy and right down light
        L,H,H,L, // ╄  right up heavy and left down light
        H,L,L,H, // ╅  left down heavy and right up light
        L,H,L,H, // ╆  right down heavy and left up light
        H,H,H,L, // ╇  down light and up horizontal heavy
        H,H,L,H, // ╈  up light and down horizontal heavy
        H,L,H,H, // ╉  right light and left vertical heavy
        L,H,H,H, // ╊  left light and right vertical heavy
        H,H,H,H, // ╋  heavy vertical and horizontal
        L,L,N,N, // ╌  double dash horizontal (→ solid)
        H,H,N,N, // ╍  heavy double dash horizontal
        N,N,L,L, // ╎  double dash vertical
        N,N,H,H, // ╏  heavy double dash vertical

        // U+2550–U+255F
        D,D,N,N, // ═  double horizontal
        N,N,D,D, // ║  double vertical
        N,D,N,L, // ╒  down single and right double
        N,L,N,D, // ╓  down double and right single
        N,D,N,D, // ╔  double down and right
        D,N,N,L, // ╕  down single and left double
        L,N,N,D, // ╖  down double and left single
        D,N,N,D, // ╗  double down and left
        N,D,L,N, // ╘  up single and right double
        N,L,D,N, // ╙  up double and right single
        N,D,D,N, // ╚  double up and right
        D,N,L,N, // ╛  up single and left double
        L,N,D,N, // ╜  up double and left single
        D,N,D,N, // ╝  double up and left
        N,D,L,L, // ╞  vertical single and right double
        N,L,D,D, // ╟  vertical double and right single

        // U+2560–U+256F
        N,D,D,D, // ╠  double vertical and right
        D,N,L,L, // ╡  vertical single and left double
        L,N,D,D, // ╢  vertical double and left single
        D,N,D,D, // ╣  double vertical and left
        D,D,N,L, // ╤  down single and horizontal double
        L,L,N,D, // ╥  down double and horizontal single
        D,D,N,D, // ╦  double down and horizontal
        D,D,L,N, // ╧  up single and horizontal double
        L,L,D,N, // ╨  up double and horizontal single
        D,D,D,N, // ╩  double up and horizontal
        D,D,L,L, // ╪  vertical single and horizontal double
        L,L,D,D, // ╫  vertical double and horizontal single
        D,D,D,D, // ╬  double vertical and horizontal
        N,L,N,L, // ╭  arc down and right (→ square corner)
        L,N,N,L, // ╮  arc down and left
        L,N,L,N, // ╯  arc up and left

        // U+2570–U+257F
        N,L,L,N, // ╰  arc up and right
        X,X,X,X, // ╱  diagonal (fall through to DirectWrite)
        X,X,X,X, // ╲  diagonal
        X,X,X,X, // ╳  diagonal cross
        L,N,N,N, // ╴  light left
        N,N,L,N, // ╵  light up
        N,L,N,N, // ╶  light right
        N,N,N,L, // ╷  light down
        H,N,N,N, // ╸  heavy left
        N,N,H,N, // ╹  heavy up
        N,H,N,N, // ╺  heavy right
        N,N,N,H, // ╻  heavy down
        L,H,N,N, // ╼  light left and heavy right
        N,N,L,H, // ╽  light up and heavy down
        H,L,N,N, // ╾  heavy left and light right
        N,N,H,L, // ╿  heavy up and light down
    };

    // ── Quadrant bitmasks for U+2596–U+259F ─────────────────────────
    // Bit 3=UL, 2=UR, 1=LL, 0=LR
    private static readonly byte[] QuadrantMasks =
    {
        0b_0010, // U+2596 ▖ lower left
        0b_0001, // U+2597 ▗ lower right
        0b_1000, // U+2598 ▘ upper left
        0b_1011, // U+2599 ▙ UL+LL+LR
        0b_1001, // U+259A ▚ UL+LR
        0b_1110, // U+259B ▛ UL+UR+LL
        0b_1101, // U+259C ▜ UL+UR+LR
        0b_0100, // U+259D ▝ upper right
        0b_0110, // U+259E ▞ UR+LL
        0b_0111, // U+259F ▟ UR+LL+LR
    };

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="cp"/> should be rendered by this
    /// renderer instead of DirectWrite.
    /// </summary>
    public static bool IsBoxDrawingCodepoint(uint cp)
        => (cp >= 0x2500 && cp <= 0x257F) || (cp >= 0x2580 && cp <= 0x259F);

    /// <summary>
    /// Renders a box-drawing or block element character into a cell-sized
    /// BGRA byte array. White (0xFF) = full foreground coverage; black (0x00)
    /// = full background. Returns null for characters that should fall
    /// through to DirectWrite (e.g. diagonals).
    /// </summary>
    public static byte[]? Render(uint cp, int cellW, int cellH)
    {
        if (cp >= 0x2580 && cp <= 0x259F)
            return RenderBlockElement(cp, cellW, cellH);

        if (cp < 0x2500 || cp > 0x257F)
            return null;

        int idx = (int)(cp - 0x2500) * 4;
        byte left  = SegmentTable[idx];
        byte right = SegmentTable[idx + 1];
        byte up    = SegmentTable[idx + 2];
        byte down  = SegmentTable[idx + 3];

        // Sentinel: fall through to DirectWrite
        if (left == X) return null;

        var buf = new byte[cellW * cellH * 4];
        RenderSegments(buf, cellW, cellH, left, right, up, down);
        return buf;
    }

    // ── Segment rendering ───────────────────────────────────────────

    private static void RenderSegments(
        byte[] buf, int cellW, int cellH,
        byte left, byte right, byte up, byte down)
    {
        int cX = cellW / 2;
        int cY = cellH / 2;
        int lightThick = Math.Max(1, (int)Math.Round(cellW / 8.0));
        int heavyThick = Math.Max(2, (int)Math.Round(cellW / 4.0));

        // Double-line geometry
        int doubleGap = Math.Max(1, lightThick);
        int halfGap   = doubleGap / 2;
        int halfGapC  = (doubleGap + 1) / 2; // ceil

        if (left != N) DrawHorizontalSegment(buf, cellW, cellH, cX, cY, left, lightThick, heavyThick, halfGap, halfGapC, isLeft: true);
        if (right != N) DrawHorizontalSegment(buf, cellW, cellH, cX, cY, right, lightThick, heavyThick, halfGap, halfGapC, isLeft: false);
        if (up != N) DrawVerticalSegment(buf, cellW, cellH, cX, cY, up, lightThick, heavyThick, halfGap, halfGapC, isUp: true);
        if (down != N) DrawVerticalSegment(buf, cellW, cellH, cX, cY, down, lightThick, heavyThick, halfGap, halfGapC, isUp: false);
    }

    private static void DrawHorizontalSegment(
        byte[] buf, int cellW, int cellH, int cX, int cY,
        byte weight, int lightThick, int heavyThick,
        int halfGap, int halfGapC, bool isLeft)
    {
        int xStart = isLeft ? 0 : cX - heavyThick / 2;
        int xEnd   = isLeft ? cX + (heavyThick + 1) / 2 : cellW;

        if (weight == L || weight == H)
        {
            int thick = weight == L ? lightThick : heavyThick;
            int yStart = cY - thick / 2;
            // Adjust x extent based on own thickness
            int xs = isLeft ? 0 : cX - thick / 2;
            int xe = isLeft ? cX + (thick + 1) / 2 : cellW;
            FillRect(buf, cellW, cellH, xs, yStart, xe, yStart + thick);
        }
        else if (weight == D)
        {
            // Two parallel light lines
            int y1 = cY - halfGap - lightThick;
            int y2 = cY + halfGapC;
            int xs = isLeft ? 0 : cX - lightThick / 2;
            int xe = isLeft ? cX + (lightThick + 1) / 2 : cellW;
            FillRect(buf, cellW, cellH, xs, y1, xe, y1 + lightThick);
            FillRect(buf, cellW, cellH, xs, y2, xe, y2 + lightThick);
        }
    }

    private static void DrawVerticalSegment(
        byte[] buf, int cellW, int cellH, int cX, int cY,
        byte weight, int lightThick, int heavyThick,
        int halfGap, int halfGapC, bool isUp)
    {
        if (weight == L || weight == H)
        {
            int thick = weight == L ? lightThick : heavyThick;
            int xStart = cX - thick / 2;
            int ys = isUp ? 0 : cY - thick / 2;
            int ye = isUp ? cY + (thick + 1) / 2 : cellH;
            FillRect(buf, cellW, cellH, xStart, ys, xStart + thick, ye);
        }
        else if (weight == D)
        {
            int x1 = cX - halfGap - lightThick;
            int x2 = cX + halfGapC;
            int ys = isUp ? 0 : cY - lightThick / 2;
            int ye = isUp ? cY + (lightThick + 1) / 2 : cellH;
            FillRect(buf, cellW, cellH, x1, ys, x1 + lightThick, ye);
            FillRect(buf, cellW, cellH, x2, ys, x2 + lightThick, ye);
        }
    }

    // ── Block element rendering ─────────────────────────────────────

    private static byte[]? RenderBlockElement(uint cp, int cellW, int cellH)
    {
        var buf = new byte[cellW * cellH * 4];
        int halfW = cellW / 2;
        int halfH = cellH / 2;

        switch (cp)
        {
            // Lower blocks (U+2581–U+2587)
            case 0x2580: FillRect(buf, cellW, cellH, 0, 0, cellW, halfH); break;                           // ▀ upper half
            case 0x2581: FillRect(buf, cellW, cellH, 0, cellH * 7 / 8, cellW, cellH); break;               // ▁ lower 1/8
            case 0x2582: FillRect(buf, cellW, cellH, 0, cellH * 3 / 4, cellW, cellH); break;               // ▂ lower 1/4
            case 0x2583: FillRect(buf, cellW, cellH, 0, cellH * 5 / 8, cellW, cellH); break;               // ▃ lower 3/8
            case 0x2584: FillRect(buf, cellW, cellH, 0, halfH, cellW, cellH); break;                       // ▄ lower half
            case 0x2585: FillRect(buf, cellW, cellH, 0, cellH * 3 / 8, cellW, cellH); break;               // ▅ lower 5/8
            case 0x2586: FillRect(buf, cellW, cellH, 0, cellH / 4, cellW, cellH); break;                   // ▆ lower 3/4
            case 0x2587: FillRect(buf, cellW, cellH, 0, cellH / 8, cellW, cellH); break;                   // ▇ lower 7/8

            // Full block
            case 0x2588: FillRect(buf, cellW, cellH, 0, 0, cellW, cellH); break;                           // █ full block

            // Left blocks (U+2589–U+258F)
            case 0x2589: FillRect(buf, cellW, cellH, 0, 0, cellW * 7 / 8, cellH); break;                   // ▉ left 7/8
            case 0x258A: FillRect(buf, cellW, cellH, 0, 0, cellW * 3 / 4, cellH); break;                   // ▊ left 3/4
            case 0x258B: FillRect(buf, cellW, cellH, 0, 0, cellW * 5 / 8, cellH); break;                   // ▋ left 5/8
            case 0x258C: FillRect(buf, cellW, cellH, 0, 0, halfW, cellH); break;                           // ▌ left half
            case 0x258D: FillRect(buf, cellW, cellH, 0, 0, cellW * 3 / 8, cellH); break;                   // ▍ left 3/8
            case 0x258E: FillRect(buf, cellW, cellH, 0, 0, cellW / 4, cellH); break;                       // ▎ left 1/4
            case 0x258F: FillRect(buf, cellW, cellH, 0, 0, cellW / 8, cellH); break;                       // ▏ left 1/8

            // Right half
            case 0x2590: FillRect(buf, cellW, cellH, halfW, 0, cellW, cellH); break;                       // ▐ right half

            // Shade characters
            case 0x2591: FillShade(buf, cellW, cellH, 64); break;                                           // ░ light shade 25%
            case 0x2592: FillShade(buf, cellW, cellH, 128); break;                                          // ▒ medium shade 50%
            case 0x2593: FillShade(buf, cellW, cellH, 192); break;                                          // ▓ dark shade 75%

            // Upper/right 1/8 blocks
            case 0x2594: FillRect(buf, cellW, cellH, 0, 0, cellW, cellH / 8); break;                       // ▔ upper 1/8
            case 0x2595: FillRect(buf, cellW, cellH, cellW * 7 / 8, 0, cellW, cellH); break;               // ▕ right 1/8

            // Quadrant characters (U+2596–U+259F)
            case >= 0x2596 and <= 0x259F:
                int mask = QuadrantMasks[cp - 0x2596];
                if ((mask & 0b_1000) != 0) FillRect(buf, cellW, cellH, 0, 0, halfW, halfH);         // UL
                if ((mask & 0b_0100) != 0) FillRect(buf, cellW, cellH, halfW, 0, cellW, halfH);     // UR
                if ((mask & 0b_0010) != 0) FillRect(buf, cellW, cellH, 0, halfH, halfW, cellH);     // LL
                if ((mask & 0b_0001) != 0) FillRect(buf, cellW, cellH, halfW, halfH, cellW, cellH); // LR
                break;

            default:
                return null;
        }

        return buf;
    }

    // ── Pixel helpers ───────────────────────────────────────────────

    /// <summary>
    /// Fills a rectangle in the BGRA buffer with white (full coverage).
    /// Coordinates are clamped to buffer bounds.
    /// </summary>
    private static void FillRect(byte[] buf, int cellW, int cellH,
        int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, 0, cellW);
        x1 = Math.Clamp(x1, 0, cellW);
        y0 = Math.Clamp(y0, 0, cellH);
        y1 = Math.Clamp(y1, 0, cellH);

        for (int y = y0; y < y1; y++)
        {
            int rowOffset = y * cellW * 4;
            for (int x = x0; x < x1; x++)
            {
                int idx = rowOffset + x * 4;
                buf[idx]     = 0xFF; // B
                buf[idx + 1] = 0xFF; // G
                buf[idx + 2] = 0xFF; // R
                buf[idx + 3] = 0xFF; // A
            }
        }
    }

    /// <summary>
    /// Fills the entire cell with a uniform shade level (0–255) across all channels.
    /// </summary>
    private static void FillShade(byte[] buf, int cellW, int cellH, byte level)
    {
        for (int i = 0; i < cellW * cellH; i++)
        {
            int idx = i * 4;
            buf[idx]     = level; // B
            buf[idx + 1] = level; // G
            buf[idx + 2] = level; // R
            buf[idx + 3] = 0xFF; // A
        }
    }
}
