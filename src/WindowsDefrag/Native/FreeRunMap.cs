namespace WindowsDefrag.Native;

/// <summary>Sorted free-cluster runs so placement does not scan the bitmap per file.</summary>
internal sealed class FreeRunMap
{
    private readonly List<(long Start, long Length)> _runs = [];

    public static FreeRunMap FromSession(VolumeSession session)
    {
        var map = new FreeRunMap();
        long runStart = -1;
        var total = session.BitmapClusterCount;
        for (long c = 0; c < total; c++)
        {
            if (!session.IsAllocated(c))
            {
                if (runStart < 0)
                    runStart = c;
            }
            else if (runStart >= 0)
            {
                map._runs.Add((runStart, c - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
            map._runs.Add((runStart, total - runStart));

        return map;
    }

    public bool TryFirstFit(long minClusters, long searchFrom, out long startLcn)
    {
        startLcn = -1;
        if (minClusters <= 0)
            minClusters = 1;

        for (var i = 0; i < _runs.Count; i++)
        {
            var (s, len) = _runs[i];
            var begin = s < searchFrom ? searchFrom : s;
            if (begin >= s + len)
                continue;
            if (s + len - begin >= minClusters)
            {
                startLcn = begin;
                return true;
            }
        }

        if (searchFrom > 0)
            return TryFirstFit(minClusters, 0, out startLcn);

        return false;
    }

    public bool TryLargest(out long startLcn, out long length)
    {
        startLcn = -1;
        length = 0;
        foreach (var (s, len) in _runs)
        {
            if (len > length)
            {
                startLcn = s;
                length = len;
            }
        }

        return length > 0;
    }

    public void Allocate(long start, long count)
    {
        if (count <= 0)
            return;
        var end = start + count;
        for (var i = 0; i < _runs.Count;)
        {
            var (s, len) = _runs[i];
            var e = s + len;
            if (e <= start || s >= end)
            {
                i++;
                continue;
            }

            _runs.RemoveAt(i);
            if (s < start)
            {
                _runs.Insert(i, (s, start - s));
                i++;
            }

            if (e > end)
            {
                _runs.Insert(i, (end, e - end));
                i++;
            }
        }
    }

    public void Free(long start, long count)
    {
        if (count <= 0)
            return;
        var end = start + count;
        var i = 0;
        while (i < _runs.Count && _runs[i].Start + _runs[i].Length < start)
            i++;

        _runs.Insert(i, (start, count));
        MergeFrom(Math.Max(0, i - 1));
    }

    private void MergeFrom(int index)
    {
        var i = index;
        while (i + 1 < _runs.Count)
        {
            var (s, len) = _runs[i];
            var (ns, nlen) = _runs[i + 1];
            if (s + len >= ns)
            {
                var end = Math.Max(s + len, ns + nlen);
                _runs[i] = (s, end - s);
                _runs.RemoveAt(i + 1);
            }
            else
            {
                i++;
                if (i > index + 1)
                    break;
            }
        }
    }
}
