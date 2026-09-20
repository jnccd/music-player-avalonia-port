using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace MusicPlayerAvaloniaPort.Views.Options;

/// <summary>
/// Arranges children that each need a different amount of space so that they use as little of a window as
/// possible.
///
/// The options window used a <see cref="WrapPanel"/>: it fills row by row, so every child of a row lands in
/// a row that is as tall as the tallest child of that row and the shorter children leave the rest of the
/// row empty - that is where the options window's large empty areas came from. This panel instead keeps
/// every child at the height its content asked for and stacks the children into columns: the children keep
/// the order they are declared in, a column holds a run of them and all columns start at the top.
///
/// All children of a column are as wide as the widest child of that column, so the boxes of a column line up
/// and fill the column instead of leaving a ragged edge. The children of the options window are group boxes
/// whose texts wrap at their own MaxWidth, so a wider box only makes the rows of the group wider - it does
/// not make the box taller.
///
/// The panel picks the split into columns that keeps <em>the tallest column as short as possible</em>, so
/// the window never grows taller than the tallest group needs - that is the height the groups already had
/// to fit in a row - and the rest of the groups fill the space next to it. Of the splits that tie on that
/// height (there is always one: a column per child), the one needing the least area wins, so the groups are
/// packed as tightly as possible instead of being spread out. The desired size of the panel is that box, so
/// the (size to content) options window shrinks to the groups.
///
/// A child is never stretched towards its column's height: it keeps the height its content needs, and the
/// margins of the children are what separates them. When the panel gets less width than the chosen split
/// needs (a window narrower than the content, e.g. on a small screen), a split with more, taller columns is
/// used instead.
/// </summary>
public class SpaceFillingPanel : Panel
{
    /// <summary>Sizes that differ by less than this many pixels count as the same height or width.</summary>
    const double ExtentEpsilon = 0.5;

    /// <summary>Two candidate arrangements whose boxes differ by less than this many square pixels are the same layout.</summary>
    const double AreaEpsilon = 0.5;

    /// <summary>
    /// Up to this many children every split into columns is tried. Above it the number of splits (2^(n-1))
    /// grows too fast, so the columns are filled towards every height a column can end at instead, which
    /// finds the same arrangement for evenly sized children and a good one otherwise.
    /// </summary>
    const int ExhaustiveChildLimit = 12;

    /// <summary>The size every child is arranged with: the width of its column and the height it needs there.</summary>
    Size[] sizes = Array.Empty<Size>();

    /// <summary>Where every child goes, from the last measure pass.</summary>
    Point[] positions = Array.Empty<Point>();

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            sizes = Array.Empty<Size>();
            positions = Array.Empty<Point>();
            return default;
        }

        // Children are measured with the width they may use and an unlimited height, so each one reports the
        // size its content really needs (the texts of the groups wrap at their own MaxWidth). The desired
        // size of a child contains its margin, which is what ends up separating the children.
        bool widthIsLimited = !double.IsInfinity(availableSize.Width);
        double availableWidth = widthIsLimited ? availableSize.Width : double.PositiveInfinity;

        var natural = new Size[count];
        for (int i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(availableWidth, double.PositiveInfinity));
            natural[i] = Children[i].DesiredSize;
        }

        Layout layout = FindLayout(natural, availableWidth);

        // Every child takes the width of its column and keeps the height it needs at that width. Measuring
        // the child again with that width is what gives that height: a wider child needs the same or fewer
        // lines of text, so the columns only ever get shorter than the split promised.
        sizes = new Size[count];
        var heights = new double[count];
        for (int i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(layout.ItemWidths[i], double.PositiveInfinity));
            heights[i] = Children[i].DesiredSize.Height;
            sizes[i] = new Size(layout.ItemWidths[i], heights[i]);
        }

        positions = layout.Positions;
        return new Size(layout.Width, layout.HeightOf(heights));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The children are arranged with what the measure pass decided; a host that hands over less room
        // than the panel asked for (it is never the options window, which sizes to its content) clips them
        // rather than moving them around behind the back of a layout pass.
        if (sizes.Length != Children.Count)
        {
            for (int i = 0; i < Children.Count; i++)
                Children[i].Arrange(new Rect(Children[i].DesiredSize));
            return finalSize;
        }

        for (int i = 0; i < Children.Count; i++)
            Children[i].Arrange(new Rect(positions[i], sizes[i]));

        return finalSize;
    }

    // ---------- Choosing the arrangement ----------

    /// <summary>
    /// Splits the children (keeping their order) into columns so that the window stays as short as the
    /// groups allow it to be. A column is as wide as its widest child and as tall as its children together;
    /// the split with the shortest tallest column wins, so adding a child to a column only ever happens
    /// while it stays within the height of the tallest child. Splits of that height are then compared by the
    /// area they need (the narrower box wins), and splits that would be wider than
    /// <paramref name="availableWidth"/> are not considered at all.
    /// </summary>
    static Layout FindLayout(Size[] sizes, double availableWidth)
    {
        int count = sizes.Length;

        // The width of every run of children, so a column's width is a lookup: runWidth[start, end] is the
        // widest child of the run start..end.
        var runWidth = new double[count, count];
        for (int start = 0; start < count; start++)
        {
            double widest = 0;
            for (int end = start; end < count; end++)
            {
                widest = Math.Max(widest, sizes[end].Width);
                runWidth[start, end] = widest;
            }
        }

        Layout? best = null;
        if (count <= ExhaustiveChildLimit)
        {
            // Bit i of a split set means "the column ends at child i"; a set without any bit keeps all
            // children in a single column. The last child always ends a column.
            int splitCount = 1 << (count - 1);
            for (int splits = 0; splits < splitCount; splits++)
                best = Consider(sizes, runWidth, splits, availableWidth, best);
        }
        else
        {
            var targets = new List<double>(count);
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum += sizes[i].Height;
                targets.Add(sum);
            }

            foreach (double target in targets)
                best = Consider(sizes, runWidth, SplitsForTarget(sizes, target), availableWidth, best);
        }

        // A child wider than the panel fits nowhere: one column is then the best that can be done.
        return best ?? BuildLayout(sizes, runWidth, 0, Math.Max(runWidth[0, count - 1], availableWidth), SumHeight(sizes, 0, count - 1));
    }

    /// <summary>Evaluates one arrangement and keeps it when it beats the best one found so far.</summary>
    static Layout? Consider(Size[] sizes, double[,] runWidth, int splits, double availableWidth, Layout? best)
    {
        double width = 0;
        double height = 0;
        int start = 0;

        for (int end = 0; end < sizes.Length; end++)
        {
            if (end < sizes.Length - 1 && (splits & (1 << end)) == 0)
                continue;

            width += runWidth[start, end];
            height = Math.Max(height, SumHeight(sizes, start, end));
            start = end + 1;
        }

        if (width > availableWidth)
            return best;

        if (best != null && !IsBetter(width, height, best.Width, best.Height))
            return best;

        return BuildLayout(sizes, runWidth, splits, width, height);
    }

    /// <summary>True when a box uses the window better than the best box found so far.</summary>
    static bool IsBetter(double width, double height, double bestWidth, double bestHeight)
    {
        // The shorter box wins: the window should never be taller than the tallest group needs, and every
        // group that is stacked somewhere below another makes the whole window taller.
        if (height < bestHeight - ExtentEpsilon)
            return true;
        if (height > bestHeight + ExtentEpsilon)
            return false;

        // Both are as short as they can be: the one that wastes less space is the one to take (the same
        // height means this compares the widths, but the box the children really occupy is the area).
        double area = width * height;
        double bestArea = bestWidth * bestHeight;

        if (area < bestArea - AreaEpsilon)
            return true;
        if (area > bestArea + AreaEpsilon)
            return false;

        return width < bestWidth - ExtentEpsilon;
    }

    /// <summary>
    /// Lays the children out for an arrangement: each child is placed below the previous one of its column
    /// and a column starts right of the previous one, so the children of a column line up at the top. Every
    /// child of a column is as wide as the widest one of them.
    /// </summary>
    static Layout BuildLayout(Size[] sizes, double[,] runWidth, int splits, double width, double height)
    {
        var positions = new Point[sizes.Length];
        var itemWidths = new double[sizes.Length];
        var columnStarts = new List<int>();
        double x = 0;
        int start = 0;

        for (int end = 0; end < sizes.Length; end++)
        {
            if (end < sizes.Length - 1 && (splits & (1 << end)) == 0)
                continue;

            double columnWidth = runWidth[start, end];
            double y = 0;
            for (int i = start; i <= end; i++)
            {
                positions[i] = new Point(x, y);
                itemWidths[i] = columnWidth;
                y += sizes[i].Height;
            }

            columnStarts.Add(start);
            x += columnWidth;
            start = end + 1;
        }

        return new Layout(positions, itemWidths, columnStarts.ToArray(), width, height);
    }

    /// <summary>
    /// The split set of the arrangement that fills each column with children until the column would grow
    /// past <paramref name="targetHeight"/>.
    /// </summary>
    static int SplitsForTarget(Size[] sizes, double targetHeight)
    {
        int splits = 0;
        double columnHeight = 0;

        for (int i = 0; i < sizes.Length; i++)
        {
            if (i > 0 && columnHeight + sizes[i].Height > targetHeight)
            {
                splits |= 1 << (i - 1);
                columnHeight = 0;
            }

            columnHeight += sizes[i].Height;
        }

        return splits;
    }

    static double SumHeight(Size[] sizes, int start, int end)
    {
        double sum = 0;
        for (int i = start; i <= end; i++)
            sum += sizes[i].Height;
        return sum;
    }

    /// <summary>One arrangement: where every child goes, how wide it is and the box the children occupy.</summary>
    sealed class Layout
    {
        public Layout(Point[] positions, double[] itemWidths, int[] columnStarts, double width, double height)
        {
            Positions = positions;
            ItemWidths = itemWidths;
            ColumnStarts = columnStarts;
            Width = width;
            Height = height;
        }

        public Point[] Positions { get; }

        /// <summary>The width of the column a child belongs to, which is the width the child is arranged with.</summary>
        public double[] ItemWidths { get; }

        /// <summary>The index of the first child of every column.</summary>
        public int[] ColumnStarts { get; }

        public double Width { get; }
        public double Height { get; }

        /// <summary>
        /// The height the arrangement really needs for the given heights of the children, which is the
        /// tallest column. The chosen arrangement is based on the heights the children reported while being
        /// measured, and this is how tall the columns end up once every child has the width of its column.
        /// </summary>
        public double HeightOf(double[] itemHeights)
        {
            double height = 0;

            for (int column = 0; column < ColumnStarts.Length; column++)
            {
                int start = ColumnStarts[column];
                int end = column + 1 < ColumnStarts.Length ? ColumnStarts[column + 1] - 1 : itemHeights.Length - 1;

                double columnHeight = 0;
                for (int i = start; i <= end; i++)
                    columnHeight += itemHeights[i];

                height = Math.Max(height, columnHeight);
            }

            return height;
        }
    }
}
