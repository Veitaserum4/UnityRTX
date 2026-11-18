using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace UnityRemix.Games.Ultrakill
{
    /// <summary>
    /// UltrakillStainPruner on a background thread: a room filled with blood is thousands of
    /// coverage tests (about a millisecond each), which the main thread cannot afford. The main
    /// thread submits each new read of the stains; the worker publishes which to draw, at once
    /// after a submission (the new stains drawn untested) and then as testing hides more.
    /// </summary>
    internal sealed class UltrakillStainPruning
    {
        /// <summary>One read of the game's stain quads (never changed once submitted).</summary>
        public sealed class Stains
        {
            public Vector3[] vertices, normals;
            public Vector2[] uvs;
            public int Count => vertices.Length / 4;
        }

        /// <summary>Which stains of a read to draw.</summary>
        public sealed class Result
        {
            public Stains stains;
            public bool[] kept;
            public int hidden;
            public bool testing;
        }

        // Progress is published at most this often while testing (each one rebuilds the mesh).
        private const double PublishMilliseconds = 250;

        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread thread;
        private Stains submitted;
        private bool resetRequested;
        private bool[] atlas;
        private int atlasWidth, atlasHeight;
        private volatile Result latest;

        public Result Latest => latest;

        public void SetAtlas(bool[] opaque, int width, int height)
        {
            lock (gate)
            {
                atlas = opaque;
                atlasWidth = width;
                atlasHeight = height;
            }
            Start();
            wake.Set();
        }

        public void Submit(Stains stains)
        {
            lock (gate)
                submitted = stains;
            Start();
            wake.Set();
        }

        public void Reset()
        {
            lock (gate)
            {
                submitted = null;
                resetRequested = true;
            }
            latest = null;
            wake.Set();
        }

        private void Start()
        {
            if (thread != null)
                return;
            thread = new Thread(Run) { IsBackground = true, Name = "UnityRTX stain pruning", Priority = System.Threading.ThreadPriority.BelowNormal };
            thread.Start();
        }

        private void Run()
        {
            var pruner = new UltrakillStainPruner();
            Stains current = null;
            var sincePublish = Stopwatch.StartNew();
            bool unpublished = false;
            while (true)
            {
                Stains next;
                bool reset;
                bool[] newAtlas;
                int width, height;
                lock (gate)
                {
                    next = submitted;
                    submitted = null;
                    reset = resetRequested;
                    resetRequested = false;
                    newAtlas = atlas;
                    atlas = null;
                    width = atlasWidth;
                    height = atlasHeight;
                }
                try
                {
                    if (newAtlas != null)
                    {
                        pruner.SetAtlas(newAtlas, width, height);
                        next = next ?? current;
                    }
                    if (reset)
                    {
                        pruner.Reset();
                        current = null;
                    }
                    if (next != null)
                    {
                        current = next;
                        pruner.Update(current.vertices, current.uvs, current.Count, 0);
                        Publish(pruner, current);
                        sincePublish.Restart();
                        unpublished = false;
                    }
                    if (current != null && pruner.Pending)
                    {
                        unpublished |= pruner.Continue(20);
                        if (unpublished && (!pruner.Pending || sincePublish.Elapsed.TotalMilliseconds >= PublishMilliseconds))
                        {
                            Publish(pruner, current);
                            sincePublish.Restart();
                            unpublished = false;
                        }
                        continue;
                    }
                    if (unpublished && current != null)
                    {
                        Publish(pruner, current);
                        unpublished = false;
                    }
                }
                catch (Exception e)
                {
                    // A failed test leaves every stain drawn: never fewer than the game shows.
                    UnityEngine.Debug.LogWarning($"[UltrakillStainPruning] {e.GetType().Name}: {e.Message}");
                    pruner.Reset();
                    current = null;
                    latest = null;
                }
                wake.WaitOne();
            }
        }

        private void Publish(UltrakillStainPruner pruner, Stains stains)
        {
            var kept = new bool[stains.Count];
            for (int i = 0; i < kept.Length; i++)
                kept[i] = pruner.IsKept(i);
            latest = new Result { stains = stains, kept = kept, hidden = pruner.Hidden, testing = pruner.Pending };
        }
    }
}
