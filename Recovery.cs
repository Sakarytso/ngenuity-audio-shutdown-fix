using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// Helpers embedded in NGENUITY for output binding retries and shutdown checks.
namespace NGENUITY.LocalRepair
{
    internal static class SessionExit
    {
        private const int SmShuttingDown = 0x2000;
        private static int exiting;

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern int GetSystemMetrics(int index);

        internal static bool IsStopping()
        {
            return Volatile.Read(ref exiting) != 0 || GetSystemMetrics(SmShuttingDown) != 0;
        }

        internal static bool BeginExit()
        {
            return Interlocked.Exchange(ref exiting, 1) == 0;
        }

        internal static Task<bool> DeclinedStart()
        {
            return Task.FromResult(false);
        }
    }

    internal sealed class BindingRetry
    {
        private const string HeadsetName = "HyperX Cloud III Wireless";
        private const int MaxRetries = 3;
        private readonly SemaphoreSlim clientLock;
        private readonly Func<bool> canSend;
        private readonly Action<string> send;
        private readonly Action<string> log;
        private readonly string action;
        private readonly object gate = new object();
        private string target;
        private object request;
        private int attempts;
        private bool sentRetry;
        private Timer timer;

        internal BindingRetry(SemaphoreSlim clientLock, Func<bool> canSend,
                              Action<string> send, Action<string> log, string action)
        {
            this.clientLock = clientLock;
            this.canSend = canSend;
            this.send = send;
            this.log = log;
            this.action = action;
        }

        internal void Track(string destination)
        {
            lock (gate)
            {
                ClearLocked();
                if (destination != null && destination.IndexOf(HeadsetName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    target = destination;
                    request = new object();
                }
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                ClearLocked();
            }
        }

        private void ClearLocked()
        {
            if (timer != null)
                timer.Dispose();
            timer = null;
            request = null;
            target = null;
            attempts = 0;
            sentRetry = false;
        }

        internal void Reply(bool okay, string error)
        {
            lock (gate)
            {
                if (target == null)
                    return;
                if (okay)
                {
                    if (sentRetry)
                        log("[LocalAudioPatch] " + action + " succeeded after retry.");
                    ClearLocked();
                    return;
                }
                // The native engine reports SetOutputDeviceName in both main and chat errors.
                if (SessionExit.IsStopping() || timer != null || error == null ||
                    !error.EndsWith("SetOutputDeviceName failed with code -8", StringComparison.Ordinal))
                    return;
                if (attempts >= MaxRetries)
                {
                    log("[LocalAudioPatch] " + action + " still failed after three retries.");
                    ClearLocked();
                    return;
                }
                attempts++;
                int delayMs = attempts == 1 ? 300 : attempts == 2 ? 1000 : 2000;
                timer = new Timer(Resend, request, delayMs, Timeout.Infinite);
            }
        }

        private void Resend(object requestKey)
        {
            bool acquired = false;
            try
            {
                if (!SessionExit.IsStopping())
                    acquired = clientLock.Wait(2500);

                string destination;
                int attempt;
                lock (gate)
                {
                    if (!ReferenceEquals(requestKey, request) || timer == null)
                        return;
                    if (!acquired || SessionExit.IsStopping() || !canSend())
                    {
                        ClearLocked();
                        return;
                    }
                    timer.Dispose();
                    timer = null;
                    destination = target;
                    attempt = attempts;
                    sentRetry = true;
                }
                log("[LocalAudioPatch] Retrying " + action + " (attempt " + attempt + ").");
                if (!SessionExit.IsStopping() && canSend())
                    send(destination);
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    if (ReferenceEquals(requestKey, request))
                        ClearLocked();
                }
                log("[LocalAudioPatch] " + action + " retry could not send: " + error.GetType().Name);
            }
            finally
            {
                if (acquired)
                    clientLock.Release();
            }
        }
    }
}
