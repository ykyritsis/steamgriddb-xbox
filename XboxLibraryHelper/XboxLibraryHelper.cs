// Companion program for the SteamGridDB for Xbox widget.
//
// The Xbox app keeps its custom library (custom games and apps) in memory in a background process, XboxPcAppFT.exe,
// and saves that copy over the manifest files. A change to those files only survives if the Xbox app AND that
// background process are restarted around it. The widget runs in a sandbox and is not allowed to end those
// processes, so this small full-trust program does it:
//
//   1. the widget writes the new manifest(s) into <its LocalState>\XboxLibraryHelper\ (games.manifest / apps.manifest);
//   2. this program ends the Xbox app and XboxPcAppFT, copies the pending manifest(s) into place, and reopens the
//      Xbox app if it was open;
//   3. it writes result.txt there for the widget to read.
//
// It ends nothing but the Xbox app's own processes and writes nothing but those two manifest files.
// Kept to C# 5 so the compiler that ships with Windows (.NET Framework csc.exe) can build it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class XboxLibraryHelper
{
    private const string XboxPackageFamilyName = "Microsoft.GamingApp_8wekyb3d8bbwe";
    private const string XboxPackagePathMarker = "Microsoft.GamingApp_";
    private const string XboxAppUserModelId = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    // The app itself (new and legacy executables): if one is running, the Xbox app is "open"
    private static readonly string[] AppProcessNames = { "XboxPcAppCE", "XboxPcApp" };

    // Everything that has to be restarted for a library change to stick. XboxPcAppFT is restarted by Windows on demand.
    private static readonly string[] RestartProcessNames = { "XboxPcAppCE", "XboxPcApp", "XboxPcAppFT", "crashpad_handler" };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder name);

    private static int Main(string[] args)
    {
        string workFolder = null;

        try
        {
            // An explicit folder can be passed for testing outside the package
            workFolder = args.Length > 1 ? args[1] : GetWorkFolder();

            if (workFolder == null || !Directory.Exists(workFolder))
            {
                return 2;
            }

            string xboxLocalState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", XboxPackageFamilyName, "LocalState");

            var pending = new List<KeyValuePair<string, string>>();
            AddPending(pending, Path.Combine(workFolder, "games.manifest"),
                Path.Combine(xboxLocalState, "ThirdPartyLibraries", "CustomLibraryManagement", "CustomLibraryManagement.manifest"));
            AddPending(pending, Path.Combine(workFolder, "apps.manifest"),
                Path.Combine(xboxLocalState, "CustomLibraryManagement", "CustomLibraryManagement.manifest"));

            if (pending.Count == 0)
            {
                WriteResult(workFolder, "error: nothing to apply");
                return 3;
            }

            bool wasOpen = IsRunning(AppProcessNames);

            // In Xbox full screen experience Windows reopens the Xbox app within a second of it closing, so it can
            // come back between the stop and the copy and load the old library: check and repeat if it did.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                StopProcesses(RestartProcessNames);

                foreach (KeyValuePair<string, string> item in pending)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Value));
                    File.Copy(item.Key, item.Value, true);
                }

                Thread.Sleep(400);

                if (!IsRunning(AppProcessNames))
                {
                    break;
                }

                wasOpen = true;
            }

            bool reopened = IsRunning(AppProcessNames);

            if (wasOpen && !reopened)
            {
                Process.Start("explorer.exe", "shell:AppsFolder\\" + XboxAppUserModelId);
                reopened = true;
            }

            foreach (KeyValuePair<string, string> item in pending)
            {
                File.Delete(item.Key);
            }

            WriteResult(workFolder, "ok;reopened=" + (reopened ? "1" : "0"));
            return 0;
        }
        catch (Exception ex)
        {
            if (workFolder != null)
            {
                try { WriteResult(workFolder, "error: " + ex.Message); } catch { }
            }

            return 1;
        }
    }

    private static void AddPending(List<KeyValuePair<string, string>> pending, string source, string target)
    {
        if (File.Exists(source))
        {
            pending.Add(new KeyValuePair<string, string>(source, target));
        }
    }

    private static string GetWorkFolder()
    {
        int length = 0;
        GetCurrentPackageFamilyName(ref length, null);

        if (length <= 0)
        {
            return null;
        }

        var name = new StringBuilder(length);

        if (GetCurrentPackageFamilyName(ref length, name) != 0)
        {
            return null;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", name.ToString(), "LocalState", "XboxLibraryHelper");
    }

    private static void WriteResult(string workFolder, string text)
    {
        File.WriteAllText(Path.Combine(workFolder, "result.txt"), text);
    }

    private static bool IsRunning(string[] names)
    {
        return FindProcesses(names).Count > 0;
    }

    private static List<Process> FindProcesses(string[] names)
    {
        var found = new List<Process>();

        foreach (string name in names)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                if (BelongsToXboxApp(process, name))
                {
                    found.Add(process);
                }
            }
        }

        return found;
    }

    private static bool BelongsToXboxApp(Process process, string name)
    {
        try
        {
            return process.MainModule.FileName.IndexOf(XboxPackagePathMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            // Path not readable: trust the Xbox-specific names, but never a generic one like crashpad_handler
            return name.StartsWith("XboxPc", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void StopProcesses(string[] names)
    {
        List<Process> processes = FindProcesses(names);

        foreach (Process process in processes)
        {
            try { process.Kill(); } catch { }
        }

        foreach (Process process in processes)
        {
            try { process.WaitForExit(5000); } catch { }
        }
    }
}
