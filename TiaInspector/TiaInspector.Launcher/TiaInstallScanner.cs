using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TiaInspector.Launcher
{
    internal sealed class TiaInstall
    {
        public int Major;
        public string InstallDir;
        public string PublicApiDir;
        public bool HasOpenness;
        public string WorkerExe;       // 이 버전용 TiaInspector.Vnn.exe (없으면 null)
        public List<int> RunningPids = new List<int>();

        public string Label { get { return "V" + Major; } }
        public bool IsRunning { get { return RunningPids.Count > 0; } }
        public bool Launchable { get { return HasOpenness && WorkerExe != null; } }
    }

    /// <summary>
    /// 설치된 TIA Portal 을 찾는다. Openness 를 로드하지 않는다 — 런처가
    /// Openness 를 건드리는 순간 .NET 이 그 버전을 프로세스에 묶어 버려서
    /// 다른 버전 워커를 띄우는 의미가 사라진다.
    ///
    /// 레지스트리(HKLM\SOFTWARE\Siemens\Automation\Openness)에는 버전 하위키만
    /// 있고 InstallPath 가 비어 있는 설치가 흔하다(실측). 그래서 파일시스템
    /// 스캔이 주 경로이고 레지스트리는 보조다.
    /// </summary>
    internal static class TiaInstallScanner
    {
        const int AssemblyLayoutSplitFrom = 21;

        static readonly string[] BaseDirs =
        {
            @"C:\Program Files\Siemens\Automation",
            @"C:\Program Files (x86)\Siemens\Automation"
        };

        public static List<TiaInstall> Scan()
        {
            var byMajor = new Dictionary<int, TiaInstall>();

            foreach (string baseDir in BaseDirs)
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (string dir in SafeDirs(baseDir))
                {
                    Match m = Regex.Match(Path.GetFileName(dir), @"^Portal V(\d+)",
                        RegexOptions.IgnoreCase);
                    if (!m.Success) continue;

                    int major = int.Parse(m.Groups[1].Value);
                    if (byMajor.ContainsKey(major)) continue;

                    string apiDir = PublicApiDir(dir, major);
                    byMajor[major] = new TiaInstall
                    {
                        Major = major,
                        InstallDir = dir,
                        PublicApiDir = apiDir,
                        HasOpenness = Directory.Exists(apiDir) && CoreDllExists(apiDir, major)
                    };
                }
            }

            // 레지스트리에만 있는 설치를 보강 (비표준 경로)
            foreach (int major in RegistryMajors())
                if (!byMajor.ContainsKey(major))
                    byMajor[major] = new TiaInstall { Major = major, HasOpenness = false };

            AttachRunningProcesses(byMajor);
            AttachWorkerExes(byMajor);

            var result = new List<TiaInstall>(byMajor.Values);
            result.Sort((a, b) => b.Major.CompareTo(a.Major));   // 최신 먼저
            return result;
        }

        static string PublicApiDir(string installDir, int major)
        {
            string baseDir = Path.Combine(installDir, "PublicAPI", "V" + major);
            return major >= AssemblyLayoutSplitFrom ? Path.Combine(baseDir, "net48") : baseDir;
        }

        static bool CoreDllExists(string apiDir, int major)
        {
            string core = major >= AssemblyLayoutSplitFrom
                ? "Siemens.Engineering.Base.dll"
                : "Siemens.Engineering.dll";
            return File.Exists(Path.Combine(apiDir, core));
        }

        static IEnumerable<string> SafeDirs(string parent)
        {
            try { return Directory.GetDirectories(parent); }
            catch { return new string[0]; }
        }

        static IEnumerable<int> RegistryMajors()
        {
            var found = new List<int>();
            string[] keys =
            {
                @"SOFTWARE\Siemens\Automation\Openness",
                @"SOFTWARE\WOW6432Node\Siemens\Automation\Openness"
            };

            foreach (string keyPath in keys)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath))
                    {
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            int major;
                            string head = sub.Split('.')[0];
                            if (int.TryParse(head, out major) && !found.Contains(major))
                                found.Add(major);
                        }
                    }
                }
                catch { }
            }
            return found;
        }

        /// <summary>
        /// 떠 있는 TIA Portal 을 실행 파일 경로로 판별한다 — Openness 없이
        /// 버전을 알 수 있는 유일한 방법이다.
        /// </summary>
        static void AttachRunningProcesses(Dictionary<int, TiaInstall> byMajor)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName("Siemens.Automation.Portal"); }
            catch { return; }

            foreach (Process p in processes)
            {
                string path = null;
                try { path = p.MainModule != null ? p.MainModule.FileName : null; }
                catch { /* 권한 부족 — 건너뛴다 */ }
                if (path == null) continue;

                Match m = Regex.Match(path, @"Portal V(\d+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                int major = int.Parse(m.Groups[1].Value);
                TiaInstall install;
                if (!byMajor.TryGetValue(major, out install))
                {
                    install = new TiaInstall { Major = major };
                    byMajor[major] = install;
                }
                install.RunningPids.Add(p.Id);
            }
        }

        static void AttachWorkerExes(Dictionary<int, TiaInstall> byMajor)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (TiaInstall install in byMajor.Values)
            {
                string exe = Path.Combine(dir, "TiaInspector.V" + install.Major + ".exe");
                if (File.Exists(exe)) install.WorkerExe = exe;
            }
        }
    }
}
