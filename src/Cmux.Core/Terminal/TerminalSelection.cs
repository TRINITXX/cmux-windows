using System.Text;

namespace Cmux.Core.Terminal;

public struct SelectionPoint
{
    public int Row;
    public int Col;

    public SelectionPoint(int row, int col)
    {
        Row = row;
        Col = col;
    }
}

/// <summary>
/// Manages text selection in the terminal buffer.
/// Coordinates are stored as virtual line numbers (scrollback + scrollOffset + visRow)
/// so that selection tracks content across scrolling.
/// </summary>
public class TerminalSelection
{
    private SelectionPoint? _start;
    private SelectionPoint? _end;

    public bool HasSelection => _start.HasValue && _end.HasValue;
    public SelectionPoint? Start => _start;
    public SelectionPoint? End => _end;

    public event Action? SelectionChanged;

    public void StartSelection(int visRow, int col, int scrollOffset, int scrollbackCount)
    {
        int virtualLine = scrollbackCount + scrollOffset + visRow;
        _start = new SelectionPoint(virtualLine, col);
        _end = new SelectionPoint(virtualLine, col);
        SelectionChanged?.Invoke();
    }

    public void ExtendSelection(int visRow, int col, int scrollOffset, int scrollbackCount)
    {
        if (!_start.HasValue) return;
        int virtualLine = scrollbackCount + scrollOffset + visRow;
        _end = new SelectionPoint(virtualLine, col);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        _start = null;
        _end = null;
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Gets the normalized selection range (start before end).
    /// </summary>
    public (SelectionPoint start, SelectionPoint end)? GetNormalizedRange()
    {
        if (!_start.HasValue || !_end.HasValue) return null;

        var s = _start.Value;
        var e = _end.Value;

        if (s.Row > e.Row || (s.Row == e.Row && s.Col > e.Col))
            (s, e) = (e, s);

        return (s, e);
    }

    /// <summary>
    /// Tests whether a cell is within the current selection.
    /// Converts the visual row to a virtual line for comparison with stored coordinates.
    /// </summary>
    public bool IsSelected(int visRow, int col, int scrollOffset, int scrollbackCount)
    {
        var range = GetNormalizedRange();
        if (!range.HasValue) return false;

        int virtualLine = scrollbackCount + scrollOffset + visRow;
        var (s, e) = range.Value;

        if (virtualLine < s.Row || virtualLine > e.Row) return false;
        if (virtualLine == s.Row && virtualLine == e.Row) return col >= s.Col && col <= e.Col;
        if (virtualLine == s.Row) return col >= s.Col;
        if (virtualLine == e.Row) return col <= e.Col;
        return true;
    }

    /// <summary>
    /// Extracts selected text from the terminal buffer.
    /// Coordinates are already virtual lines — iterate directly.
    /// </summary>
    public string GetSelectedText(TerminalBuffer buffer)
    {
        var range = GetNormalizedRange();
        if (!range.HasValue) return "";

        var (s, e) = range.Value;
        var sb = new StringBuilder();
        int scrollbackCount = buffer.ScrollbackCount;

        for (int virtualLine = s.Row; virtualLine <= e.Row; virtualLine++)
        {
            bool isScrollback = virtualLine < scrollbackCount;
            int bufferRow = virtualLine - scrollbackCount;

            int startCol = virtualLine == s.Row ? s.Col : 0;
            int endCol = virtualLine == e.Row ? e.Col : buffer.Cols - 1;

            for (int col = startCol; col <= endCol && col < buffer.Cols; col++)
            {
                char ch;
                if (isScrollback)
                {
                    var line = buffer.GetScrollbackLine(virtualLine);
                    ch = (line != null && col < line.Length) ? line[col].Character : '\0';
                }
                else if (bufferRow >= 0 && bufferRow < buffer.Rows)
                {
                    ch = buffer.CellAt(bufferRow, col).Character;
                }
                else
                {
                    ch = '\0';
                }
                sb.Append(ch == '\0' ? ' ' : ch);
            }

            // Trim trailing spaces on each line
            if (virtualLine < e.Row)
            {
                while (sb.Length > 0 && sb[^1] == ' ')
                    sb.Length--;
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Selects the word at the given position (double-click behavior).
    /// Stores as virtual line coordinates.
    /// </summary>
    public void SelectWord(TerminalBuffer buffer, int visRow, int col, int scrollOffset)
    {
        if (col < 0 || col >= buffer.Cols) return;

        int scrollbackCount = buffer.ScrollbackCount;
        int virtualLine = scrollbackCount + scrollOffset + visRow;
        bool isScrollback = virtualLine < scrollbackCount;
        int bufferRow = virtualLine - scrollbackCount;

        char GetChar(int c)
        {
            if (isScrollback)
            {
                var line = buffer.GetScrollbackLine(virtualLine);
                return (line != null && c < line.Length) ? line[c].Character : '\0';
            }
            if (bufferRow >= 0 && bufferRow < buffer.Rows)
                return buffer.CellAt(bufferRow, c).Character;
            return '\0';
        }

        bool IsWordChar(char ch) => ch != '\0' && ch != ' ' && (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');

        if (!IsWordChar(GetChar(col)))
        {
            _start = new SelectionPoint(virtualLine, col);
            _end = new SelectionPoint(virtualLine, col);
            SelectionChanged?.Invoke();
            return;
        }

        int startCol = col;
        int endCol = col;

        while (startCol > 0 && IsWordChar(GetChar(startCol - 1)))
            startCol--;

        while (endCol < buffer.Cols - 1 && IsWordChar(GetChar(endCol + 1)))
            endCol++;

        _start = new SelectionPoint(virtualLine, startCol);
        _end = new SelectionPoint(virtualLine, endCol);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Selects the entire line (triple-click behavior).
    /// Stores as virtual line coordinates.
    /// </summary>
    public void SelectLine(int visRow, int cols, int scrollOffset, int scrollbackCount)
    {
        int virtualLine = scrollbackCount + scrollOffset + visRow;
        _start = new SelectionPoint(virtualLine, 0);
        _end = new SelectionPoint(virtualLine, cols - 1);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Selects all visible content in the terminal buffer.
    /// </summary>
    public void SelectAll(int rows, int cols, int scrollOffset, int scrollbackCount)
    {
        int firstVirtual = scrollbackCount + scrollOffset;
        int lastVirtual = firstVirtual + rows - 1;
        _start = new SelectionPoint(firstVirtual, 0);
        _end = new SelectionPoint(lastVirtual, cols - 1);
        SelectionChanged?.Invoke();
    }
}
