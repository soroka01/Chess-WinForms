// Settings file tests: atomic write, refusing to overwrite a file that could not be read, line breaks in keys and values.
// They use a file in a temporary folder (Settings.PathOverride), never the real %APPDATA%\ChessGame\settings.ini.
// Build & run:  tests\run-settings-tests.bat
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ChessGame;

static class SettingsTests
{
    static string dir;
    static string file;

    static string[] Lines()
    {
        return File.ReadAllLines(file, Encoding.UTF8);
    }

    static void Fresh()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Settings.ResetForTest();
    }

    static void TestFirstRunAndAtomicWrite()
    {
        Console.WriteLine("first run, atomic replacement");
        Fresh();
        TestKit.Check(!File.Exists(file), "no file on the first run");
        Settings.Set("a", "1");
        Settings.Save();
        TestKit.Check(File.Exists(file) && Array.IndexOf(Lines(), "a=1") >= 0, "a missing file is normal: the first Save creates it");
        TestKit.Check(!Settings.UnreadableForTest, "a missing file is not 'unreadable'");

        Settings.Set("b", "2");
        Settings.Set("a", "3");
        Settings.Save();
        string[] lines = Lines();
        TestKit.Check(lines.Length == 2 && Array.IndexOf(lines, "a=3") >= 0 && Array.IndexOf(lines, "b=2") >= 0, "the file was replaced as a whole: " + string.Join("|", lines));
        TestKit.Check(Directory.GetFiles(dir).Length == 1, "no temporary file stays behind: " + string.Join(",", Directory.GetFiles(dir)));

        // a stale temporary file of an earlier crash is simply overwritten
        File.WriteAllText(file + ".tmp", "garbage");
        Settings.Set("c", "4");
        Settings.Save();
        TestKit.Check(Directory.GetFiles(dir).Length == 1 && Array.IndexOf(Lines(), "c=4") >= 0, "a leftover temporary file does not matter");

        // the settings come back after a restart
        Settings.ResetForTest();
        TestKit.Check(Settings.Get("a", "") == "3" && Settings.GetInt("c", 0) == 4, "values survive a reload");
    }

    static void TestUnreadableFileIsNotOverwritten()
    {
        Console.WriteLine("a file that cannot be read is not overwritten");
        Fresh();
        File.WriteAllLines(file, new string[] { "lankey=SECRETKEY", "theme=dark" }, new UTF8Encoding(false));
        Settings.ResetForTest();
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // another process / an antivirus holds it
        {
            TestKit.Check(Settings.Get("theme", "none") == "none", "while locked nothing is known");
            TestKit.Check(Settings.UnreadableForTest, "the failed read is remembered");
            Settings.Set("new", "1");
            Settings.Save();   // must neither throw nor touch the file
        }
        string[] lines = Lines();
        TestKit.Check(lines.Length == 2 && lines[0] == "lankey=SECRETKEY" && lines[1] == "theme=dark", "the locked file keeps its content: " + string.Join("|", lines));

        Settings.Save();   // readable now: the old content is read first, then what was set meanwhile is added
        lines = Lines();
        TestKit.Check(Array.IndexOf(lines, "lankey=SECRETKEY") >= 0 && Array.IndexOf(lines, "theme=dark") >= 0 && Array.IndexOf(lines, "new=1") >= 0,
            "after the lock is gone nothing is lost and the new key is added: " + string.Join("|", lines));
        TestKit.Check(!Settings.UnreadableForTest, "readable again");
    }

    static void TestLineBreaks()
    {
        Console.WriteLine("line breaks cannot add keys");
        Fresh();
        Settings.Set("update.tag", "v1.0\nevil=1\r\nlankey=stolen");
        Settings.Set("bad\nkey", "x");
        Settings.Set("", "empty key");
        Settings.Set("a=b", "equals in a key");
        TestKit.Check(Settings.Get("update.tag", "") == "v1.0evil=1lankey=stolen", "line breaks are removed from the value: " + Settings.Get("update.tag", ""));
        TestKit.Check(Settings.Get("badkey", "") == "x", "and from the key");
        Settings.Save();
        string[] lines = Lines();
        TestKit.Check(lines.Length == 2, "two lines in the file: " + string.Join("|", lines));
        Settings.ResetForTest();
        TestKit.Check(Settings.Get("evil", "none") == "none" && Settings.Get("lankey", "none") == "none", "no key was smuggled in");
        TestKit.Check(Settings.Get("update.tag", "") == "v1.0evil=1lankey=stolen", "the value is intact after a reload");
        TestKit.Check(Settings.Get("", "none") == "none" && Settings.Get("a=b", "none") == "none", "empty keys and keys with '=' are refused");
    }

    static void TestPersistOff()
    {
        Console.WriteLine("Persist = false");
        Fresh();
        bool saved = Settings.Persist;
        Settings.Persist = false;
        try
        {
            Settings.Set("x", "1");
            Settings.Save();
            TestKit.Check(!File.Exists(file), "nothing is written");
            TestKit.Check(Settings.Get("x", "") == "1", "values still live in memory");
        }
        finally { Settings.Persist = saved; }
    }

    static void TestThreads()
    {
        Console.WriteLine("several threads");
        Fresh();
        List<System.Threading.Thread> ts = new List<System.Threading.Thread>();
        List<string> errors = new List<string>();
        for (int t = 0; t < 4; t++)
        {
            int id = t;
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < 200; i++)
                    {
                        Settings.Set("k" + id + "_" + (i % 20), i);
                        Settings.Get("k" + id, "");
                        if (i % 50 == 0) Settings.Save();
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex.GetType().Name); }
            });
            ts.Add(th);
            th.Start();
        }
        foreach (System.Threading.Thread th in ts) th.Join();
        Settings.Save();
        TestKit.Check(errors.Count == 0, "no exception from concurrent use: " + string.Join(",", errors.ToArray()));
        TestKit.Check(Lines().Length == 80, "all keys are in the file: " + Lines().Length);
    }

    /// <summary>Run as the "settings" group of tests\NetTests.cs (that build contains every source file).</summary>
    public static void Run()
    {
        Console.WriteLine("settings file");
        dir = Path.Combine(Path.GetTempPath(), "chess-settings-test-" + Guid.NewGuid().ToString("N"));
        file = Path.Combine(dir, "settings.ini");
        bool persist = Settings.Persist;
        Settings.PathOverride = file;
        Settings.Persist = true;
        try
        {
            TestFirstRunAndAtomicWrite();
            TestUnreadableFileIsNotOverwritten();
            TestLineBreaks();
            TestPersistOff();
            TestThreads();
        }
        finally
        {
            Settings.PathOverride = null;
            Settings.Persist = persist;
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
