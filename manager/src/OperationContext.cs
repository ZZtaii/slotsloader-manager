using System;
using System.Threading;

namespace SlotsInstaller
{
    internal sealed class OperationProgress
    {
        internal string Stage;
        internal long Completed, Total;
        internal bool CanCancel;
    }

    // One synchronous operation runs on each worker. UI callbacks are marshalled by the caller.
    internal static class OperationContext
    {
        [ThreadStatic] private static State current;
        private sealed class State
        {
            internal CancellationToken Token;
            internal Action<OperationProgress> Progress;
            internal bool CanCancel = true;
            internal long LastReport;
            internal string LastStage;
        }
        internal static CancellationToken Token { get { return current == null || !current.CanCancel ? CancellationToken.None : current.Token; } }
        internal static Action<OperationProgress> Progress { get { return current == null ? null : current.Progress; } }
        internal static IDisposable Begin(CancellationToken token, Action<OperationProgress> progress)
        {
            State previous = current;
            current = new State { Token = token, Progress = progress };
            return new Scope(delegate { current = previous; });
        }
        internal static void ThrowIfCancellationRequested() { Token.ThrowIfCancellationRequested(); }
        internal static void Report(string stage, long completed, long total)
        {
            State state = current;
            if (state == null || state.Progress == null) return;
            long now = DateTime.UtcNow.Ticks;
            if (stage == state.LastStage && completed != total && now - state.LastReport < TimeSpan.TicksPerMillisecond * 100) return;
            state.LastReport = now; state.LastStage = stage;
            state.Progress(new OperationProgress { Stage = stage, Completed = completed, Total = total, CanCancel = state.CanCancel });
        }
        internal static IDisposable NonCancellable()
        {
            State state = current;
            if (state == null) return new Scope(delegate { });
            bool previous = state.CanCancel;
            state.CanCancel = false;
            Report("Finishing installation", 0, 0);
            return new Scope(delegate { state.CanCancel = previous; });
        }
        private sealed class Scope : IDisposable
        {
            private Action finished;
            internal Scope(Action action) { finished = action; }
            public void Dispose() { Action action = finished; finished = null; if (action != null) action(); }
        }
    }
}
