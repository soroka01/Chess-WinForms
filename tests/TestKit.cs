// Shared helpers of the suites that run inside one process with real message pumping:
// tests\RoomTests.cs, tests\LanRoomTests.cs, tests\NetTests.cs.
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

static class TestKit
{
    public static int Failures;

    static Form anchor;
    static WindowsFormsSynchronizationContext context;
    static int progress;

    public static void Check(bool ok, string what)
    {
        if (!ok) { Failures++; Console.WriteLine("  FAIL: " + what); }
    }

    /// <summary>Pumps messages until the condition holds or the time is up; returns the last value of the condition.</summary>
    public static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            progress++;
            Application.DoEvents();
            InstallContext();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Application.DoEvents();
        InstallContext();
        return condition();
    }

    public static void Pump(int ms)
    {
        WaitFor(delegate { return false; }, ms);
    }

    /// <summary>
    /// Application.Run does this in the real program. Application.DoEvents puts a plain context back afterwards, and objects that
    /// capture the current context when they are created (transports, fake lobbies) would then raise their events on a pool thread.
    /// Cheap to call: it only acts when the one shared context is not the current one.
    /// </summary>
    public static void InstallContext()
    {
        if (anchor == null)
        {
            anchor = new Form();
            Offscreen(anchor);
            anchor.Show();
            context = new WindowsFormsSynchronizationContext();
        }
        if (SynchronizationContext.Current != context) SynchronizationContext.SetSynchronizationContext(context);
    }

    /// <summary>Test windows must not appear on (or steal the focus from) the screen of whoever runs the tests.</summary>
    public static void Offscreen(Form f)
    {
        f.ShowInTaskbar = false;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new System.Drawing.Point(-6000, -6000);
    }

    /// <summary>
    /// First call of Main: an exception stops the run with its text instead of raising a "program has stopped working" dialog,
    /// and a UI thread that makes no progress for 45 s (a hang, or a modal dialog nobody answers) is reported with its stack.
    /// </summary>
    public static void Guard()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine("UNHANDLED EXCEPTION: " + e.ExceptionObject);
            Environment.Exit(3);
        };

        Thread main = Thread.CurrentThread;
        Thread dog = new Thread(delegate()
        {
            int last = -1, still = 0;
            while (true)
            {
                Thread.Sleep(1000);
                int now = progress;
                if (now != last) { last = now; still = 0; continue; }
                if (++still < 45) continue;
#pragma warning disable 618   // Suspend is the only way to read another thread's stack in .NET Framework
                main.Suspend();
                Console.WriteLine("WATCHDOG: the UI thread made no progress for " + still + " s. It is here:");
                Console.WriteLine(new StackTrace(main, true).ToString());
#pragma warning restore 618
                Environment.Exit(2);
            }
        });
        dog.IsBackground = true;
        dog.Start();
    }

    /// <summary>Last line of a suite: "ALL &lt;what&gt; PASSED" or the number of failures; the return value is the exit code.</summary>
    public static int Finish(string what)
    {
        Console.WriteLine();
        Console.WriteLine(Failures == 0 ? "ALL " + what + " PASSED" : Failures + " FAILURE(S)");
        return Failures == 0 ? 0 : 1;
    }
}
