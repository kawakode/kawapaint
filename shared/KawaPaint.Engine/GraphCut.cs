// KawaPaint - s/t minimum cut on a sparse graph, for Select Subject's GrabCut segmentation.
//
// This is the Boykov-Kolmogorov augmenting-path algorithm ("An Experimental Comparison of
// Min-Cut/Max-Flow Algorithms for Energy Minimization in Vision", PAMI 2004): two search trees
// grow from the source and the sink, meet on an arc, push the bottleneck along the path, then
// re-adopt the nodes that the push cut off instead of rebuilding the trees from scratch. On the
// short-path, grid-shaped graphs that image segmentation builds it beats the textbook algorithms
// by a wide margin, and it is plain managed code with no native dependency, so it runs the same
// on desktop, Android and in the browser.

namespace KawaPaint.Engine;

public sealed class GraphCut
{
    // Parent markers. A non-negative parent is the arc from the node towards its tree's root.
    private const int Free = -1, Terminal = -2, Orphan = -3;

    private readonly int _nodeCount;
    private readonly double[] _terminal;   // > 0: residual source->node, < 0: residual node->sink
    private readonly int[] _firstArc;

    private int[] _head, _next;
    private double[] _residual;
    private int _arcCount;

    // Search state, sized once the cut runs.
    private int[] _parent = [];
    private bool[] _isSink = [];
    private int[] _stamp = [];
    private int[] _dist = [];
    private bool[] _queued = [];

    public double Flow { get; private set; }

    public GraphCut(int nodeCount, int arcCapacityHint = 0)
    {
        _nodeCount = nodeCount;
        _terminal = new double[nodeCount];
        _firstArc = new int[nodeCount];
        Array.Fill(_firstArc, -1);
        int capacity = Math.Max(16, arcCapacityHint);
        _head = new int[capacity];
        _next = new int[capacity];
        _residual = new double[capacity];
    }

    public int NodeCount => _nodeCount;

    /// <summary>Adds capacity from the source to <paramref name="node"/> and from it to the sink.
    /// Only the difference matters to the cut; the common part is booked straight into Flow.</summary>
    public void AddTerminalWeights(int node, double toSource, double toSink)
    {
        double delta = _terminal[node];
        if (delta > 0) toSource += delta; else toSink -= delta;
        Flow += Math.Min(toSource, toSink);
        _terminal[node] = toSource - toSink;
    }

    /// <summary>Adds the arc pair a->b (capacity) and b->a (reverse capacity).</summary>
    public void AddEdge(int a, int b, double capacity, double reverseCapacity)
    {
        if (_arcCount + 2 > _head.Length)
        {
            int size = _head.Length * 2;
            Array.Resize(ref _head, size);
            Array.Resize(ref _next, size);
            Array.Resize(ref _residual, size);
        }
        // Arcs come in pairs (2k, 2k+1), so an arc's sister is index ^ 1.
        int forward = _arcCount++, backward = _arcCount++;
        _head[forward] = b; _residual[forward] = capacity; _next[forward] = _firstArc[a]; _firstArc[a] = forward;
        _head[backward] = a; _residual[backward] = reverseCapacity; _next[backward] = _firstArc[b]; _firstArc[b] = backward;
    }

    /// <summary>After <see cref="Solve"/>: whether the node ended on the source side of the cut.
    /// Nodes neither tree reached could go either way at the same cost; they report false.</summary>
    public bool IsSourceSide(int node) => _parent[node] != Free && !_isSink[node];

    public double Solve()
    {
        int n = _nodeCount;
        _parent = new int[n];
        _isSink = new bool[n];
        _stamp = new int[n];
        _dist = new int[n];
        _queued = new bool[n];
        var active = new Queue<int>();
        var orphans = new LinkedList<int>();

        for (int i = 0; i < n; i++)
        {
            if (_terminal[i] > 0) { _parent[i] = Terminal; _isSink[i] = false; _dist[i] = 1; Activate(active, i); }
            else if (_terminal[i] < 0) { _parent[i] = Terminal; _isSink[i] = true; _dist[i] = 1; Activate(active, i); }
            else _parent[i] = Free;
        }

        int time = 0;
        int current = -1;
        while (true)
        {
            int i = current;
            if (i < 0 || _parent[i] == Free)
            {
                i = -1;
                while (active.Count > 0)
                {
                    int candidate = active.Dequeue();
                    _queued[candidate] = false;
                    if (_parent[candidate] != Free) { i = candidate; break; }
                }
                if (i < 0) break;
            }

            // Grow the tree that i belongs to until it touches the other one.
            int meeting = -1;
            if (!_isSink[i])
            {
                for (int a = _firstArc[i]; a >= 0; a = _next[a])
                {
                    if (_residual[a] <= 0) continue;
                    int j = _head[a];
                    if (_parent[j] == Free)
                    {
                        _isSink[j] = false; _parent[j] = a ^ 1; _stamp[j] = _stamp[i]; _dist[j] = _dist[i] + 1;
                        Activate(active, j);
                    }
                    else if (_isSink[j]) { meeting = a; break; }
                    else if (_stamp[j] <= _stamp[i] && _dist[j] > _dist[i])
                    {
                        _parent[j] = a ^ 1; _stamp[j] = _stamp[i]; _dist[j] = _dist[i] + 1;
                    }
                }
            }
            else
            {
                for (int a = _firstArc[i]; a >= 0; a = _next[a])
                {
                    if (_residual[a ^ 1] <= 0) continue;
                    int j = _head[a];
                    if (_parent[j] == Free)
                    {
                        _isSink[j] = true; _parent[j] = a ^ 1; _stamp[j] = _stamp[i]; _dist[j] = _dist[i] + 1;
                        Activate(active, j);
                    }
                    else if (!_isSink[j]) { meeting = a ^ 1; break; }
                    else if (_stamp[j] <= _stamp[i] && _dist[j] > _dist[i])
                    {
                        _parent[j] = a ^ 1; _stamp[j] = _stamp[i]; _dist[j] = _dist[i] + 1;
                    }
                }
            }

            time++;
            if (meeting < 0) { current = -1; continue; }

            current = i;   // i may still have unexplored arcs: look at it again next round
            Augment(meeting, orphans);
            while (orphans.First is { } first)
            {
                int orphan = first.Value;
                orphans.RemoveFirst();
                if (_isSink[orphan]) AdoptSinkOrphan(orphan, time, active, orphans);
                else AdoptSourceOrphan(orphan, time, active, orphans);
            }
        }
        return Flow;
    }

    private void Activate(Queue<int> active, int node)
    {
        if (_queued[node]) return;
        _queued[node] = true;
        active.Enqueue(node);
    }

    /// <summary>Pushes the bottleneck along source -> tail(middle) -> head(middle) -> sink.</summary>
    private void Augment(int middle, LinkedList<int> orphans)
    {
        double bottleneck = _residual[middle];
        int i;
        for (i = _head[middle ^ 1]; _parent[i] != Terminal; i = _head[_parent[i]])
            bottleneck = Math.Min(bottleneck, _residual[_parent[i] ^ 1]);
        bottleneck = Math.Min(bottleneck, _terminal[i]);
        for (i = _head[middle]; _parent[i] != Terminal; i = _head[_parent[i]])
            bottleneck = Math.Min(bottleneck, _residual[_parent[i]]);
        bottleneck = Math.Min(bottleneck, -_terminal[i]);

        _residual[middle ^ 1] += bottleneck;
        _residual[middle] -= bottleneck;

        for (i = _head[middle ^ 1]; ; )
        {
            int a = _parent[i];
            if (a == Terminal) break;
            _residual[a] += bottleneck;
            _residual[a ^ 1] -= bottleneck;
            int up = _head[a];
            if (_residual[a ^ 1] <= 0) MakeOrphan(i, orphans, front: true);
            i = up;
        }
        _terminal[i] -= bottleneck;
        if (_terminal[i] <= 0) MakeOrphan(i, orphans, front: true);

        for (i = _head[middle]; ; )
        {
            int a = _parent[i];
            if (a == Terminal) break;
            _residual[a ^ 1] += bottleneck;
            _residual[a] -= bottleneck;
            int up = _head[a];
            if (_residual[a] <= 0) MakeOrphan(i, orphans, front: true);
            i = up;
        }
        _terminal[i] += bottleneck;
        if (_terminal[i] >= 0) MakeOrphan(i, orphans, front: true);

        Flow += bottleneck;
    }

    private void MakeOrphan(int node, LinkedList<int> orphans, bool front)
    {
        _parent[node] = Orphan;
        if (front) orphans.AddFirst(node); else orphans.AddLast(node);
    }

    /// <summary>Distance from <paramref name="start"/> to its tree's terminal, or -1 when the path
    /// runs into an orphan. Caches what it learns in the stamp/dist marks.</summary>
    private int RootDistance(int start, int time)
    {
        int d = 0;
        int j = start;
        while (true)
        {
            if (_stamp[j] == time) { d += _dist[j]; break; }
            int a = _parent[j];
            d++;
            if (a == Terminal) { _stamp[j] = time; _dist[j] = 1; break; }
            if (a == Orphan || a == Free) return -1;
            j = _head[a];
        }
        // Mark the walked path with its now-known distances.
        for (j = start; _stamp[j] != time; j = _head[_parent[j]])
        {
            _stamp[j] = time;
            _dist[j] = d;
            d--;
        }
        return _dist[start];
    }

    private void AdoptSourceOrphan(int i, int time, Queue<int> active, LinkedList<int> orphans)
    {
        int bestArc = -1, bestDist = int.MaxValue;
        for (int a = _firstArc[i]; a >= 0; a = _next[a])
        {
            if (_residual[a ^ 1] <= 0) continue;
            int j = _head[a];
            if (_isSink[j] || _parent[j] < 0 && _parent[j] != Terminal) continue;
            int d = RootDistance(j, time);
            if (d >= 0 && d < bestDist) { bestDist = d; bestArc = a; }
        }
        if (bestArc >= 0)
        {
            _parent[i] = bestArc; _stamp[i] = time; _dist[i] = bestDist + 1;
            return;
        }

        // No way back to the source: i drops out of the tree, and so do the nodes hanging off it.
        for (int a = _firstArc[i]; a >= 0; a = _next[a])
        {
            int j = _head[a];
            int pa = _parent[j];
            if (_isSink[j] || pa == Free) continue;
            if (_residual[a ^ 1] > 0) Activate(active, j);
            if (pa >= 0 && _head[pa] == i) MakeOrphan(j, orphans, front: false);
        }
        _parent[i] = Free;
    }

    private void AdoptSinkOrphan(int i, int time, Queue<int> active, LinkedList<int> orphans)
    {
        int bestArc = -1, bestDist = int.MaxValue;
        for (int a = _firstArc[i]; a >= 0; a = _next[a])
        {
            if (_residual[a] <= 0) continue;
            int j = _head[a];
            if (!_isSink[j] || _parent[j] < 0 && _parent[j] != Terminal) continue;
            int d = RootDistance(j, time);
            if (d >= 0 && d < bestDist) { bestDist = d; bestArc = a; }
        }
        if (bestArc >= 0)
        {
            _parent[i] = bestArc; _stamp[i] = time; _dist[i] = bestDist + 1;
            return;
        }

        for (int a = _firstArc[i]; a >= 0; a = _next[a])
        {
            int j = _head[a];
            int pa = _parent[j];
            if (!_isSink[j] || pa == Free) continue;
            if (_residual[a] > 0) Activate(active, j);
            if (pa >= 0 && _head[pa] == i) MakeOrphan(j, orphans, front: false);
        }
        _parent[i] = Free;
    }
}
