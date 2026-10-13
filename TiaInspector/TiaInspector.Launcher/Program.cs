using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TiaInspector.Launcher
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            List<TiaInstall> installs = TiaInstallScanner.Scan();

            if (installs.Count == 0)
            {
                MessageBox.Show(
                    "설치된 TIA Portal 을 찾지 못했습니다.\n\n"
                    + @"C:\Program Files\Siemens\Automation\Portal V** 를 확인하세요.",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // --v20 / -v 20 / 20 처럼 버전을 주면 물어보지 않고 바로 띄운다.
            int requested;
            if (TryParseRequestedMajor(args, out requested))
            {
                foreach (TiaInstall t in installs)
                {
                    if (t.Major != requested) continue;
                    if (!t.Launchable) break;
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(t.WorkerExe) { UseShellExecute = false });
                    return;
                }
                MessageBox.Show(
                    "V" + requested + " 로 실행할 수 없습니다 (Openness 미설치이거나 해당 빌드 없음).",
                    "TiaInspector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Application.Run(new LauncherForm(installs));
        }

        static bool TryParseRequestedMajor(string[] args, out int major)
        {
            major = 0;
            if (args == null) return false;
            foreach (string raw in args)
            {
                string s = raw.TrimStart('-', '/').TrimStart('v', 'V');
                int parsed;
                if (int.TryParse(s, out parsed) && parsed >= 13 && parsed < 100)
                {
                    major = parsed;
                    return true;
                }
            }
            return false;
        }
    }
}
