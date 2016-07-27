using System;
using System.Threading;
using System.Threading.Tasks;

namespace AllocTracker
{
    //
    // Pretend this is a real memory allocator. :)
    //
    public static class Allocator
    {
        // Tracks allocation per-thread
        [ThreadStatic]
        static long t_bytesAllocated;

        public static long CurrentThreadBytesAllocated => t_bytesAllocated;

        public static long Alloc(long size)
        {
            t_bytesAllocated += size;
            return size;
        }
    }

    public sealed class AllocationTracker
    {
        // The AllocationTracker for the current async control flow
        private static AsyncLocal<AllocationTracker> s_current = new AsyncLocal<AllocationTracker>(OnTrackerChanged);

        // Trackers can be nested.  The parent is whatever was current when this tracker was started.  If this tracker
        // is current then its parent is also considered current (since the parent is tracking a superset of the scope
        // of this tracker).
        private readonly AllocationTracker m_parent;

        private bool IsEqualToOrParentOf(AllocationTracker tracker) => tracker != null && (this == tracker || this.IsEqualToOrParentOf(tracker.m_parent));

        private bool IsCurrent => IsEqualToOrParentOf(s_current.Value);

        private AllocationTracker(AllocationTracker parent)
        {
            m_parent = parent;
        }

        // Create a new tracker for the current context
        public static AllocationTracker Start() => s_current.Value = new AllocationTracker(parent: s_current.Value);


        // How many bytes were already allocated on the current thread, the last time we switched AllocationTrackers?
        [ThreadStatic]
        private static long t_allocatedBytesAtLastTrackerChange;

        // How many bytes have been allocated on the current thread, since the last AllocationTracker switch?
        private static long BytesAllocatedSinceLastTrackerChange => Allocator.CurrentThreadBytesAllocated - t_allocatedBytesAtLastTrackerChange;

        // Total bytes allocated under this tracker, not counting any bytes recently allocated on threads where this
        // tracker is current.  Note that if an async method "forks," this mutable field may be accessed concurrently
        // from multiple threads, so we need to handle it with care.
        private long m_totalBytes_doNotAccessDirectly;
        private long TotalBytes => Volatile.Read(ref m_totalBytes_doNotAccessDirectly);
        private void AddTotalBytes(long addend)
        {
            if (addend != 0)
            {
                Interlocked.Add(ref m_totalBytes_doNotAccessDirectly, addend);
                m_parent?.AddTotalBytes(addend);
            }
        }

        // Gets the number of bytes allocated under this tracker so far, including any bytes allocated on the current
        // thread if this tracker is current.  Does not include bytes allocated on other threads where this same tracker
        // is also current - those bytes will be added when the tracker is switched off of those threads.
        public long BytesAllocated => TotalBytes + (IsCurrent ? BytesAllocatedSinceLastTrackerChange : 0);


        private static void OnTrackerChanged(AsyncLocalValueChangedArgs<AllocationTracker> args)
        {
            args.PreviousValue?.AddTotalBytes(BytesAllocatedSinceLastTrackerChange);
            t_allocatedBytesAtLastTrackerChange = Allocator.CurrentThreadBytesAllocated;
        }
    }

    class Program
    {

        static async Task DoStuff1()
        {
            var tracker = AllocationTracker.Start();

            var manualCount = await DoStuff2();

            Console.WriteLine($"Manual count: {manualCount}, Tracker count: {tracker.BytesAllocated}");
        }

        static async Task<long> DoStuff2()
        {
            var manualCount = Allocator.Alloc(100000);

            var innerTracker = AllocationTracker.Start();
            var innerManualCount = await DoStuff3();

            Console.WriteLine($"Manual count: {innerManualCount}, Tracker count: {innerTracker.BytesAllocated}");

            manualCount += innerManualCount;

            return manualCount;
        }

        static async Task<long> DoStuff3()
        {
            await Task.Yield();
            return Allocator.Alloc(1000000);
        }



        static void Main(string[] args)
        {
            DoStuff1().Wait();
        }
    }
}
